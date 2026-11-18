using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using ClassExland.Contracts;
using ClassExland.Features.Duty.Models;
using ClassExland.Features.Duty.Services;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Duty.Reminders;

/// <summary>
/// 值日与活动提醒：
/// - 收到 BellMomentKind.ClassEnd（At 已含 T_end + D_bell）立即弹值日提醒，不再二次延迟；
/// - 特殊活动开始前 N 分钟提前提示，并附带当前值日名单；
/// - 每条通知显式 Duration、显示完主动 Cancel()、弹新提醒前先关上一条。
/// 时间来自 IBellTimeline 事件 + 宿主主计时器，不自己起定时器、不重写非虚的 StartAsync。
/// </summary>
[NotificationProviderInfo("7C1F5A92-4E63-4D18-B0A7-2F9E6D4C8B31", "ClassExland 值日提醒", "\uE823",
    "下课后弹出值日名单（延迟由 BellModule 统一控制）；特殊活动开始前提前提示并附带值日名单。")]
public sealed class DutyReminderProvider : NotificationProviderBase
{
    private readonly DutySettings _duty;
    private readonly ActivitySettings _activity;
    private readonly DutyService _dutyService;
    private readonly ActivityService _activityService;
    private readonly IBellTimeline _bell;
    private readonly ILessonsService _lessons;
    private readonly IExactTimeService _time;
    private readonly ILogger<DutyReminderProvider> _logger;

    private readonly HashSet<string> _firedActivityKeys = new();

    private NotificationRequest? _activeRequest;
    private DateTime _activeRequestCloseAt = DateTime.MinValue;

