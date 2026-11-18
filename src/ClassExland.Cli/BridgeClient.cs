using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ClassExland.Cli;

internal sealed class BridgeConnectionException : Exception
{
    public BridgeConnectionException(int exitCode, string message) : base(message) => ExitCode = exitCode;
    public int ExitCode { get; }
}

internal sealed record BridgeEndpointInfo(string PipeName, string Token, string Source);

internal static class EndpointResolver
{
    private static readonly string DefaultDiscover = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassExland", "bridge-endpoint.json");

    public static BridgeEndpointInfo Resolve(string? configDir)
    {
        var candidates = new List<(string path, string source)>();
        var env = Environment.GetEnvironmentVariable("CLASSEXLAND_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
            candidates.Add((Path.Combine(configDir, "bridge.json"), $"--config-dir {configDir}"));
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add((Path.Combine(env, "bridge.json"), "CLASSEXLAND_CONFIG_DIR"));
        candidates.Add((DefaultDiscover, "default discovery"));

        foreach (var (path, source) in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var pipe = root.TryGetProperty("pipeName", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var token = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (!string.IsNullOrWhiteSpace(pipe) && !string.IsNullOrWhiteSpace(token))
                    return new BridgeEndpointInfo(pipe, token, source);
            }
            catch (JsonException)
            {
                // 尝试下一个候选
            }
        }

        throw new BridgeConnectionException(CliExit.IpcUnavailable,
            "找不到插件指令桥端点。请先启动 ClassIsland 并确认 ClassExland 插件已加载；" +
            "或用 --config-dir / CLASSEXLAND_CONFIG_DIR 指定插件配置目录。");
    }
}

internal sealed class BridgeClient
{
    private const int ConnectTimeoutMs = 5000;
    private readonly BridgeEndpointInfo _endpoint;
    private readonly int _ioTimeoutMs;

    public BridgeClient(BridgeEndpointInfo endpoint, int ioTimeoutMs = 15000)
    {
        _endpoint = endpoint;
        _ioTimeoutMs = ioTimeoutMs;
    }

    public async Task<CommandOutcome> ExecuteAsync(CommandRequestDto request, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(
            ".", _endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new BridgeConnectionException(CliExit.IpcUnavailable,
                "连接指令桥超时：ClassIsland 可能未运行或插件被禁用");
        }
        catch (Exception ex)
        {
            throw new BridgeConnectionException(CliExit.IpcUnavailable, "连接指令桥失败：" + ex.Message);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_ioTimeoutMs);

        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(_endpoint.Token.AsMemory(), cts.Token).ConfigureAwait(false);
            var accept = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (!string.Equals(accept, "OK", StringComparison.Ordinal))
                throw new BridgeConnectionException(CliExit.Security, "指令桥拒绝令牌（安全策略拒绝）");

            var payload = JsonSerializer.Serialize(request, CliJson.Options);
            await writer.WriteLineAsync(payload.AsMemory(), cts.Token).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                throw new BridgeConnectionException(CliExit.IpcUnavailable, "指令桥未返回响应");

            var response = JsonSerializer.Deserialize<BridgeResponseDto>(line, CliJson.Options);
            if (response is null)
                throw new BridgeConnectionException(CliExit.IpcUnavailable, "指令桥响应无法解析");

            return CommandOutcome.From(request.Command, response);
        }
        catch (OperationCanceledException)
        {
            throw new BridgeConnectionException(CliExit.IpcUnavailable, "指令桥响应超时");
        }
    }
}
