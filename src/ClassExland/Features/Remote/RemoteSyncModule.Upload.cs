using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassExland.Contracts;

namespace ClassExland.Features.Remote;

/// <summary>
/// 双向同步的「上行」半边与周期轮询。
///
/// 每个轮询周期：
///   1. 拉清单看服务端有没有新版本 → 有就走原来的 SyncOnceAsync（下载、校验、备份、应用、指令）；
///   2. 比对本地四个配置文件的 SHA-256 与上次同步时记下的指纹 → 有变化就 POST 上传；
///   3. 服务端用 base=head 判断是否快进；分叉时不覆盖当前快照，进待审队列由服务器裁决。
///
/// 关键点：下载优先于上传。两边同时改过时先应用服务端版本，本地指纹随之更新，
/// 不会再把自己刚收到的内容又传回去（避免回声循环）。
/// </summary>
public sealed partial class RemoteSyncModule
{
    /// <summary>CLI / 设置页手动上传（反射契约：零参数，返回可解析文本）。</summary>
    public async Task<string> UploadNowAsync()
    {
        try
        {
            lock (_statusGate) { _busy = true; }
            var cfg = _configStore.Load();
            var result = await UploadLocalAsync(cfg, "手动上传", CancellationToken.None).ConfigureAwait(false);
            var report = new SyncReport { Ok = result.Ok || result.Conflict };
            report.Add(result.Message);
            SetLast(report);
            return JsonSerializer.Serialize(new
            {
                ok = result.Ok,
                conflict = result.Conflict,
                head = result.Head,
                message = result.Message
            });
        }
        catch (Exception ex)
        {
            _audit.Log("upload-exception", $"{ex.GetType().Name}: {ex.Message}", ok: false);
            var report = new SyncReport { Ok = false };
            report.Add($"上传异常：{ex.Message}");
            SetLast(report);
            return JsonSerializer.Serialize(new { ok = false, conflict = false, head = "", message = report.Notes[0] });
        }
        finally
        {
            lock (_statusGate) { _busy = false; }
        }
    }

