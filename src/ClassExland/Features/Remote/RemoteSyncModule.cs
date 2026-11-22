using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassExland.Contracts;
using Microsoft.Extensions.Hosting;

namespace ClassExland.Features.Remote;

/// <summary>
/// 远程配置同步编排。仅在启动时发起一次后台请求（带超时），无网络时静默跳过，
/// 绝不阻塞宿主启动。支持：完整快照 + 指令队列、HTTPS + Bearer + 可选 HMAC、
/// 版本/etag 去重、整份备份、校验失败/下载失败自动回滚、审计、一键停用。
/// </summary>
public sealed partial class RemoteSyncModule : IHostedService, IRemoteSyncService
{
    private readonly RemoteConfigStore _configStore;
    private readonly RemoteStateStore _state;
    private readonly RemoteAuditLogger _audit;
    private readonly SafeConfigWriter _writer;
    private readonly CoreConfigWriter _coreWriter;
    private readonly ICommandDispatcher? _dispatcher;
    private readonly IServiceProvider _services;
    private readonly IPluginPaths _paths;

    private CancellationTokenSource? _lifetime;

    private readonly object _statusGate = new();
    private SyncReport? _lastReport;
    private DateTimeOffset? _lastRunAt;
    private bool _busy;

    public RemoteSyncModule(
        RemoteConfigStore configStore,
        RemoteStateStore state,
        RemoteAuditLogger audit,
        SafeConfigWriter writer,
        CoreConfigWriter coreWriter,
        IPluginPaths paths,
        IServiceProvider serviceProvider)
    {
        _configStore = configStore;
        _state = state;
        _audit = audit;
        _writer = writer;
        _coreWriter = coreWriter;
        _paths = paths;
        _services = serviceProvider;
        // 命令层可能稍后由 AddCommandBridge 注册，这里用 IServiceProvider 安全解析，缺失也不抛。
        _dispatcher = serviceProvider.GetService(typeof(ICommandDispatcher)) as ICommandDispatcher;
    }

    // ---- IRemoteSyncService（反射契约：零参数）----

    public object? Status
    {
        get
        {
            try
            {
                lock (_statusGate)
                {
                    var (version, etag) = _state.Load();
                    var cfg = _configStore.Load();
                    // 属性名用 PascalCase，便于 Bridge 按 Enabled/CanSync/LastOk 等关键名字段读取。
                    return new
                    {
                        Enabled = cfg.Enabled,
                        CanSync = cfg.CanSync(),
                        CoreSyncEnabled = cfg.EnableCoreConfigSync,
                        LastVersion = version,
                        LastETag = etag,
                        LastRunAt = _lastRunAt,
                        Busy = _busy,
                        LastOk = _lastReport?.Ok,
                        LastMessage = _lastReport?.Notes.Count > 0 ? _lastReport.Notes[^1] : "",
                        AutoUpload = cfg.AutoUpload,
                        PollIntervalSeconds = cfg.PollIntervalSeconds,
                        HeadHash = _state.LoadHead(),
                        LocalDirty = _lastReport?.LocalDirty ?? false,
                        LastConflict = _state.LoadUploadInfo().LastConflict,
                        LastUploadAt = _state.LoadUploadInfo().LastUploadAt
                    };
                }
            }
            catch (Exception ex)
            {
                _audit.Log("status-exception", $"{ex.GetType().Name}: {ex.Message}", ok: false);
                return new { Enabled = false, CanSync = false, CoreSyncEnabled = false, LastVersion = "", LastETag = "", LastRunAt = (DateTimeOffset?)null, Busy = false, LastOk = (bool?)null, LastMessage = ex.Message };
            }
        }
    }

    public async Task<string> SyncNowAsync()
    {
        try
        {
            lock (_statusGate) { _busy = true; }
            var report = await SyncOnceAsync(_configStore.Load()).ConfigureAwait(false);
            SetLast(report);
            return Summarize(report);
        }
        catch (Exception ex)
        {
            _audit.Log("sync-exception", $"{ex.GetType().Name}: {ex.Message}", ok: false);
            var report = new SyncReport { Ok = false };
            report.Add($"同步异常：{ex.Message}");
            SetLast(report);
            return Summarize(report);
        }
        finally
        {
            lock (_statusGate) { _busy = false; }
        }
    }

