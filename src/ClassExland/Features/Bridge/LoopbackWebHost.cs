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
        <!doctype html>
        <html lang="zh-CN"><head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>ClassExland 控制台</title>
        <style>
          :root{color-scheme:light dark;--bg:#f5f6f8;--card:#fff;--fg:#1b1f24;--muted:#6a737d;--line:#e3e6ea;--accent:#2f6fed;--ok:#1a7f37;--danger:#c0392b}
          @media (prefers-color-scheme:dark){:root{--bg:#14171a;--card:#1c2024;--fg:#e9edf2;--muted:#98a2ad;--line:#2a3036;--accent:#6b9dff;--ok:#4ac26b;--danger:#ff7b72}}
          *{box-sizing:border-box}
          body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.55 system-ui,"Segoe UI","Microsoft YaHei",sans-serif}
          .wrap{max-width:760px;margin:0 auto;padding:26px 18px 56px}
          header{display:flex;align-items:baseline;gap:10px;margin-bottom:16px}
          header h1{font-size:18px;margin:0}
          .tag{font-size:12px;color:var(--muted);border:1px solid var(--line);border-radius:99px;padding:2px 9px}
          .card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:18px;margin-bottom:13px}
          .state{font-size:30px;font-weight:700;line-height:1.2}
          .state[data-s="OnClass"]{color:var(--accent)}
          .state[data-s="Breaking"]{color:var(--ok)}
          .state[data-s="AfterSchool"],.state[data-s="None"]{color:var(--muted)}
          .sub{color:var(--muted);font-size:14px}
          .grid{display:grid;grid-template-columns:repeat(2,1fr);gap:10px;margin-bottom:13px}
          @media (max-width:520px){.grid{grid-template-columns:1fr}}
          button{font:inherit;padding:12px 14px;border-radius:10px;border:1px solid var(--line);background:var(--card);color:var(--fg);cursor:pointer}
          button:hover{border-color:var(--accent)}
          button.primary{background:var(--accent);border-color:var(--accent);color:#fff}
          button:disabled{opacity:.5;cursor:default}
          h2{font-size:14px;margin:0 0 10px;color:var(--muted);font-weight:600}
          .duty-item{display:flex;justify-content:space-between;gap:12px;padding:9px 0;border-top:1px dashed var(--line)}
          .duty-item:first-of-type{border-top:0}
          .duty-proj{color:var(--muted)}
          .duty-names{font-weight:600;text-align:right}
          .row{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
          select{font:inherit;padding:9px 10px;border-radius:9px;border:1px solid var(--line);background:var(--card);color:var(--fg);min-width:190px}
          .hint{color:var(--muted);font-size:12px}
          details{margin-top:14px}
          summary{cursor:pointer}
          pre{background:var(--bg);border:1px solid var(--line);border-radius:10px;padding:12px;overflow:auto;font-size:12px}
          #toast{position:fixed;left:50%;bottom:22px;transform:translateX(-50%);background:#111;color:#fff;padding:10px 16px;border-radius:99px;font-size:13px;opacity:0;transition:.2s;pointer-events:none;max-width:90vw}
          #toast.show{opacity:.92}
        </style></head>
        <body><div class="wrap">
          <header><h1>ClassExland 控制台</h1><span class="tag">仅本机可访问</span></header>

          <section class="card">
            <div class="state" id="state" data-s="">读取中…</div>
            <div class="sub" id="next">—</div>
            <div class="sub" id="meta">—</div>
          </section>

          <div class="grid">
            <button onclick="switchLayout('class')">切到上课布局</button>
            <button onclick="switchLayout('break')">切到下课布局</button>
            <button onclick="refresh()">刷新状态</button>
            <button onclick="loadDuty()">查看值日表</button>
            <button onclick="loadProfiles()">切换班级档案</button>
            <button onclick="syncNow()">立即同步配置</button>
          </div>

          <section class="card" id="profileCard" hidden>
            <h2>切换班级档案</h2>
            <div class="row">
              <select id="profileSelect"></select>
              <button class="primary" onclick="doSwitch()">切换</button>
            </div>
            <p class="hint">切换会改变当前班级的课表与值日人员，执行前会再确认一次。</p>
          </section>

          <section class="card">
            <h2>值日表</h2>
            <div id="dutyBox"><p class="hint">点上方「查看值日表」加载。</p></div>
          </section>

          <details><summary class="hint">原始数据（排查问题时用）</summary><pre id="out">—</pre></details>
        </div>
        <div id="toast"></div>
        <script>
        const token = new URLSearchParams(location.search).get('token') || '';
        const STATE = { None: '空闲', PrepareOnClass: '预备铃', OnClass: '上课中', Breaking: '课间', AfterSchool: '放学' };
        const $ = function (id) { return document.getElementById(id); };

        function el(tag, cls, text) { const n = document.createElement(tag); if (cls) n.className = cls; if (text != null) n.textContent = text; return n; }
        function toast(msg) { const t = $('toast'); t.textContent = msg; t.classList.add('show'); clearTimeout(t._t); t._t = setTimeout(function () { t.classList.remove('show'); }, 2600); }
        function hhmm(v) { return new Date(v).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' }); }
        function left(sec) { if (sec == null) return ''; return sec >= 60 ? Math.floor(sec / 60) + ' 分 ' + (sec % 60) + ' 秒' : sec + ' 秒'; }
        function baseName(p) { const s = String(p || ''); const i = Math.max(s.lastIndexOf('/'), s.lastIndexOf('\\')); return i >= 0 ? s.slice(i + 1) : s; }
        function dump(r) { $('out').textContent = JSON.stringify(r, null, 2); }

        async function api(path, opt) {
          const sep = path.indexOf('?') >= 0 ? '&' : '?';
          const r = await fetch(path + sep + 'token=' + encodeURIComponent(token), opt);
          return r.json();
        }

        async function refresh() {
          try {
            const r = await api('/api/status');
            dump(r);
            if (!r.ok) { toast(r.message || '状态读取失败'); return; }
            const d = r.data || {};
            const s = $('state');
            s.textContent = STATE[d.virtualState] || d.virtualState || '未知';
            s.dataset.s = d.virtualState || '';
            $('next').textContent = d.nextBellAt
              ? '下一次铃 ' + hhmm(d.nextBellAt) + (d.secondsToNextBell != null ? ' · ' + left(d.secondsToNextBell) + '后' : '')
              : '今天没有更多铃声';
            const bits = [];
            if (d.currentMoment && d.currentMoment.subject) bits.push('当前：' + d.currentMoment.subject);
            if (d.bellConfiguredSlots != null) bits.push('已配置 ' + d.bellConfiguredSlots + ' 节课');
            if (d.profile) bits.push('档案：' + baseName(d.profile));
            $('meta').textContent = bits.join(' · ') || '—';
            if (d.duty) renderDuty(d.duty);
          } catch (e) {
            $('state').textContent = '连不上插件';
            $('next').textContent = String(e);
          }
        }

        function renderDuty(duty) {
          const box = $('dutyBox');
          box.replaceChildren();
          if (!duty || !duty.items || duty.items.length === 0) { box.append(el('p', 'hint', '当前时段没有值日安排。')); return; }
          box.append(el('p', 'hint', (duty.slotLabel || '') + (duty.date ? ' · ' + duty.date : '')));
          duty.items.forEach(function (it) {
            const row = el('div', 'duty-item');
            row.append(el('span', 'duty-proj', it.project || ''));
            const names = (it.persons || []).map(function (p) { return p.name; }).join('、') || '—';
            row.append(el('span', 'duty-names', names));
            box.append(row);
          });
        }

        async function loadDuty() {
          try {
            const r = await api('/api/duty');
            dump(r);
            if (!r.ok) { toast(r.message || '读取失败'); return; }
            const d = r.data || {};
            renderDuty(d.current || (d.views && d.views[0]));
          } catch (e) { toast('读取失败：' + e); }
        }

        async function loadProfiles() {
          try {
            const r = await api('/api/profiles');
            dump(r);
            if (!r.ok) { toast(r.message || '读取失败'); return; }
            const list = (r.data && r.data.profiles) || [];
            const sel = $('profileSelect');
            sel.replaceChildren();
            list.forEach(function (name) {
              const o = document.createElement('option');
              o.value = name;
              o.textContent = baseName(name);
              sel.append(o);
            });
            $('profileCard').hidden = list.length === 0;
            if (!list.length) toast('没有可切换的档案');
          } catch (e) { toast('读取失败：' + e); }
        }

        async function doSwitch() {
          const name = $('profileSelect').value;
          if (!name) return;
          if (!confirm('确定切换到「' + name + '」？这会改变当前班级的课表与值日人员。')) return;
          const r = await api('/api/profile/switch', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ to: name, confirm: 'yes' }) });
          dump(r);
          toast(r.ok ? '已切换' : (r.message || '切换失败'));
          if (r.ok) refresh();
        }

        async function switchLayout(to) {
          if (!confirm(to === 'class' ? '切到上课布局？' : '切到下课布局？')) return;
          const r = await api('/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ command: 'layout.switch', args: { to: to }, apply: true, dryRun: false }) });
          dump(r);
          toast(r.ok ? '已切换布局' : (r.message || '切换失败'));
        }

        async function syncNow() {
          const r = await api('/api/sync', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
          dump(r);
          toast(r.ok ? '同步完成' : (r.message || '同步失败'));
        }

        refresh();
        setInterval(refresh, 30000);
        </script>
        </body></html>
        """;
}