    /// <summary>后台周期轮询；配置每次重新读，改了间隔不用重启宿主。</summary>
    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _configStore.Load();
            var seconds = cfg.PollIntervalSeconds is < 15 or > 3600 ? 60 : cfg.PollIntervalSeconds;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!cfg.CanSync() || File.Exists(_paths.Get("remote.disabled"))) continue;
            await SyncTickAsync(cfg, ct).ConfigureAwait(false);
        }
    }

    /// <summary>一次轮询：先看服务端，再看本地。任一环节失败只记日志，不让循环退出。</summary>
    private async Task SyncTickAsync(RemoteConfig cfg, CancellationToken ct)
    {
        try
        {
            lock (_statusGate) { _busy = true; }

            var poll = await PollAsync(cfg, ct).ConfigureAwait(false);
            if (poll.NeedApply)
            {
                var applied = await SyncOnceAsync(cfg, ct).ConfigureAwait(false);
                poll.Add(applied.Notes.Count > 0 ? applied.Notes[^1] : "已应用服务端新版本");
                poll.Ok = applied.Ok;
            }

            if (cfg.AutoUpload)
            {
                var hashes = RemoteLocalSnapshot.HashAll(_paths);
                if (RemoteLocalSnapshot.HasChanges(_state.LoadLocalHashes(), hashes))
                {
                    var summary = RemoteLocalSnapshot.DescribeChanges(_state.LoadLocalHashes(), hashes);
                    var uploaded = await UploadLocalAsync(cfg, "自动上传：" + summary, ct).ConfigureAwait(false);
                    poll.LocalDirty = !uploaded.Ok;
                    poll.Add(uploaded.Message);
                    if (uploaded.Conflict) poll.Ok = false;
                }
            }

            SetLast(poll);
        }
        catch (Exception ex)
        {
            _audit.Log("tick-exception", $"{ex.GetType().Name}: {ex.Message}", ok: false);
        }
        finally
        {
            lock (_statusGate) { _busy = false; }
        }
    }

    /// <summary>把本机现有配置原文整体上传到服务端（服务端负责 diff 与版本管理）。</summary>
    public async Task<UploadResult> UploadLocalAsync(RemoteConfig cfg, string message, CancellationToken ct = default)
    {
        var result = new UploadResult();
        if (!cfg.CanSync())
        {
            result.Message = "配置无效或开关关闭，未上传";
            _audit.Log("upload-skip", result.Message, ok: false);
            return result;
        }

        var files = RemoteLocalSnapshot.ReadAll(_paths);
        if (files.Count == 0)
        {
            result.Message = "本机还没有可上传的配置（bell/duty/activities/layout）";
            return result;
        }

        var hashes = RemoteLocalSnapshot.HashAll(_paths);
        var url = cfg.ServerUrl.TrimEnd('/') + ResolveUploadPath(cfg);
        if (cfg.DeviceName.Length > 0)
            url += (url.Contains('?') ? "&" : "?") + "device=" + Uri.EscapeDataString(cfg.DeviceName);

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                @base = _state.LoadHead(),
                device = cfg.DeviceName,
                message = message,
                files = files
            });

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.FetchTimeoutSeconds) };
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.BearerToken);
            if (cfg.DeviceName.Length > 0)
                req.Headers.TryAddWithoutValidation("X-ClassExland-Device", cfg.DeviceName);
            if (!string.IsNullOrEmpty(cfg.HmacSecret))
                req.Headers.TryAddWithoutValidation("X-ClassExland-Signature", ComputeHmac(body, cfg.HmacSecret));
            var version = typeof(RemoteSyncModule).Assembly.GetName().Version?.ToString();
            if (!string.IsNullOrEmpty(version))
                req.Headers.TryAddWithoutValidation("X-ClassExland-Version", version);

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if ((int)resp.StatusCode == 409)
            {
                string detail = "";
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("message", out var m)) detail = m.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("head", out var h)) result.Head = h.GetString() ?? "";
                }
                catch { /* 非 JSON 时保留通用提示 */ }

                result.Conflict = true;
                result.Message = string.IsNullOrEmpty(detail)
                    ? "上传与服务器版本不一致，已进入待审队列"
                    : detail;
                _state.SaveConflict(result.Message, result.Head, hashes);
                _audit.Log("upload-conflict", $"{message} -> {result.Message}", ok: false);
                return result;
            }

            if (!resp.IsSuccessStatusCode)
            {
                result.Message = $"上传失败：HTTP {(int)resp.StatusCode}（{Truncate(raw, 200)}）";
                _audit.Log("upload-fail", $"{url} -> {(int)resp.StatusCode}", ok: false);
                return result;
            }

            using var okDoc = JsonDocument.Parse(raw);
            var root = okDoc.RootElement;
            result.Head = root.TryGetProperty("head", out var head) ? head.GetString() ?? "" : "";
            result.Files = root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                : Array.Empty<string>();
            result.Ok = true;
            result.Message = $"已上传 {result.Files.Count} 个配置（{message}）";
            _state.SaveUploaded(result.Head, hashes);
            _audit.Log("upload", $"{message} files={result.Files.Count} head={result.Head[..Math.Min(12, result.Head.Length)]}", ok: true);
            return result;
        }
        catch (Exception ex)
        {
            result.Message = $"上传异常：{ex.GetType().Name}: {ex.Message}";
            _audit.Log("upload-exception", result.Message, ok: false);
            return result;
        }
    }

    /// <summary>
    /// 指令的独立短周期轮询。即使配置文件一个字没变，管理员在服务器上点的命令也能很快落地。
    /// 端点只回队列，不回快照；按 id 去重，所以轮询本身是只读的、可重复调用。
    /// </summary>
    private async Task RunCommandLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _configStore.Load();
            var seconds = cfg.CommandPollSeconds is < 5 or > 3600 ? 10 : cfg.CommandPollSeconds;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!cfg.CanSync() || !cfg.EnableCommandQueue ||
                File.Exists(_paths.Get("remote.disabled"))) continue;

            try
            {
                await PollCommandsOnceAsync(cfg, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _audit.Log("command-poll-exception", $"{ex.GetType().Name}: {ex.Message}", ok: false);
            }
        }
    }

    private async Task PollCommandsOnceAsync(RemoteConfig cfg, CancellationToken ct)
    {
        var path = cfg.ManifestPath.EndsWith("manifest.php", StringComparison.OrdinalIgnoreCase)
            ? cfg.ManifestPath[..^"manifest.php".Length] + "command.php"
            : "/remote/command.php";
        var url = cfg.ServerUrl.TrimEnd('/') + path;
        if (cfg.DeviceName.Length > 0)
            url += (url.Contains('?') ? "&" : "?") + "device=" + Uri.EscapeDataString(cfg.DeviceName);

        string raw;
        string? headerSignature = null;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.FetchTimeoutSeconds) })
        {
            using var req = BuildRequest(cfg, url);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 多设备没填名字、令牌过期等只记日志，别把配置同步的状态污染成失败。
                _audit.Log("command-poll-fail", $"HTTP {(int)resp.StatusCode}", ok: false);
                return;
            }
            if (resp.Headers.TryGetValues("X-ClassExland-Signature", out var values))
                headerSignature = values.FirstOrDefault();
            raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        if (!SafeConfigWriter.IsValidJson(raw)) return;
        if (!string.IsNullOrEmpty(cfg.HmacSecret) && !VerifySignature(raw, headerSignature, "", cfg.HmacSecret))
        {
            _audit.Log("command-poll-hmac", "签名不匹配，忽略本批指令", ok: false);
            return;
        }

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("commands", out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        var done = new HashSet<string>(_state.LoadAppliedCommandIds(), StringComparer.Ordinal);
        var fresh = new List<RemoteCommand>();
        foreach (var el in arr.EnumerateArray())
        {
            var cmd = el.Deserialize<RemoteCommand>();
            if (cmd is null || string.IsNullOrWhiteSpace(cmd.Command)) continue;
            var id = string.IsNullOrWhiteSpace(cmd.Id)
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cmd.Command)))
                : cmd.Id;
            cmd.Id = id;
            if (done.Contains(id)) continue;
            fresh.Add(cmd);
        }
        if (fresh.Count == 0) return;

        var report = new SyncReport();
        // 执行成功的 id 由 ExecuteCommandsAsync 记录；失败的留在服务端队列里，下轮再试。
        await ExecuteCommandsAsync(fresh, cfg, report, ct).ConfigureAwait(false);
    }

    /// <summary>清单里的指令与 command.php 拉到的指令共用同一执行/去重路径。</summary>
    private async Task ExecuteCommandsAsync(
        IEnumerable<RemoteCommand> commands, RemoteConfig cfg, SyncReport report, CancellationToken ct)
    {
        if (_dispatcher is null)
        {
            report.Add("命令层（AddCommandBridge）未注册，跳过指令队列");
            _audit.Log("command-skip", "ICommandDispatcher 未注册", ok: false);
            return;
        }

        var done = new HashSet<string>(_state.LoadAppliedCommandIds(), StringComparer.Ordinal);
        foreach (var command in commands)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(command.Id) && !done.Add(command.Id)) continue;

            try
            {
                var request = new CommandRequest(
                    command.Command, command.Args, "remote", command.Apply, command.DryRun);
                var result = await _dispatcher.ExecuteAsync(request, ct).ConfigureAwait(false);
                report.Add($"指令 {command.Command}: {(result.Ok ? "ok" : result.Message)}");
                _audit.Log("command", $"{command.Command}: {result.Message}", ok: result.Ok);
                if (!string.IsNullOrWhiteSpace(command.Id)) _state.MarkCommandApplied(command.Id);
            }
            catch (Exception ex)
            {
                report.Add($"指令 {command.Command} 异常：{ex.Message}");
                _audit.Log("command-exception", $"{command.Command}: {ex.Message}", ok: false);
            }
        }
    }

    private static string ResolveUploadPath(RemoteConfig cfg)
    {
        var path = cfg.UploadPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = cfg.ManifestPath.EndsWith("manifest.php", StringComparison.OrdinalIgnoreCase)
                ? cfg.ManifestPath[..^"manifest.php".Length] + "upload.php"
                : "/remote/upload.php";
        }
        return path.StartsWith('/') ? path : "/" + path;
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? "" : (text.Length > max ? text[..max] : text);
}