    public async Task<string> PollOnceAsync()
    {
        try
        {
            lock (_statusGate) { _busy = true; }
            var report = await PollAsync(_configStore.Load()).ConfigureAwait(false);
            SetLast(report);
            return Summarize(report);
        }
        catch (Exception ex)
        {
            _audit.Log("poll-exception", $"{ex.GetType().Name}: {ex.Message}", ok: false);
            var report = new SyncReport { Ok = false };
            report.Add($"检查异常：{ex.Message}");
            SetLast(report);
            return Summarize(report);
        }
        finally
        {
            lock (_statusGate) { _busy = false; }
        }
    }

    private void SetLast(SyncReport report)
    {
        lock (_statusGate)
        {
            _lastReport = report;
            _lastRunAt = DateTimeOffset.Now;
        }
    }

    private static string Summarize(SyncReport report)
        => JsonSerializer.Serialize(new
        {
            ok = report.Ok,
            version = report.Version,
            etag = report.ETag,
            message = report.Notes.Count > 0 ? report.Notes[^1] : ""
        });

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 一键停用：存在 remote.disabled 文件时，无论配置如何都不发起请求。
            if (File.Exists(_paths.Get("remote.disabled")))
            {
                _audit.Log("disabled", "检测到 remote.disabled，跳过本次同步");
                return Task.CompletedTask;
            }

            var cfg = _configStore.Load();
            if (!cfg.CanSync())
            {
                _audit.Log("skip", "未配置有效的 https 地址/令牌，或开关关闭；完全离线可用");
                return Task.CompletedTask;
            }

            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _lifetime.Token;
            // 后台任务 + 超时：不在启动路径做阻塞网络请求。
            _ = Task.Run(() => SyncOnceAsync(cfg, token), CancellationToken.None);
            // 之后按配置周期轮询：先检查服务端新版本，再检查本地变更并上传。
            _ = Task.Run(() => RunPollLoopAsync(token), CancellationToken.None);
            // 指令单独短周期拉取（默认 10 秒），让管理页点「立即下发」很快生效。
            _ = Task.Run(() => RunCommandLoopAsync(token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _audit.Log("start-exception", $"{ex.GetType().Name}: {ex.Message}", ok: false);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;
        }
        catch { /* 忽略 */ }
        return Task.CompletedTask;
    }

