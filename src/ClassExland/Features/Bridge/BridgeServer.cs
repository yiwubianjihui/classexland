using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClassExland.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Bridge;

/// <summary>
/// 本机命名管道服务器，接收 CLI 请求并交给 CommandDispatcher。
/// 令牌不符直接 DENIED；管道仅当前用户可连接；不监听 TCP。
/// </summary>
public sealed class BridgeServer : BackgroundService
{
    private readonly BridgeEndpoint _endpoint;
    private readonly CommandDispatcher _dispatcher;
    private readonly ILogger<BridgeServer> _logger;

    public BridgeServer(BridgeEndpoint endpoint, CommandDispatcher dispatcher, ILogger<BridgeServer> logger)
    {
        _endpoint = endpoint;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var doc = _endpoint.Ensure();
        _endpoint.RefreshDiscovery();
        _logger.LogInformation("ClassExland 指令桥已启动，管道 {Pipe}", doc.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = PipeOptions.Asynchronous;
            if (OperatingSystem.IsWindows()) options |= PipeOptions.CurrentUserOnly;

            var server = new NamedPipeServerStream(
                doc.PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options);

            try
            {
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "等待指令桥连接失败");
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleAsync(server, doc.Token, stoppingToken), stoppingToken);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, string token, CancellationToken ct)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };

                var presented = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (!string.Equals(presented, token, StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync(BridgeProtocol.Deny).ConfigureAwait(false);
                    return;
                }
                await writer.WriteLineAsync(BridgeProtocol.Accept).ConfigureAwait(false);

                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) return;

                var req = JsonSerializer.Deserialize<BridgeRequest>(line, BridgeProtocol.Json);
                if (req is null) return;

                var request = new CommandRequest(
                    req.Command,
                    req.Args ?? new Dictionary<string, string?>(),
                    req.Source ?? "cli",
                    req.Apply,
                    req.DryRun);

                var result = await _dispatcher.ExecuteAsync(request, ct).ConfigureAwait(false);
                var response = new BridgeResponse(result.Ok, result.Message, result.Data);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, BridgeProtocol.Json)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "处理指令桥连接失败");
            }
        }
    }
}
