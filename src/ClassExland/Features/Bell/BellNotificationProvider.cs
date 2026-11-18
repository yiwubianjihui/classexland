using ClassExland.Contracts;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Bell;

/// <summary>
/// 预备铃提醒：预告 / 预备铃 / 正式上课各一条。
/// MaskContent 与 OverlayContent 都显式指定 Duration；显示完毕后主动 Cancel，
/// 弹新提醒前先关上一条，避免重复排队或被插件异常禁用。
/// </summary>
[NotificationProviderInfo(BellConstants.NotificationProviderGuidText, "ClassExland 预备铃", "\uE823",
    "预备铃预告、预备铃与正式上课提醒。")]
public sealed class BellNotificationProvider : NotificationProviderBase
{
    private static readonly TimeSpan MaskDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OverlayDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    private readonly IBellTimeline _timeline;
    private readonly ILogger<BellNotificationProvider> _logger;
    private readonly ILessonsService _lessonsService;

    private NotificationRequest? _activeRequest;
    private DateTime _activeCloseAt = DateTime.MinValue;

    public BellNotificationProvider(
        IBellTimeline timeline,
        ILessonsService lessonsService,
        ILogger<BellNotificationProvider> logger)
    {
        _timeline = timeline;
        _lessonsService = lessonsService;
        _logger = logger;

        _timeline.MomentReached += OnMomentReached;
        _lessonsService.PostMainTimerTicked += OnMainTimerTicked;
    }

    private void OnMainTimerTicked(object? sender, EventArgs e)
    {
        try
        {
            CloseFinishedReminder();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭预备铃提醒时出现异常，已忽略。");
        }
    }

    private void OnMomentReached(object? sender, BellMoment moment)
    {
        try
        {
            if (moment.Kind is not (BellMomentKind.PreClassNotice or BellMomentKind.PrepareBell or BellMomentKind.ClassOnNotice))
                return;

            var (title, text, icon) = Describe(moment);
            if (string.IsNullOrWhiteSpace(text)) return;

            var request = new NotificationRequest
            {
                MaskContent = NotificationContent.CreateTwoIconsMask(
                    title, rightIcon: icon, factory: x => x.Duration = MaskDuration),
                OverlayContent = NotificationContent.CreateSimpleTextContent(
                    text, x => x.Duration = OverlayDuration)
            };

            // 先关上一条，避免叠加占用主界面。
            CancelActive();
            _activeRequest = request;
            _activeCloseAt = DateTime.Now + OverlayDuration + CloseGrace;
            ShowNotification(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "显示预备铃提醒时出现异常，已忽略。");
        }
    }

    private void CloseFinishedReminder()
    {
        if (_activeRequest == null || DateTime.Now < _activeCloseAt) return;
        CancelActive();
    }

    private void CancelActive()
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
            _logger.LogWarning(ex, "取消上一条预备铃提醒时出现异常，已忽略。");
        }
    }

    private static (string Title, string Text, string Icon) Describe(BellMoment moment) => moment.Kind switch
    {
        BellMomentKind.PreClassNotice =>
            ("预备铃预告", $"距离预备铃还有 60 秒，请回到座位并保持安静。", "\uE823"),
        BellMomentKind.PrepareBell =>
            ("预备铃", "预备铃响，请保持安静，马上开始上课。", "\uE823"),
        BellMomentKind.ClassOnNotice =>
            ("上课", "现在正式上课。", "\uE7AD"),
        _ => ("", "", "")
    };
}
