using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using ClassExland.Contracts;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Lucky;

/// <summary>
/// LuckyPicker → ClassIsland 广播。
///
/// LuckyPicker 是完全独立的程序（WinForms + WPF 两版），没有 IPC 也没有插件接口，
/// 唯一稳定的对外事实是它会把每次抽选写进 %LOCALAPPDATA%\LuckyPicker\history.json。
/// 所以这里用「低频轮询这个文件 + 指纹去重」来捕获新抽选，再用宿主的提醒通道广播，
/// 不动 LuckyPicker 一行代码，也不要求它和 ClassIsland 同时开在特定顺序。
///
/// 定时器沿用宿主的 PostMainTimerTicked（每秒），不自己 new Timer——
/// 与项目里其他模块一致，也避免 NotificationProviderBase.StartAsync 非虚导致的坑。
/// </summary>
[NotificationProviderInfo(LuckyConstants.NotificationProviderGuidText, "ClassExland 幸运摇人", "\uE823",
    "把 LuckyPicker 抽到的名字在 ClassIsland 上广播出来。")]
public sealed class LuckyBroadcastService : NotificationProviderBase, ILuckyService
{
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    private readonly LuckyConfigStore _store;
    private readonly ILessonsService _lessons;
    private readonly ILogger<LuckyBroadcastService> _logger;

    private readonly object _gate = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pushedIds = new(StringComparer.Ordinal);
    /// <summary>推送后这段时间内，轮询不再把同一条当新记录（吸收 history.json 写入时间差）。</summary>
    private const int SuppressSeconds = 8;
    private DateTime _suppressUntil = DateTime.MinValue;
    /// <summary>刚推送过的人名；轮询据此跳过对应记录，避免「抽一次播两次」。</summary>
    private readonly HashSet<string> _suppressNames = new(StringComparer.Ordinal);
    private bool _primed;
    private DateTime _lastPollAt = DateTime.MinValue;
    private string _lastHash = "";

    private NotificationRequest? _active;
    private DateTime _activeCloseAt = DateTime.MinValue;

    public LuckyBroadcastService(
        LuckyConfigStore store,
        ILessonsService lessons,
        ILogger<LuckyBroadcastService> logger)
    {
        _store = store;
        _lessons = lessons;
        _logger = logger;
        _lessons.PostMainTimerTicked += OnTick;
    }

    /// <summary>当前配置与最近状态（CLI 只读查询用）。</summary>
    public object? Status
    {
        get
        {
            try
            {
                var cfg = _store.Load();
                var path = cfg.ResolveHistoryPath();
                lock (_gate)
                {
                    return new
                    {
                        Enabled = cfg.Enabled,
                        HistoryPath = path,
                        HistoryExists = File.Exists(path),
                        PollIntervalSeconds = cfg.PollIntervalSeconds,
                        KnownCount = _known.Count,
                        Primed = _primed,
                        LastPollAt = _lastPollAt == DateTime.MinValue ? (DateTime?)null : _lastPollAt
                    };
                }
            }
            catch (Exception ex)
            {
                return new { Enabled = false, Error = ex.Message };
            }
        }
    }

