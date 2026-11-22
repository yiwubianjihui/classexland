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
            fresh = LuckyHistory.FindNew(entries, _known, skipAllWhenEmpty: !_primed && cfg.SkipExistingOnStart).ToList();

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
            var text = cfg.MessageTemplate.Replace("{names}", LuckyHistory.FormatNames(names));
            Broadcast(cfg, text);
        }
    }

    private void Broadcast(LuckyConfig cfg, string text)
    {
        // CreateTwoIconsMask 内部会 new LucideIconSource（Avalonia 控件对象），
        // 只能在 UI 线程构造；本服务由 PostMainTimerTicked 驱动，所在线程不保证是 UI 线程
        // （实测抛过 "Call from invalid thread"）。统一 Post 到 UI 线程再发。
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) BroadcastCore(cfg, text);
            else Dispatcher.UIThread.Post(() => BroadcastCore(cfg, text));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "调度 LuckyPicker 广播失败。");
        }
    }

    private void BroadcastCore(LuckyConfig cfg, string text)
    {
        try
        {
            var maskDuration = TimeSpan.FromSeconds(cfg.MaskDurationSeconds);
            var overlayDuration = TimeSpan.FromSeconds(cfg.OverlayDurationSeconds);

            var request = new NotificationRequest
            {
                MaskContent = NotificationContent.CreateTwoIconsMask(
                    cfg.MaskTitle,
                    rightIcon: "\uE823",
                    factory: x =>
                    {
                        x.Duration = maskDuration;
                        x.SpeechContent = cfg.EnableSpeech ? text : null;
                    }),
                OverlayContent = NotificationContent.CreateSimpleTextContent(
                    text,
                    x => x.Duration = overlayDuration)
            };

            // 先关上一条，避免叠在一起占满空间。
            CloseActive();

            _active = request;
            _activeCloseAt = DateTime.Now + overlayDuration + CloseGrace;
            ShowNotification(request);
            _logger.LogInformation("已广播 LuckyPicker 抽选：{Text}", text);
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
