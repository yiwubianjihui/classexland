using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassExland.Features.Bridge;

/// <summary>
/// 本机命名管道上的一行一条 JSON 协议（JSON 不含换行，ReadLine/WriteLine 分帧）。
/// 首帧：客户端令牌；第二帧：请求；服务端回应：OK/DENIED 帧 + 响应帧。
/// </summary>
internal static class BridgeProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const string Accept = "OK";
    public const string Deny = "DENIED";
}

internal sealed class BridgeRequest
{
    public string Command { get; set; } = "";
    public Dictionary<string, string?>? Args { get; set; }
    public string? Source { get; set; }
    public bool Apply { get; set; }
    public bool DryRun { get; set; }
}

internal sealed class BridgeResponse
{
    public BridgeResponse() { }

    public BridgeResponse(bool ok, string message, object? data)
    {
        Ok = ok;
        Message = message;
        Data = data;
    }

    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public object? Data { get; set; }
}