    /// <summary>手动广播一段文本（设置页/CLI 试用）。</summary>
    public async Task<string> AnnounceAsync(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return "内容为空，未广播";
            var cfg = _store.Load();
            Broadcast(cfg, text.Trim());
            await Task.CompletedTask;
            return "已广播：" + text.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "手动广播失败。");
            return "广播失败：" + ex.Message;
        }
    }

    /// <summary>
    /// LuckyPicker 主动推送的入口（幂等）。
    /// LuckyPicker 抽完立刻经本机命名管道调用，省掉轮询那 0~2 秒的等待；
    /// 同一个 id 重复推送只广播一次，管道抖动或重试都不会播两遍。
    /// </summary>
    public async Task<string> PushAsync(string namesText, string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(namesText)) return "内容为空，未广播";

            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(id) && !_pushedIds.Add(id))
                    return "该抽选已广播过（id 重复）";
                if (_pushedIds.Count > 200) _pushedIds.Clear();

                // 压制窗口：刚推来的这条，轮询在 8 秒内不再判为新记录。
                // 光靠 _known 不行——推送时还拿不到 history.json 里那条记录的 time，
                // 指纹与轮询算出来的不一致，下一拍就会被当作新记录再播一次（抽一次播两次）。
                _suppressUntil = DateTime.Now.AddSeconds(SuppressSeconds);
                _suppressNames.Clear();
                foreach (var n in LuckyHistory.ExtractNames(namesText)) _suppressNames.Add(n);
            }

            var cfg = _store.Load();
            Broadcast(cfg, namesText.Trim());
            await Task.CompletedTask;
            return "已广播：" + namesText.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理 LuckyPicker 推送失败。");
            return "推送失败：" + ex.Message;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            CloseFinished();
            Poll();
        }
        catch (Exception ex)
        {
            // 这个回调抛异常会让宿主把整个插件判为异常并自动禁用，必须吞掉。
            _logger.LogWarning(ex, "LuckyPicker 联动轮询异常，已忽略。");
        }
    }

    private void Poll()
    {
        var cfg = _store.Load();
        if (!cfg.Enabled) return;

        var now = DateTime.Now;
        if ((now - _lastPollAt).TotalSeconds < cfg.PollIntervalSeconds) return;
        _lastPollAt = now;

        var path = cfg.ResolveHistoryPath();
        if (!File.Exists(path)) return;

        string raw;
        try
        {
            // LuckyPicker 用 File.WriteAllText 整文件覆盖，可能撞上写一半；
            // 读失败就跳过这一拍，下一拍再试，绝不把残缺内容当成新记录。
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            raw = reader.ReadToEnd();
        }
        catch (IOException)
        {
            return;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        lock (_gate)
        {
            if (hash == _lastHash) return;
            _lastHash = hash;
        }

        var entries = LuckyHistory.Parse(raw);
        if (entries is null)
        {
            _logger.LogWarning("LuckyPicker history.json 格式无法识别，本次跳过。");
            return;
        }

        List<LuckyHistoryEntry> fresh;
        lock (_gate)
        {
            var candidates = LuckyHistory.FindNew(entries, _known, skipAllWhenEmpty: !_primed && cfg.SkipExistingOnStart).ToList();

            // 推送刚播过的记录：这一拍跳过，否则同一次抽选会播两遍。
            // 按人名剔除，不误伤同一时刻产生的其他抽选。
            if (DateTime.Now < _suppressUntil && _suppressNames.Count > 0)
            {
                candidates = candidates.Where(e =>
                {
                    var ns = LuckyHistory.ExtractNames(e.Text);
                    return !(ns.Count > 0 && ns.All(_suppressNames.Contains));
                }).ToList();
            }
            if (DateTime.Now >= _suppressUntil) _suppressNames.Clear();

            fresh = candidates;

            // 首次读到文件时把已有记录全部标记为"已见"，避免开机把整本历史广播一遍。
            foreach (var e in entries) _known.Add(LuckyHistory.Fingerprint(e));
            _primed = true;

            // 历史被清空后指纹集合要跟着缩，否则清空再抽同名会被误判为旧记录。
            if (entries.Count == 0) _known.Clear();
        }

        foreach (var entry in fresh)
        {
            var names = LuckyHistory.ExtractNames(entry.Text);
            if (names.Count == 0) continue;
            Broadcast(cfg, LuckyHistory.FormatNames(names));
        }
    }

    private void Broadcast(LuckyConfig cfg, string namesText)
    {
        // 遮罩上要显示的是「人名」，正文才是「抽到了……」这句话——
        // 之前遮罩写的是固定标题（幸运摇人），信息密度太低。
        var names = LuckyHistory.ExtractNames(namesText);
        var display = names.Count > 0 ? LuckyHistory.FormatNames(names) : namesText.Trim();
        var overlay = BuildOverlayText(cfg, display);

        // CreateTwoIconsMask 内部会 new LucideIconSource（Avalonia 控件对象），
        // 只能在 UI 线程构造；本服务由 PostMainTimerTicked 驱动，所在线程不保证是 UI 线程
        // （实测抛过 "Call from invalid thread"）。统一 Post 到 UI 线程再发。
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) BroadcastCore(cfg, display, overlay);
            else Dispatcher.UIThread.Post(() => BroadcastCore(cfg, display, overlay));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "调度 LuckyPicker 广播失败。");
        }
    }

    /// <summary>正文文案：默认「抽到了：张三」，可用 {names} 自定义。</summary>
    private static string BuildOverlayText(LuckyConfig cfg, string names)
    {
        var template = string.IsNullOrWhiteSpace(cfg.OverlayTemplate)
            ? "抽到了：{names}"
            : cfg.OverlayTemplate;
        return template.Replace("{names}", names);
    }

    private void BroadcastCore(LuckyConfig cfg, string maskText, string overlayText)
    {
        try
        {
            var overlayDuration = TimeSpan.FromSeconds(cfg.OverlayDurationSeconds);
            // 横幅只有 1 秒左右就退场：宿主的 MainWindowLine 会无条件解引用 MaskContent，
            // 传 null 会直接抛 NullReferenceException（22:43 那次崩溃就是这么来的）。
            // 所以这里始终给一个短横幅，观感上"一闪而过"，不挡课表。
            var maskDuration = cfg.EnableMask
                ? TimeSpan.FromSeconds(cfg.MaskDurationSeconds)
                : TimeSpan.FromSeconds(1);

            // MaskContent 必须非 null —— 这是宿主 API 的硬性前提。
            var mask = NotificationContent.CreateTwoIconsMask(
                maskText,
                leftIcon: cfg.MaskLeftIcon,
                rightIcon: cfg.MaskRightIcon,
                hasRightIcon: !string.IsNullOrEmpty(cfg.MaskRightIcon),
                factory: x =>
                {
                    x.Duration = maskDuration;
                });

            var request = new NotificationRequest
            {
                MaskContent = mask,
                OverlayContent = NotificationContent.CreateSimpleTextContent(
                    overlayText,
                    x =>
                    {
                        x.Duration = overlayDuration;
                        if (cfg.EnableSpeech) x.SpeechContent = overlayText;
                    })
            };

            // 先关上一条，避免叠在一起占满空间。
            CloseActive();

            _active = request;
            // 关闭时机按两者里较长的那个算，横幅先退、正文后收。
            _activeCloseAt = DateTime.Now + (overlayDuration > maskDuration ? overlayDuration : maskDuration) + CloseGrace;
            ShowNotification(request);
            _logger.LogInformation("已广播 LuckyPicker 抽选：{Overlay}", overlayText);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "广播 LuckyPicker 抽选失败。");
        }
    }

    /// <summary>
    /// 显示完主动 Cancel：宿主的提醒在"票据被取消但请求没结束"时会被重新排队再播一次，
    /// 只设 Duration 不能保证它真的消失（试写作业插件踩过同样的坑）。
    /// </summary>
    private void CloseFinished()
    {
        if (_active is null || DateTime.Now < _activeCloseAt) return;
        CloseActive();
    }

    private void CloseActive()
    {
        var request = _active;
        _active = null;
        if (request is null) return;
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) request.Cancel();
            else Dispatcher.UIThread.Post(() => { try { request.Cancel(); } catch { } });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "关闭上一条广播时出现异常，已忽略。");
        }
    }
}
