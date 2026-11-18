using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassExland.Contracts;

namespace ClassExland.Features.Bridge;

/// <summary>
/// 管理本机指令桥端点：管道名与令牌在插件首次启动时生成到插件配置目录，
/// 并写一份「发现文件」到 %LOCALAPPDATA%\ClassExland\bridge-endpoint.json 供 CLI 定位。
/// 管道使用 CurrentUserOnly，仅当前用户可连接；不监听 TCP，不需要管理员权限。
/// </summary>
public sealed class BridgeEndpoint
{
    private readonly IPluginPaths _paths;
    private readonly object _gate = new();
    private EndpointDocument? _cached;

    public BridgeEndpoint(IPluginPaths paths) => _paths = paths;

    public string ConfigPath => _paths.Get(ConfigFiles.Bridge);

    /// <summary>CLI 默认发现文件路径。</summary>
    public static string DiscoverFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassExland", "bridge-endpoint.json");

    public EndpointDocument Ensure()
    {
        lock (_gate)
        {
            if (_cached is not null) return _cached;

            EndpointDocument? doc = null;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    doc = JsonSerializer.Deserialize<EndpointDocument>(
                        File.ReadAllText(ConfigPath), BridgeProtocol.Json);
                }
            }
            catch
            {
                // 文件损坏则重新生成
            }

            if (doc is null || string.IsNullOrWhiteSpace(doc.PipeName) || string.IsNullOrWhiteSpace(doc.Token))
            {
                doc = new EndpointDocument
                {
                    PipeName = "ClassExland.Bridge." + Guid.NewGuid().ToString("N"),
                    Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    CreatedAt = DateTimeOffset.UtcNow
                };
                WriteDocument(ConfigPath, doc);
            }

            _cached = doc;
            WriteDiscovery(doc);
            return doc;
        }
    }

    public void RefreshDiscovery() => WriteDiscovery(Ensure());

    private void WriteDiscovery(EndpointDocument doc)
    {
        try
        {
            WriteDocument(DiscoverFilePath, doc);
        }
        catch
        {
            // 发现文件写入失败不应阻止插件启动
        }
    }

    private static void WriteDocument(string path, EndpointDocument doc)
    {
        FileStore.WriteAtomic(path, JsonSerializer.Serialize(doc, BridgeProtocol.Json));
        FileStore.RestrictToCurrentUser(path);
    }
}

public sealed class EndpointDocument
{
    public string PipeName { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Version { get; set; } = "1.0.0.0";
    /// <summary>Loopback Web 管理页实际监听端口，0 表示未启动。</summary>
    public int WebPort { get; set; }
    /// <summary>Loopback Web 管理页地址，含一次性访问令牌。</summary>
    public string? WebUrl { get; set; }
}
