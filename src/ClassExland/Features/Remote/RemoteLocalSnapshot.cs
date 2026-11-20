using System.Security.Cryptography;
using System.Text;
using ClassExland.Contracts;

namespace ClassExland.Features.Remote;

/// <summary>
/// 本地配置快照的指纹与原文读取。只处理服务端白名单里的四个插件配置，
/// 绝不碰 remote.json / remote-state.json / remote.disabled。
/// </summary>
public static class RemoteLocalSnapshot
{
    public static readonly string[] FileNames =
    {
        ConfigFiles.Bell, ConfigFiles.Duty, ConfigFiles.Activity, ConfigFiles.Layout
    };

    /// <summary>各文件原文（UTF-8）的 SHA-256；文件不存在时不参与比较。</summary>
    public static Dictionary<string, string> HashAll(IPluginPaths paths)
        => HashDirectory(paths.ConfigDirectory);

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
