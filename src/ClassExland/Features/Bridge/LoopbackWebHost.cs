using System.Net;
using System.Text;
using System.Text.Json;
using ClassExland.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Bridge;

/// <summary>
/// 仅监听 127.0.0.1 的轻量管理页，与 CLI 共用 ICommandDispatcher。
/// 需携带桥令牌（?token= 或 Authorization: Bearer），由 web.start / web.stop 控制。
/// 默认不随插件自动启动。
/// </summary>
public sealed class LoopbackWebHost : BackgroundService
{
    private readonly BridgeEndpoint _endpoint;
    private readonly CommandDispatcher _dispatcher;
    private readonly ILogger<LoopbackWebHost> _logger;
    private readonly string _token;
    private HttpListener? _listener;
    private CancellationTokenSource? _loopCts;
    private volatile bool _running;
    private int _port;
    private string _url = "";

    public LoopbackWebHost(BridgeEndpoint endpoint, CommandDispatcher dispatcher, ILogger<LoopbackWebHost> logger)
    {
        _endpoint = endpoint;
        _dispatcher = dispatcher;
        _logger = logger;
        // 只在构造时取一次令牌，避免每次 HTTP 请求都进 lock；Ensure() 首次会生成端点文档
        _token = endpoint.Ensure().Token;
    }

