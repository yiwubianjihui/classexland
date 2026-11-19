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
public sealed class RemoteSyncModule : IHostedService, IRemoteSyncService
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
                        LastMessage = _lastReport?.Notes.Count > 0 ? _lastReport.Notes[^1] : ""
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
                var url = cfg.ServerUrl + cfg.ManifestPath;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.BearerToken);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    report.Add($"下载失败：HTTP {(int)resp.StatusCode}");
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
                if (_dispatcher is null)
                {
                    report.Add("命令层（AddCommandBridge）未注册，跳过指令队列");
                    _audit.Log("command-skip", "ICommandDispatcher 未注册", ok: false);
                }
                else
                {
                    foreach (var command in manifest.Commands)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var request = new CommandRequest(
                                command.Command, command.Args, "remote", command.Apply, command.DryRun);
                            var result = await _dispatcher.ExecuteAsync(request, ct).ConfigureAwait(false);
                            report.Add($"指令 {command.Command}: {(result.Ok ? "ok" : result.Message)}");
                            _audit.Log("command", $"{command.Command}: {result.Message}", ok: result.Ok);
                        }
                        catch (Exception ex)
                        {
                            report.Add($"指令 {command.Command} 异常：{ex.Message}");
                            _audit.Log("command-exception", $"{command.Command}: {ex.Message}", ok: false);
                        }
                    }
                }
            }

            // 8) 记录版本，完成
            _state.SaveApplied(manifest.Version, manifest.ETag);
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
                var url = cfg.ServerUrl + cfg.ManifestPath;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.BearerToken);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    report.Add($"拉取清单失败：HTTP {(int)resp.StatusCode}");
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