    /// <summary>手动触发一次同步（Web 管理页 / CLI 可复用）。</summary>
    public async Task<SyncReport> SyncOnceAsync(RemoteConfig cfg, CancellationToken ct = default)
    {
        var report = new SyncReport();
        if (!cfg.CanSync())
        {
            report.Add("配置无效或开关关闭");
            return report;
        }

        var applied = new List<ApplyFileResult>();
        try
        {
            // 1) 下载（https + Bearer + 超时）
            string raw;
            string? headerSignature = null;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.FetchTimeoutSeconds) })
            {
                var url = BuildManifestUrl(cfg);
                using var req = BuildRequest(cfg, url);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var hint = await DescribeHttpFailureAsync(resp, ct).ConfigureAwait(false);
                    report.Add($"下载失败：HTTP {(int)resp.StatusCode}{hint}");
                    _audit.Log("download-fail", $"{url} -> {(int)resp.StatusCode}", ok: false);
                    report.Ok = false;
                    return report;
                }

                if (resp.Headers.TryGetValues("X-ClassExland-Signature", out var values))
                    headerSignature = values.FirstOrDefault();

                raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            if (!SafeConfigWriter.IsValidJson(raw))
            {
                report.Add("下载内容不是合法 JSON，拒绝应用");
                _audit.Log("validate-fail", "清单不是合法 JSON", ok: false);
                report.Ok = false;
                return report;
            }

            var manifest = JsonSerializer.Deserialize<RemoteManifest>(raw) ?? new RemoteManifest();
            report.Version = manifest.Version;
            report.ETag = manifest.ETag;
            report.Head = manifest.Head;

            // 2) 可选 HMAC-SHA256 签名校验。
            //    优先用响应头 X-ClassExland-Signature：它对响应原文直接签名，服务端用任何语言都好实现
            //    （PHP 的 hash_hmac 直接对上）；没有头时退回旧协议——对去掉 signature 字段后的清单计算。
            if (!string.IsNullOrEmpty(cfg.HmacSecret)
                && !VerifySignature(raw, headerSignature, manifest.Signature, cfg.HmacSecret))
            {
                report.Add("HMAC 签名校验失败，拒绝应用");
                _audit.Log("hmac-fail", $"version={manifest.Version}", ok: false);
                report.Ok = false;
                return report;
            }

            // 2.5) 白名单/黑名单安全策略：出现禁止键/文件/指令 ⇒ 整份拒绝，绝不部分应用。
            var policy = CoreConfigPolicy.Validate(manifest);
            if (!policy.Ok)
            {
                report.Add($"安全策略拒绝：{policy.Error}");
                _audit.Log("policy-reject", policy.Error, ok: false);
                report.Ok = false;
                return report;
            }

            // 3) 版本/etag 去重，避免重复应用
            var (lastVersion, lastETag) = _state.Load();
            if (!string.IsNullOrEmpty(manifest.Version) &&
                manifest.Version == lastVersion && manifest.ETag == lastETag)
            {
                report.Add($"版本 {manifest.Version} 已应用，跳过");
                _audit.Log("duplicate", $"version={manifest.Version} etag={manifest.ETag}");
                report.Ok = true;
                return report;
            }

            // 4) 应用前整份备份
            var fullBackup = _writer.BackupConfigTree();
            report.Add($"整份备份：{(string.IsNullOrEmpty(fullBackup) ? "失败" : fullBackup)}");
            _audit.Log("backup", $"full={fullBackup}", ok: !string.IsNullOrEmpty(fullBackup));

            // 5) 插件配置快照
            if (cfg.EnableSnapshotSync && manifest.Snapshot is not null)
            {
                foreach (var pair in manifest.Snapshot.PluginConfigs)
                {
                    ct.ThrowIfCancellationRequested();
                    var fileName = pair.Key;
                    if (!IsSafePluginConfigName(fileName))
                    {
                        report.Add($"拒绝非法配置名：{fileName}");
                        _audit.Log("reject-file", fileName, ok: false);
                        continue;
                    }

                    var content = pair.Value.GetRawText();
                    var result = _writer.ApplyFile(_paths.Get(fileName), content, validateJson: true);
                    applied.Add(result);
                    report.Add($"插件配置 {fileName}: {result.Message}");
                    if (!result.Ok) throw new InvalidOperationException($"插件配置写入失败：{fileName} {result.Message}");
                }

                // 6) ClassIsland 核心配置（备份-校验-原子写-冲突保留本地）
                if (cfg.EnableCoreConfigSync)
                {
                    var core = _coreWriter.Apply(manifest.Snapshot.Core, ct);
                    report.Add(core.Message);
                    foreach (var f in core.RolledBackFiles) report.RolledBack.Add(f);
                    if (!core.Ok) throw new InvalidOperationException(core.Message);
                }

                // 快照是直接写文件的，运行时内存里仍是旧值；预备铃必须在这里热生效，
                // 否则远程下发了每节课提前量也要等下次重启才看得到。
                RefreshBellRuntime(applied);
            }

            // 7) 指令队列（经 ICommandDispatcher，与 CLI 共用命令层）
            if (cfg.EnableCommandQueue)
            {
                await ExecuteCommandsAsync(manifest.Commands, cfg, report, ct).ConfigureAwait(false);
            }

            // 8) 记录版本与本地指纹，完成。
            //    这里记的是「应用完之后磁盘上的真实内容」：首次安装时服务端快照可能是空的，
            //    本机已有的配置会因此被识别为新变更，在下一次轮询里整体上传。
            var localHashes = RemoteLocalSnapshot.HashAll(_paths);
            _state.SaveApplied(manifest.Version, manifest.ETag, manifest.Head, localHashes);
            report.Ok = true;
            _audit.Log("applied", $"version={manifest.Version} etag={manifest.ETag}", ok: true);
            return report;
        }
        catch (Exception ex)
        {
            // 下载/校验/写入任一环节失败 → 整批回滚插件配置。
            var rolled = _writer.RollbackAll(applied);
            report.Ok = false;
            report.Add($"同步失败并回滚：{ex.GetType().Name}: {ex.Message}（回滚{(rolled ? "完成" : "部分失败")}）");
            _audit.Log("rollback", report.Notes.Last(), ok: false);
            return report;
        }
    }

    /// <summary>只拉取清单并检查版本/etag，不应用任何配置（供 remote.poll 命令使用）。</summary>
    public async Task<SyncReport> PollAsync(RemoteConfig cfg, CancellationToken ct = default)
    {
        var report = new SyncReport();
        if (!cfg.CanSync())
        {
            report.Add("配置无效或开关关闭");
            return report;
        }

        try
        {
            string raw;
            string? headerSignature = null;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.FetchTimeoutSeconds) })
            {
                var url = BuildManifestUrl(cfg);
                using var req = BuildRequest(cfg, url);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var hint = await DescribeHttpFailureAsync(resp, ct).ConfigureAwait(false);
                    report.Add($"拉取清单失败：HTTP {(int)resp.StatusCode}{hint}");
                    _audit.Log("poll-fail", $"{(int)resp.StatusCode}", ok: false);
                    report.Ok = false;
                    return report;
                }

                if (resp.Headers.TryGetValues("X-ClassExland-Signature", out var values))
                    headerSignature = values.FirstOrDefault();

                raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            if (!SafeConfigWriter.IsValidJson(raw))
            {
                report.Add("清单不是合法 JSON");
                report.Ok = false;
                return report;
            }

            var manifest = JsonSerializer.Deserialize<RemoteManifest>(raw) ?? new RemoteManifest();
            report.Version = manifest.Version;
            report.ETag = manifest.ETag;
            report.Head = manifest.Head;

            if (!string.IsNullOrEmpty(cfg.HmacSecret)
                && !VerifySignature(raw, headerSignature, manifest.Signature, cfg.HmacSecret))
            {
                report.Add("HMAC 签名校验失败");
                _audit.Log("poll-hmac-fail", manifest.Version, ok: false);
                report.Ok = false;
                return report;
            }

            var (lastVersion, lastETag) = _state.Load();
            var upToDate = !string.IsNullOrEmpty(manifest.Version)
                           && manifest.Version == lastVersion && manifest.ETag == lastETag;
            report.Ok = true;
            report.NeedApply = !upToDate;
            var localHashes = RemoteLocalSnapshot.HashAll(_paths);
            report.LocalDirty = RemoteLocalSnapshot.HasChanges(_state.LoadLocalHashes(), localHashes);
            report.Add(upToDate
                ? $"已是最新版本 {manifest.Version}"
                : $"发现新版本 {manifest.Version}（本地 {lastVersion}），可执行 remote.sync");
            _audit.Log("poll", report.Notes[0]);
            return report;
        }
        catch (Exception ex)
        {
            report.Ok = false;
            report.Add($"拉取清单异常：{ex.GetType().Name}: {ex.Message}");
            _audit.Log("poll-exception", ex.Message, ok: false);
            return report;
        }
    }

    /// <summary>一键停用：写入 remote.disabled 并在内存中生效。</summary>
    public void Disable()
    {
        try
        {
            File.WriteAllText(_paths.Get("remote.disabled"),
                DateTimeOffset.Now.ToString("o"));
            _lifetime?.Cancel();
            _audit.Log("disable", "已通过开关停用远程同步");
        }
        catch (Exception ex)
        {
            _audit.Log("disable-fail", ex.Message, ok: false);
        }
    }

    /// <summary>重新启用：删除停用标记。</summary>
    public void Enable()
    {
        try
        {
            var marker = _paths.Get("remote.disabled");
            if (File.Exists(marker)) File.Delete(marker);
            _audit.Log("enable", "已重新启用远程同步（下次启动生效）");
        }
        catch (Exception ex)
        {
            _audit.Log("enable-fail", ex.Message, ok: false);
        }
    }

    /// <summary>
    /// 快照写完后让预备铃模块重新读配置：重载内存 → 把每节课的值同步进档案附加设置 → 时间轴重算。
    /// 只在确实写了 bell.json / bell-options.json 时执行，其他快照不打扰时间轴。
    /// </summary>
    private void RefreshBellRuntime(IReadOnlyList<ApplyFileResult> applied)
    {
        try
        {
            var touched = applied.Any(a =>
                a.Ok && (a.Path.EndsWith(ConfigFiles.Bell, StringComparison.OrdinalIgnoreCase) ||
                         a.Path.EndsWith("bell-options.json", StringComparison.OrdinalIgnoreCase)));
            if (!touched) return;

            var settings = _services.GetService(typeof(Features.Bell.BellSettings)) as Features.Bell.BellSettings;
            settings?.Reload();
            var writer = _services.GetService(typeof(Features.Bell.BellProfileWriter)) as Features.Bell.BellProfileWriter;
            writer?.ApplySlotPreps();
            var timeline = _services.GetService(typeof(Features.Bell.BellTimelineService)) as Features.Bell.BellTimelineService;
            timeline?.InvalidateSchedule();
            _audit.Log("bell-refresh", "远程快照已写入，预备铃配置已热重载", ok: true);
        }
        catch (Exception ex)
        {
            _audit.Log("bell-refresh-fail", ex.Message, ok: false);
        }
    }

    /// <summary>
    /// 拼清单地址。配了设备名就带上 <c>?device=</c>——同一个服务器上每台教室电脑只拿自己那份快照。
    /// </summary>
    private static string BuildManifestUrl(RemoteConfig cfg)
    {
        var url = cfg.ServerUrl.TrimEnd('/') + cfg.ManifestPath;
        var device = (cfg.DeviceName ?? "").Trim();
        if (device.Length == 0) return url;

        var separator = cfg.ManifestPath.Contains('?') ? '&' : '?';
        return url + separator + "device=" + Uri.EscapeDataString(device);
    }

    private static HttpRequestMessage BuildRequest(RemoteConfig cfg, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.BearerToken);

        var device = (cfg.DeviceName ?? "").Trim();
        // 设备名同时放进头里：少数反代/防火墙会吃掉 query，留着当退路。
        if (device.Length > 0)
            req.Headers.TryAddWithoutValidation("X-ClassExland-Device", device);

        var version = typeof(RemoteSyncModule).Assembly.GetName().Version?.ToString();
        if (!string.IsNullOrEmpty(version))
            req.Headers.TryAddWithoutValidation("X-ClassExland-Version", version);

        return req;
    }

    /// <summary>
    /// 拉取服务器上登记的设备名（只含名字、备注、版本与最近拉取时间，不含任何配置内容）。
    /// 设置页的「选择设备」下拉用它，省得手打中文名字打错。
    /// </summary>
    public async Task<(bool Ok, string Message, IReadOnlyList<RemoteDeviceInfo> Devices)> FetchDevicesAsync(
        RemoteConfig cfg, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.ServerUrl) || string.IsNullOrWhiteSpace(cfg.BearerToken))
        {
            return (false, "先填服务器地址与访问令牌。", Array.Empty<RemoteDeviceInfo>());
        }

        var url = cfg.ServerUrl.TrimEnd('/') + cfg.ManifestPath;
        url += (cfg.ManifestPath.Contains('?') ? "&" : "?") + "list=1";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.FetchTimeoutSeconds) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.BearerToken);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var hint = await DescribeHttpFailureAsync(resp, ct).ConfigureAwait(false);
                _audit.Log("device-list", $"HTTP {(int)resp.StatusCode}", ok: false);
                return (false, $"读取设备列表失败：HTTP {(int)resp.StatusCode}{hint}", Array.Empty<RemoteDeviceInfo>());
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("devices", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return (false, "服务器没有返回设备列表（远程站版本可能较旧，可手动填写设备名称）。",
                    Array.Empty<RemoteDeviceInfo>());
            }

            var list = new List<RemoteDeviceInfo>();
            foreach (var el in arr.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new RemoteDeviceInfo(
                    name!,
                    el.TryGetProperty("note", out var note) ? note.GetString() ?? "" : "",
                    el.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "",
                    el.TryGetProperty("last_seen", out var seen) ? seen.GetString() ?? "" : ""));
            }

            _audit.Log("device-list", $"{list.Count} 台", ok: true);
            return (true, list.Count == 0 ? "服务器上还没有登记任何设备。" : $"服务器上有 {list.Count} 台设备。", list);
        }
        catch (Exception ex)
        {
            _audit.Log("device-list", $"{ex.GetType().Name}: {ex.Message}", ok: false);
            return (false, $"读取设备列表失败：{ex.Message}", Array.Empty<RemoteDeviceInfo>());
        }
    }

    /// <summary>
    /// 把服务端的错误码翻成人话。最常见的两种是多设备服务器没指定设备名、以及设备名写错，
    /// 服务端会在 body 里列出可选设备，这里一并显示出来。
    /// </summary>
    private static async Task<string> DescribeHttpFailureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if ((int)resp.StatusCode is not (400 or 401 or 403 or 404 or 409 or 429 or 503)) return "";

        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 400) body = body[..400];

            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;

            var devices = "";
            if (doc.RootElement.TryGetProperty("devices", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                devices = string.Join("、", list.EnumerateArray()
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            return error switch
            {
                "unauthorized" => "（访问令牌不对）",
                "device_required" => devices.Length > 0
                    ? $"（服务器上有多个设备，请在「设备名称」里选一个：{devices}）"
                    : "（服务器还没建任何设备）",
                "unknown_device" => devices.Length > 0
                    ? $"（设备名不存在，可选：{devices}）"
                    : "（设备名不存在）",
                "conflict" => "（本地与服务器版本分叉，上传已进入服务器待审队列）",
                "too_many_devices" => "（服务器设备数已达上限）",
                "station_storage_error" => "（服务器数据文件不可读，请让管理员检查权限）",
                null => "",
                _ => $"（{error}）"
            };
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 校验服务端签名。两种协议：
    ///  1. 响应头 <c>X-ClassExland-Signature</c>：hex(hmac_sha256(secret, 响应原文))，跨语言最容易对齐；
    ///  2. 兼容旧协议：清单里的 <c>signature</c> 字段，对去掉该字段后的紧凑 JSON 计算。
    /// </summary>
    private static bool VerifySignature(string raw, string? headerSignature, string bodySignature, string secret)
    {
        if (!string.IsNullOrWhiteSpace(headerSignature))
            return FixedTimeEquals(ComputeHmac(raw, secret), headerSignature);

        if (!string.IsNullOrWhiteSpace(bodySignature))
            return FixedTimeEquals(ComputeHmac(StripSignature(raw), secret), bodySignature);

        return false;
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected.ToUpperInvariant()),
                Encoding.UTF8.GetBytes(actual.Trim().ToUpperInvariant()));
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeHmac(string rawBody, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)));
    }

    /// <summary>
    /// 去掉顶层 signature 字段后的紧凑 JSON。签名必须能由服务端在"不含 signature"时算出，
    /// 否则对整包求 HMAC 会因签名包含自身而永远不成立。
    /// </summary>
    private static string StripSignature(string raw)
    {
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject obj) return raw;
            obj.Remove("signature");
            return obj.ToJsonString();
        }
        catch
        {
            return raw;
        }
    }

    private static bool IsSafePluginConfigName(string fileName)
        => !string.IsNullOrWhiteSpace(fileName)
           && fileName == Path.GetFileName(fileName)
           && fileName.IndexOf("..", StringComparison.Ordinal) < 0
           && Path.GetExtension(fileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
           // 禁止远程快照改写同步自身的配置与状态，避免被远程关闭或改向。
           && !fileName.Equals(ConfigFiles.Remote, StringComparison.OrdinalIgnoreCase)
           && !fileName.Equals("remote-state.json", StringComparison.OrdinalIgnoreCase)
           && !fileName.Equals("remote.disabled", StringComparison.OrdinalIgnoreCase);
}