    public bool IsRunning => _running;
    public string Url => _url;
    public int Port => _port;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop();
        }
    }

    public async Task<string> StartAsync(int preferredPort = 18765, CancellationToken ct = default)
    {
        if (_running) return _url;

        var port = await FindFreePortAsync(preferredPort, ct).ConfigureAwait(false);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        _listener = listener;
        _port = port;
        _url = $"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(_endpoint.Ensure().Token)}";

        var doc = _endpoint.Ensure();
        doc.WebPort = port;
        doc.WebUrl = _url;
        BridgeEndpointRefresh();

        _running = true;
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => LoopAsync(_loopCts.Token), _loopCts.Token);
        _logger.LogInformation("Loopback 管理页已启动: {Url}", _url);
        return _url;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try
        {
            _loopCts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
        }
        catch { /* ignore */ }
        finally
        {
            _listener = null;
            _loopCts?.Dispose();
            _loopCts = null;
        }
        try
        {
            var doc = _endpoint.Ensure();
            doc.WebPort = 0;
            doc.WebUrl = null;
            BridgeEndpointRefresh();
        }
        catch { /* ignore */ }
        _logger.LogInformation("Loopback 管理页已停止");
    }

    private void BridgeEndpointRefresh()
    {
        // 将最新 Web 状态刷新到发现文件，供 CLI web status 读取
        var path = BridgeEndpoint.DiscoverFilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var doc = _endpoint.Ensure();
        FileStore.WriteAtomic(path, JsonSerializer.Serialize(doc, BridgeProtocol.Json));
    }

    private static async Task<int> FindFreePortAsync(int preferred, CancellationToken ct)
    {
        for (var p = preferred; p < preferred + 100; p++)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsPortInUse(p)) return p;
        }
        return 0;
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            using (result.AsyncWaitHandle) return result.AsyncWaitHandle.WaitOne(100) && client.Connected;
        }
        catch { return false; }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var q = ctx.Request.QueryString;
            var headerToken = ctx.Request.Headers["Authorization"]?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
            var token = q["token"] ?? headerToken;
            if (!string.Equals(token, _token, StringComparison.Ordinal))
            {
                await RespondAsync(ctx, 403, "{\"error\":\"forbidden\"}", "application/json").ConfigureAwait(false);
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            switch (path)
            {
                case "/api/status":
                    await RunCommandAsync(ctx, CommandNames.Status, new Dictionary<string, string?>(), ct).ConfigureAwait(false);
                    return;
                case "/api/duty":
                    await RunCommandAsync(ctx, CommandNames.Duty.Show, new Dictionary<string, string?>(), ct).ConfigureAwait(false);
                    return;
                case "/api/profiles":
                    await RunCommandAsync(ctx, CommandNames.Profile.List, new Dictionary<string, string?>(), ct).ConfigureAwait(false);
                    return;
                case "/api/profile/switch" when string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase):
                {
                    var args = await ReadArgsAsync(ctx, ct).ConfigureAwait(false);
                    var name = FirstNonEmpty(args, "to", "name", "profile");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        await RespondAsync(ctx, 400, "{\"error\":\"missing to/name\"}", "application/json").ConfigureAwait(false);
                        return;
                    }
                    args["to"] = name;
                    // 高危命令二次确认语义：仅 confirm=yes 才真正执行，否则返回 dry-run 预览
                    var confirm = IsTruthy(FirstNonEmpty(args, "confirm"));
                    await RunCommandAsync(ctx, CommandNames.Profile.Switch, args, ct,
                        apply: confirm, dryRun: !confirm).ConfigureAwait(false);
                    return;
                }
                case "/api/sync" when string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase):
                {
                    var args = await ReadArgsAsync(ctx, ct).ConfigureAwait(false);
                    var dryRun = IsTruthy(FirstNonEmpty(args, "dryRun", "dry_run"));
                    await RunCommandAsync(ctx, CommandNames.Remote.Sync, args, ct,
                        apply: !dryRun, dryRun: dryRun).ConfigureAwait(false);
                    return;
                }
                case "/api/command" when string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase):
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    var req = JsonSerializer.Deserialize<BridgeRequest>(body, BridgeProtocol.Json);
                    if (req is null)
                    {
                        await RespondAsync(ctx, 400, "{\"error\":\"bad request\"}", "application/json").ConfigureAwait(false);
                        return;
                    }
                    await RunCommandAsync(ctx, req.Command, req.Args ?? new Dictionary<string, string?>(), ct, req.Apply, req.DryRun).ConfigureAwait(false);
                    return;
                }
                default:
                    await RespondAsync(ctx, 200, BuildPage(), "text/html; charset=utf-8").ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理管理页请求失败");
            try { await RespondAsync(ctx, 500, "{\"error\":\"internal\"}", "application/json").ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task RunCommandAsync(HttpListenerContext ctx, string command, IReadOnlyDictionary<string, string?> args, CancellationToken ct, bool apply = false, bool dryRun = true)
    {
        var result = await _dispatcher.ExecuteAsync(
            new CommandRequest(command, args, "web", apply, dryRun), ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(new BridgeResponse(result.Ok, result.Message, result.Data), BridgeProtocol.Json);
        await RespondAsync(ctx, 200, json, "application/json").ConfigureAwait(false);
    }

    /// <summary>从 query string 与可选 JSON body 合并参数；body 优先。token 不进入命令参数。</summary>
    private static async Task<Dictionary<string, string?>> ReadArgsAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var args = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var query = ctx.Request.QueryString;
        foreach (string? key in query.AllKeys)
        {
            if (string.IsNullOrEmpty(key) || key.Equals("token", StringComparison.OrdinalIgnoreCase)) continue;
            args[key] = query[key];
        }

        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8, false, 1024, leaveOpen: true);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name.Equals("token", StringComparison.OrdinalIgnoreCase)) continue;
                        args[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.ToString(),
                            JsonValueKind.Null => null,
                            _ => prop.Value.GetRawText()
                        };
                    }
                }
            }
        }
        catch
        {
            // 空 body 或非法 JSON 时仅使用 query 参数
        }
        return args;
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string?> args, params string[] keys)
    {
        foreach (var key in keys)
            if (args.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    private static bool IsTruthy(string? value)
        => value is not null &&
           (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.Ordinal));

    private static Task RespondAsync(HttpListenerContext ctx, int status, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        return ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ContinueWith(_ => ctx.Response.Close());
    }

    private string BuildPage() => """
        <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>ClassExland 管理页</title>
        <style>body{font-family:system-ui;margin:2rem;max-width:780px}pre{background:#f5f5f5;padding:1rem;overflow:auto;border-radius:6px}
        button{padding:.5rem 1rem;margin:.25rem}code{color:#256}</style></head><body>
        <h1>ClassExland 管理页</h1><p>仅本机可访问，令牌已包含在地址中。请勿分享完整 URL。</p>
        <div><button onclick="load('/api/status')">当前状态</button><button onclick="load('/api/duty')">当前值日</button><button onclick="load('/api/profiles')">档案列表</button></div>
        <div><button onclick="post('/api/sync',{})">立即同步</button><button onclick="switchProfile()">切换档案</button></div>
        <h3>结果</h3><pre id="out">点击上方按钮查看</pre>
        <script>
        const token = new URLSearchParams(location.search).get('token') || '';
        async function show(r){document.getElementById('out').textContent=JSON.stringify(await r.json(),null,2);}
        async function load(p){show(await fetch(p+'?token='+encodeURIComponent(token)));}
        async function post(p,body){show(await fetch(p+'?token='+encodeURIComponent(token),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}));}
        function switchProfile(){const name=prompt('目标档案名（如 YBH.json）：');if(!name)return;const ok=window.confirm('确认切换档案？此操作会改变当前班级档案。');post('/api/profile/switch',{to:name,confirm:ok?'yes':'no'});}
        </script></body></html>
        """;
}
