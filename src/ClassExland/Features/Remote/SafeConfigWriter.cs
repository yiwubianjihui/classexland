using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassExland.Contracts;

namespace ClassExland.Features.Remote;

/// <summary>
/// 备份 → 校验 → 原子写 → 失败回滚 的底层文件操作。
/// 所有 ClassIsland 核心配置与插件配置的写入都必须经过这里。
/// </summary>
public sealed class SafeConfigWriter
{
    private readonly IPluginPaths _paths;

    public SafeConfigWriter(IPluginPaths paths)
    {
        _paths = paths;
    }

    public static bool IsValidJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            using var _ = JsonDocument.Parse(content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ComputeHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch
        {
            return "";
        }
    }

    /// <summary>整份备份单个文件到 BackupDirectory，返回备份路径。</summary>
    public string BackupFile(string path)
    {
        try
        {
            Directory.CreateDirectory(_paths.BackupDirectory);
            var name = Path.GetFileName(path);
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
            var backup = Path.Combine(_paths.BackupDirectory, $"{name}.{stamp}.bak");
            File.Copy(path, backup, overwrite: false);
            return backup;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>整份备份配置目录（含所有插件配置）。返回备份目录路径。</summary>
    public string BackupConfigTree()
    {
        try
        {
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
            var dest = Path.Combine(_paths.BackupDirectory, $"full-{stamp}");
            Directory.CreateDirectory(dest);
            CopyDirectory(_paths.ConfigDirectory, dest);
            return dest;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>校验内容后原子写入；失败时返回带原因的结果，绝不留下半截文件。</summary>
    public ApplyFileResult ApplyFile(string path, string content, bool validateJson = true)
    {
        var result = new ApplyFileResult { Path = path };
        try
        {
            if (validateJson && !IsValidJson(content))
            {
                result.Message = "内容不是合法 JSON，拒绝写入";
                return result;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            result.BackupPath = BackupFile(path);

            var tmp = path + ".cex-tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (validateJson && !IsValidJson(File.ReadAllText(tmp)))
            {
                TryDelete(tmp);
                result.Message = "临时文件校验失败，已放弃写入";
                return result;
            }

            File.Move(tmp, path, overwrite: true);
            result.Ok = true;
            result.Message = "ok";
            return result;
        }
        catch (Exception ex)
        {
            result.Message = $"{ex.GetType().Name}: {ex.Message}";
            return result;
        }
    }

    /// <summary>从备份回滚所有已应用文件；返回是否全部成功。</summary>
    public bool RollbackAll(IEnumerable<ApplyFileResult> applied)
    {
        var allOk = true;
        foreach (var item in applied)
        {
            try
            {
                if (string.IsNullOrEmpty(item.BackupPath) || !File.Exists(item.BackupPath))
                {
                    // 没有备份（原本不存在的新文件）→ 直接删除即可。
                    TryDelete(item.Path);
                    continue;
                }
                var dir = Path.GetDirectoryName(item.Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(item.BackupPath, item.Path, overwrite: true);
            }
            catch
            {
                allOk = false;
            }
        }
        return allOk;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            try { File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true); } catch { }
        }
        foreach (var sub in Directory.GetDirectories(source))
        {
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
        }
    }
}