    public DutyReminderProvider(
        DutySettings duty,
        ActivitySettings activity,
        DutyService dutyService,
        ActivityService activityService,
        IBellTimeline bell,
        ILessonsService lessons,
        IExactTimeService time,
        ILogger<DutyReminderProvider> logger)
    {
        _duty = duty;
        _activity = activity;
        _dutyService = dutyService;
        _activityService = activityService;
        _bell = bell;
        _lessons = lessons;
        _time = time;
        _logger = logger;

        try
        {
            _bell.MomentReached += OnMomentReached;
            _lessons.PostMainTimerTicked += OnPostMainTimerTicked;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "订阅铃声时间轴失败。");
        }
    }

    private void OnMomentReached(object? sender, BellMoment moment)
    {
        try
        {
            if (moment == null) return;
            switch (moment.Kind)
            {
                case BellMomentKind.ClassEnd:
                    // 以 Bell 为唯一时间真相源：moment.At 已含 T_end + D_bell，直接触发，不再二次延迟。
                    if (_duty.EnableClassEndReminder)
                        ShowDutyReminder();
                    break;

                case BellMomentKind.PrepareBell:
                case BellMomentKind.ClassOnNotice:
                    // 期间又进入上课：关闭可能仍在显示的下课值日提醒。
                    CloseActive();
                    break;

                case BellMomentKind.ActivityAdvanceNotice:
                    ShowActivityReminder(moment.Subject);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理铃声时刻时出现异常。");
        }
    }

    private void OnPostMainTimerTicked(object? sender, EventArgs e)
    {
        try
        {
            CloseFinishedReminder();

            var now = _time.GetCurrentLocalDateTime();
            CheckActivityAdvance(now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查值日/活动提醒时出现异常。");
        }
    }

    /// <summary>
    /// 若 BellModule 未发出 ActivityAdvanceNotice，则在宿主主计时器上按活动配置做提前检查。
    /// 不另起定时器；用“日期+活动+时点”去重，避免与事件触发重复弹窗。
    /// </summary>
    private void CheckActivityAdvance(DateTime now)
    {
        if (!_activity.EnableAdvanceNotice) return;
        foreach (var activity in _activity.Activities.Where(a => a.Enabled))
        {
            if (activity.SchoolDaysOnly && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (!_activityService.IsWeekConfirmed(activity, now)) continue;

            foreach (var reminderTime in activity.ReminderTimes)
            {
                var fireAt = now.Date + reminderTime - TimeSpan.FromMinutes(_activity.AdvanceMinutes);
                var elapsed = now - fireAt;
                if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(90)) continue;

                var key = $"{activity.Id}|{now:yyyy-MM-dd}|{reminderTime:hh\\:mm}";
                if (_firedActivityKeys.Contains(key)) continue;
                _firedActivityKeys.Add(key);
                TrimFiredKeys(now);
                ShowActivityReminder(activity.Name);
            }
        }
    }

    private void TrimFiredKeys(DateTime now)
    {
        if (_firedActivityKeys.Count < 200) return;
        var prefix = now.Date.ToString("yyyy-MM-dd");
        _firedActivityKeys.RemoveWhere(k => !k.Contains(prefix, StringComparison.Ordinal));
    }

    /// <summary>显示下课后值日提醒（供设置页“测试提醒”直接调用）。</summary>
    public void ShowDutyReminder()
    {
        try
        {
            var dutyText = _dutyService.GetDutyText(_time.GetCurrentLocalDateTime());
            if (string.IsNullOrWhiteSpace(dutyText)) dutyText = "暂无安排";

            var body = _duty.ReminderTemplate.Contains("{items}", StringComparison.Ordinal)
                ? _duty.ReminderTemplate.Replace("{items}", dutyText)
                : dutyText;
            var hasSweep = _dutyService.CurrentAt(DateTimeOffset.Now)?.Items.Any(i => i.IsSweep) == true;

            var request = new NotificationRequest
            {
                MaskContent = NotificationContent.CreateTwoIconsMask(
                    _duty.ReminderTitle,
                    rightIcon: "\uE70F",
                    factory: c => c.Duration = TimeSpan.FromSeconds(_duty.MaskDurationSeconds)),
                OverlayContent = NotificationContent.CreateSimpleTextContent(
                    body + (hasSweep ? "\n（含大扫除，建议整屏展示）" : ""),
                    c =>
                    {
                        c.Duration = TimeSpan.FromSeconds(_duty.OverlayDurationSeconds);
                        c.IsSpeechEnabled = _duty.EnableTtsSpeech;
                        if (_duty.EnableTtsSpeech) c.SpeechContent = dutyText;
                    })
            };
            Show(request, TimeSpan.FromSeconds(_duty.OverlayDurationSeconds) + TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "弹出值日提醒失败。");
        }
    }

    /// <summary>显示特殊活动提醒，并附带当前值日名单。</summary>
    public void ShowActivityReminder(string? activityNameOrSubject)
    {
        try
        {
            var activity = _activity.Activities.FirstOrDefault(a => a.Enabled && a.Name == activityNameOrSubject)
                           ?? _activity.Activities.FirstOrDefault(a => a.Enabled);
            if (activity == null) return;

            var now = _time.GetCurrentLocalDateTime();
            var names = _activityService.GetNamesText(activity, now);
            if (string.IsNullOrWhiteSpace(names)) names = "（今日暂无名单）";

            var body = activity.MessageTemplate.Contains("{names}", StringComparison.Ordinal)
                ? activity.MessageTemplate.Replace("{names}", names)
                : $"{activity.MessageTemplate}\n{names}";

            if (activity.IncludeDutyNames)
            {
                var dutyText = _dutyService.GetDutyText(now);
                body += "\n今日值日：" + (string.IsNullOrWhiteSpace(dutyText) ? "暂无安排" : dutyText);
            }

            var request = new NotificationRequest
            {
                MaskContent = NotificationContent.CreateTwoIconsMask(
                    activity.Name + "（提前" + _activity.AdvanceMinutes + "分钟）",
                    rightIcon: "\uE823",
                    factory: c => c.Duration = TimeSpan.FromSeconds(_duty.MaskDurationSeconds)),
                OverlayContent = NotificationContent.CreateSimpleTextContent(
                    body,
                    c =>
                    {
                        c.Duration = TimeSpan.FromSeconds(_duty.OverlayDurationSeconds);
                        c.IsSpeechEnabled = _duty.EnableTtsSpeech;
                        if (_duty.EnableTtsSpeech) c.SpeechContent = names;
                    })
            };
            Show(request, TimeSpan.FromSeconds(_duty.OverlayDurationSeconds) + TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "弹出活动提醒失败。");
        }
    }

    private void Show(NotificationRequest request, TimeSpan displayWindow)
    {
        CloseActive();
        _activeRequest = request;
        _activeRequestCloseAt = DateTime.Now + displayWindow;
        ShowNotification(request);
    }

    private void CloseFinishedReminder()
    {
        if (_activeRequest == null || DateTime.Now < _activeRequestCloseAt) return;
        CloseActive();
    }

    private void CloseActive()
    {
        var request = _activeRequest;
        _activeRequest = null;
        if (request == null) return;
        try
        {
            request.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭上一条提醒时出现异常。");
        }
    }
}
