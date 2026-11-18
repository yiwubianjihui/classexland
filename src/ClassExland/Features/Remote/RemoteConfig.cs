using System.Text.Json;
using System.Text.Json.Serialization;
using ClassExland.Contracts;

namespace ClassExland.Features.Remote;

/// <summary>
/// 远程同步的外部配置（remote.json）。服务器地址与令牌只在这里出现，绝不写进代码。
/// 默认整体关闭，避免误配置导致对外请求或核心配置被改。
/// </summary>
public sealed class RemoteConfig
{
    /// <summary>一键停用总开关。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>服务器根地址，必须是 https。</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>清单路径（相对 ServerUrl）。</summary>
    public string ManifestPath { get; set; } = "/config/manifest.json";

    /// <summary>Bearer 令牌，不写进代码。</summary>
    public string BearerToken { get; set; } = "";

    /// <summary>可选 HMAC-SHA256 签名密钥；留空则不校验签名。</summary>
    public string HmacSecret { get; set; } = "";

    /// <summary>单次请求超时（秒）。</summary>
    public int FetchTimeoutSeconds { get; set; } = 15;

    /// <summary>是否应用完整配置快照。快照包含插件配置。</summary>
    public bool EnableSnapshotSync { get; set; } = true;

    /// <summary>是否同步 ClassIsland 核心配置（Settings.json / Profiles）。高风险，需显式开启。</summary>
    public bool EnableCoreConfigSync { get; set; } = false;

    /// <summary>是否执行远程指令队列（经 ICommandDispatcher，与 CLI 共用命令层）。</summary>
    public bool EnableCommandQueue { get; set; } = true;

    public LoopbackWebConfig Loopback { get; set; } = new();

    public static RemoteConfig Sanitize(RemoteConfig? raw)
    {
        var c = raw ?? new RemoteConfig();
        c.ServerUrl = (c.ServerUrl ?? "").Trim().TrimEnd('/');
        c.ManifestPath = string.IsNullOrWhiteSpace(c.ManifestPath) ? "/config/manifest.json" : c.ManifestPath.Trim();
        if (!c.ManifestPath.StartsWith('/')) c.ManifestPath = "/" + c.ManifestPath;
        c.BearerToken ??= "";
        c.HmacSecret ??= "";
        c.FetchTimeoutSeconds = c.FetchTimeoutSeconds is < 1 or > 120 ? 15 : c.FetchTimeoutSeconds;
        c.Loopback ??= new LoopbackWebConfig();
        c.Loopback = LoopbackWebConfig.Sanitize(c.Loopback);
        return c;
    }

    /// <summary>配置是否足以发起同步：启用 + https 地址 + 令牌齐全。</summary>
    public bool CanSync()
        => Enabled
           && ServerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(BearerToken);
}

public sealed class LoopbackWebConfig
{
    /// <summary>Loopback 管理页开关。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>仅监听 127.0.0.1 的端口。绝不绑定 0.0.0.0。</summary>
    public int Port { get; set; } = 18765;

    /// <summary>访问管理页所需的 Bearer 令牌。</summary>
    public string Token { get; set; } = "";

    public static LoopbackWebConfig Sanitize(LoopbackWebConfig? raw)
    {
        var c = raw ?? new LoopbackWebConfig();
        c.Port = c.Port is < 1024 or > 65535 ? 18765 : c.Port;
        c.Token ??= "";
        return c;
    }
}

public sealed class RemoteConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public RemoteConfigStore(IPluginPaths paths)
    {
        _path = paths.Get(ConfigFiles.Remote);
    }

    public RemoteConfig Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                // 首次运行生成安全默认模板（关闭状态，不含任何地址/令牌）。
                Save(RemoteConfig.Sanitize(new RemoteConfig()));
                return RemoteConfig.Sanitize(new RemoteConfig());
            }
            var cfg = JsonSerializer.Deserialize<RemoteConfig>(File.ReadAllText(_path), JsonOptions);
            return RemoteConfig.Sanitize(cfg);
        }
        catch
        {
            return RemoteConfig.Sanitize(new RemoteConfig());
        }
    }

    public bool Save(RemoteConfig config)
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(RemoteConfig.Sanitize(config), JsonOptions));
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
