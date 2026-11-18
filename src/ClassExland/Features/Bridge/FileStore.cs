using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClassExland.Contracts;

namespace ClassExland.Features.Bridge;

/// <summary>
/// 离线配置文件的统一存取：备份 → schema 校验 → 临时文件 + 原子替换；
/// 检测到 ClassIsland 正在运行时拒绝离线写。
/// </summary>
public sealed class FileStore
{
    private readonly IPluginPaths _paths;

    public FileStore(IPluginPaths paths) => _paths = paths;

    public string PathOf(string file) => _paths.Get(file);
    public bool Exists(string file) => File.Exists(_paths.Get(file));

    public string? ReadRaw(string file)
    {
        var path = _paths.Get(file);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public T? Read<T>(string file) where T : class
    {
        var raw = ReadRaw(file);
        return raw is null ? null : JsonSerializer.Deserialize<T>(raw, BridgeProtocol.Json);
    }

    public JsonDocument? ReadDocument(string file)
    {
        var raw = ReadRaw(file);
        return raw is null ? null : JsonDocument.Parse(raw);
    }

    /// <summary>写前备份，返回备份文件路径。</summary>
    public string? Backup(string file)
    {
        var source = _paths.Get(file);
        if (!File.Exists(source)) return null;
        Directory.CreateDirectory(_paths.BackupDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var target = Path.Combine(_paths.BackupDirectory, $"{Path.GetFileNameWithoutExtension(file)}.{stamp}{Path.GetExtension(file)}");
        File.Copy(source, target, overwrite: false);
        return target;
    }

    /// <summary>备份整个配置目录为 zip，返回备份文件路径。</summary>
    public string BackupAll(IEnumerable<string> files)
    {
        Directory.CreateDirectory(_paths.BackupDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(_paths.BackupDirectory, $"full-{stamp}.zip");
        using var zip = System.IO.Compression.ZipFile.Open(target, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var path = _paths.Get(file);
            if (File.Exists(path))
                zip.CreateEntryFromFile(path, file);
        }
        return target;
    }

    /// <summary>写文件：先校验 JSON，再原子替换。</summary>
    public void Write(string file, object value)
        => WriteRaw(file, JsonSerializer.Serialize(value, BridgeProtocol.Json));

    public void WriteRaw(string file, string json, bool validateJson = true)
    {
        ValidateSchema(file, json, validateJson);
        AssertHostNotRunning();
        WriteAtomic(_paths.Get(file), json);
    }

    public void Delete(string file)
    {
        AssertHostNotRunning();
        var path = _paths.Get(file);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>基础 schema 校验：JSON 语法有效、根为对象或数组、非空。各模块可在上层追加字段级校验。</summary>
    public static void ValidateSchema(string file, string json, bool validate = true)
    {
        if (!validate) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new CommandAbortedException(ExitCodes.Validation, $"{file}: 配置根节点必须是对象或数组");
        }
        catch (JsonException ex)
        {
            throw new CommandAbortedException(ExitCodes.Validation, $"{file}: JSON 无效 - {ex.Message}");
        }
    }

    public static void AssertHostNotRunning()
    {
        if (IsHostRunning())
            throw new CommandAbortedException(ExitCodes.ConfigBusy,
                "检测到 ClassIsland 正在运行，已拒绝离线写配置。请先退出 ClassIsland 后重试，或使用在线 IPC 命令。");
    }

    public static bool IsHostRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("ClassIsland").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    public static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid is null) return;
            // .NET 8：GetAccessControl/SetAccessControl 是 FileInfo 的扩展方法（FileSystemAclExtensions）
            var fi = new FileInfo(path);
            var acl = fi.GetAccessControl();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            fi.SetAccessControl(acl);
        }
        catch
        {
            // ACL 设置失败不阻止启动，但会在 doctor 中提示
        }
    }
}

/// <summary>用于携带稳定退出码提前中止命令执行。</summary>
public sealed class CommandAbortedException : Exception
{
    public CommandAbortedException(int exitCode, string message) : base(message) => ExitCode = exitCode;
    public int ExitCode { get; }
}
