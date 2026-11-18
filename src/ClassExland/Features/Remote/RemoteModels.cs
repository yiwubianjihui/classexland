using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassExland.Features.Remote;

/// <summary>
/// 服务器返回的清单。body 原文用于 HMAC 校验；Version/ETag 用于避免重复应用。
/// </summary>
public sealed class RemoteManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("etag")]
    public string ETag { get; set; } = "";

    [JsonPropertyName("snapshot")]
    public RemoteSnapshot? Snapshot { get; set; }

    [JsonPropertyName("commands")]
    public List<RemoteCommand> Commands { get; set; } = new();

    /// <summary>可选：对响应原文做 HMAC-SHA256 后的十六进制串。</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = "";
}

/// <summary>完整配置快照：插件配置文件 + ClassIsland 核心配置。</summary>
public sealed class RemoteSnapshot
{
    /// <summary>键为插件配置文件名（如 layout.json / duty.json），值为完整 JSON 内容。</summary>
    [JsonPropertyName("pluginConfigs")]
    public Dictionary<string, JsonElement> PluginConfigs { get; set; } = new();

    [JsonPropertyName("core")]
    public CoreConfigSnapshot? Core { get; set; }
}

public sealed class CoreConfigSnapshot
{
    /// <summary>Settings.json 的新内容。</summary>
    [JsonPropertyName("settingsJson")]
    public JsonElement SettingsJson { get; set; }

    /// <summary>Settings.json 在生成快照时的哈希（用于冲突检测）；为空表示不校验。</summary>
    [JsonPropertyName("settingsBaseHash")]
    public string SettingsBaseHash { get; set; } = "";

    /// <summary>键为档案文件名（如 YBH.json），值为新内容与生成时哈希。</summary>
    [JsonPropertyName("profiles")]
    public Dictionary<string, CoreProfileEntry> Profiles { get; set; } = new();
}

public sealed class CoreProfileEntry
{
    [JsonPropertyName("json")]
    public JsonElement Json { get; set; }

    [JsonPropertyName("baseHash")]
    public string BaseHash { get; set; } = "";
}

/// <summary>远程指令：映射到 ICommandDispatcher 的 CommandRequest，与 CLI 共用命令层。</summary>
public sealed class RemoteCommand
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("args")]
    public Dictionary<string, string?> Args { get; set; } = new();

    [JsonPropertyName("apply")]
    public bool Apply { get; set; } = true;

    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; } = false;
}

public sealed class SyncReport
{
    public bool Ok { get; set; }
    public string Version { get; set; } = "";
    public string ETag { get; set; } = "";
    public List<string> Notes { get; } = new();
    public List<string> RolledBack { get; } = new();

    public void Add(string note) => Notes.Add(note);
}

public sealed class ApplyFileResult
{
    public bool Ok { get; set; }
    public string Path { get; set; } = "";
    public string? BackupPath { get; set; }
    public string Message { get; set; } = "";
    public bool ConflictKeptLocal { get; set; }
}
