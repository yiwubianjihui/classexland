using System.Security.Cryptography;
using System.Text;
using ClassExland.Contracts;

namespace ClassExland.Features.Remote;

/// <summary>
/// 本地配置快照的指纹与原文读取。只处理服务端白名单里的六个插件配置，
/// 以及纳入同步的核心配置（Settings.json / Profiles / ComponentLayouts）。
/// 绝不碰 remote.json / remote-state.json / remote.disabled / bridge.json 等同步自身文件。
///
/// 指纹 key 分区：插件配置用单层文件名；核心配置用 <c>core:</c> 前缀
/// （core:Settings.json / core:profiles/YBH.json / core:layouts/Default.json），
/// 避免与插件配置同名文件互相污染（如 Default.json）。
/// </summary>
public static class RemoteLocalSnapshot
{
    /// <summary>插件配置白名单：六个单层文件名。</summary>
    public static readonly string[] FileNames =
    {
        ConfigFiles.Bell, ConfigFiles.Duty, ConfigFiles.Activity, ConfigFiles.Layout,
        "bell-options.json", ConfigFiles.Lucky
    };

    public const string CoreSettingsKey = "core:Settings.json";
    public const string CoreProfilesPrefix = "core:profiles/";
    public const string CoreLayoutsPrefix = "core:layouts/";

    public static readonly string[] CoreProfileFileNames = { "YBH.json" };
    public static readonly string[] CoreLayoutFileNames = { "Default.json", "offclass.json" };

    /// <summary>核心配置原文与本地哈希（哈希 = 文件字节的 SHA-256，与 baseHash 语义一致）。</summary>
    public sealed record CoreFileData(string Text, string Hash);

    /// <summary>插件配置 + 核心配置的全部指纹；核心目录找不到时只算插件配置。</summary>
    public static Dictionary<string, string> HashAll(IPluginPaths paths)
    {
        var result = HashDirectory(paths.ConfigDirectory);
        var dataDirectory = new CorePaths(paths).DataDirectory;
        if (!string.IsNullOrEmpty(dataDirectory))
            foreach (var pair in HashCore(dataDirectory)) result[pair.Key] = pair.Value;
        return result;
    }

    public static Dictionary<string, string> HashDirectory(string configDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in FileNames)
        {
            var path = Path.Combine(configDirectory, name);
            if (!File.Exists(path)) continue;
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                result[name] = Hash(text);
            }
            catch
            {
                // 单个文件读失败不影响其余文件；这次就不把它算作变更。
            }
        }
        return result;
    }

    /// <summary>核心配置的指纹，key 带命名空间前缀，与上传/清单的 baseHash 同源。</summary>
    public static Dictionary<string, string> HashCore(string dataDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in ReadCore(dataDirectory)) result[pair.Key] = pair.Value.Hash;
        return result;
    }

    /// <summary>核心配置原文与哈希，供上传的 core 字段使用；文件不存在则不纳入。</summary>
    public static Dictionary<string, CoreFileData> ReadCore(string dataDirectory)
    {
        var result = new Dictionary<string, CoreFileData>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(dataDirectory)) return result;

        AddCoreFile(result, CoreSettingsKey, Path.Combine(dataDirectory, "Settings.json"));
        foreach (var name in CoreProfileFileNames)
            AddCoreFile(result, CoreProfilesPrefix + name, Path.Combine(dataDirectory, "Profiles", name));
        foreach (var name in CoreLayoutFileNames)
            AddCoreFile(result, CoreLayoutsPrefix + name,
                Path.Combine(dataDirectory, "Config", "ComponentLayouts", name));
        return result;
    }

    /// <summary>快照涉及的所有本地路径，用于本地脏时的即时备份。</summary>
    public static IReadOnlyList<string> AllLocalPaths(IPluginPaths paths)
    {
        var list = FileNames.Select(paths.Get).ToList();
        var dataDirectory = new CorePaths(paths).DataDirectory;
        if (!string.IsNullOrEmpty(dataDirectory))
        {
            list.Add(Path.Combine(dataDirectory, "Settings.json"));
            list.AddRange(CoreProfileFileNames.Select(n => Path.Combine(dataDirectory, "Profiles", n)));
            list.AddRange(CoreLayoutFileNames.Select(n =>
                Path.Combine(dataDirectory, "Config", "ComponentLayouts", n)));
        }
        return list;
    }

    /// <summary>上传用的原文，保持文件里的大小写与空白原样（PHP 端只做透传）。</summary>
    public static Dictionary<string, string> ReadAll(IPluginPaths paths)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in FileNames)
        {
            var path = Path.Combine(paths.ConfigDirectory, name);
            if (!File.Exists(path)) continue;
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(text)) result[name] = text;
            }
            catch
            {
                // 同上。
            }
        }
        return result;
    }

    public static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void AddCoreFile(Dictionary<string, CoreFileData> result, string key, string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrWhiteSpace(text)) return;
            result[key] = new CoreFileData(text, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch
        {
            // 单个文件读失败跳过，不影响其余核心配置。
        }
    }

    /// <summary>两份指纹是否有差异（新增、删除、内容变化都算）。</summary>
    public static bool HasChanges(IReadOnlyDictionary<string, string> baseline,
                                  IReadOnlyDictionary<string, string> current)
    {
        if (baseline.Count != current.Count) return true;
        foreach (var pair in current)
        {
            if (!baseline.TryGetValue(pair.Key, out var old) ||
                !string.Equals(old, pair.Value, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 本地是否相对「上次同步基线」变脏。没有基线时视为干净，避免首次安装/升级后
    /// 因为还不知道基线而永远拒绝下发服务端版本。
    /// </summary>
    public static bool IsLocalDirty(IReadOnlyDictionary<string, string> baseline,
                                    IReadOnlyDictionary<string, string> current)
        => baseline.Count > 0 && HasChanges(baseline, current);

    /// <summary>给人看的变更摘要：新增 / 修改 / 删除。</summary>
    public static string DescribeChanges(IReadOnlyDictionary<string, string> baseline,
                                         IReadOnlyDictionary<string, string> current)
    {
        var parts = new List<string>();
        foreach (var pair in current)
        {
            if (!baseline.ContainsKey(pair.Key)) parts.Add(pair.Key + " 新增");
            else if (!string.Equals(baseline[pair.Key], pair.Value, StringComparison.Ordinal))
                parts.Add(pair.Key + " 修改");
        }
        foreach (var key in baseline.Keys)
            if (!current.ContainsKey(key)) parts.Add(key + " 删除");
        return parts.Count == 0 ? "无变化" : string.Join("、", parts);
    }
}
