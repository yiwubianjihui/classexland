using System.Text.Json;
using ClassExland.Contracts;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Bell;

/// <summary>
/// 预备铃提醒：预告 / 预备铃两条。
///
/// 正式上课（ClassOnNotice）不再由本插件弹窗——宿主原生已有正式上课提示，
/// 插件只负责"上课前"的预告与预备铃，避免同一时刻两次弹窗造成闪烁。
///
/// 文案优先取该课节所属科目的「上课提示」（ClassOnPreparingText）；
/// 科目提醒开关关闭（IsAttachSettingsEnabled=false）时回退插件默认文案，
/// 绝不改动宿主的开关——用户没启用的提示，插件不替他启用。
/// </summary>
[NotificationProviderInfo(BellConstants.NotificationProviderGuidText, "ClassExland 预备铃", "\uE823",
    "预备铃预告与预备铃提醒；文案可用各科目的上课提示。")]
public sealed class BellNotificationProvider : NotificationProviderBase
{
    private static readonly TimeSpan MaskDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OverlayDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    private readonly IBellTimeline _timeline;
    private readonly ILogger<BellNotificationProvider> _logger;
    private readonly ILessonsService _lessonsService;
    private readonly IProfileService _profileService;

    private NotificationRequest? _activeRequest;
    private DateTime _activeCloseAt = DateTime.MinValue;

    public BellNotificationProvider(
        IBellTimeline timeline,
        ILessonsService lessonsService,
        IProfileService profileService,
        ILogger<BellNotificationProvider> logger)
    {
        _timeline = timeline;
        _lessonsService = lessonsService;
        _profileService = profileService;
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
            // 只弹"上课前"的两条；正式上课交宿主原生，避免双弹窗闪烁。
            if (moment.Kind is not (BellMomentKind.PreClassNotice or BellMomentKind.PrepareBell))
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

    /// <summary>
    /// 生成文案：默认文案打底，能用科目的上课提示就替换正文。
    /// </summary>
    private (string Title, string Text, string Icon) Describe(BellMoment moment)
    {
        var (title, fallback, icon) = moment.Kind switch
        {
            BellMomentKind.PreClassNotice =>
                ("预备铃预告", "距离预备铃还有 60 秒，请回到座位并保持安静。", "\uE823"),
            BellMomentKind.PrepareBell =>
                ("预备铃", "预备铃响，请保持安静，马上开始上课。", "\uE823"),
            _ => ("", "", "")
        };
        if (string.IsNullOrWhiteSpace(title)) return (title, fallback, icon);

        var subjectText = TryGetSubjectPrepareText(moment);
        var text = string.IsNullOrWhiteSpace(subjectText) ? fallback : subjectText!;
        return (title, text, icon);
    }

    /// <summary>
    /// 取该课节所属科目已启用的「上课提示」（ClassOnPreparingText）。
    /// 科目提醒开关关闭、课节/科目找不到、字段缺失时都返回 null（回退默认文案）。
    /// 只读不改——绝不会把宿主的 IsAttachSettingsEnabled 改成 true。
    /// </summary>
    private string? TryGetSubjectPrepareText(BellMoment moment)
    {
        try
        {
            var plan = _lessonsService.CurrentClassPlan;
            var profile = _profileService.Profile;
            if (plan?.Classes is null || profile?.Subjects is null) return null;

            // 用课节键（布局GUID|开始|结束）定位当前课属于哪一节。
            var classInfo = plan.Classes.FirstOrDefault(c =>
                c.CurrentTimeLayoutItem is { } item &&
                ItemKeyMatchesMoment(item, moment.TimeLayoutItemId));
            if (classInfo is null || !profile.Subjects.TryGetValue(classInfo.SubjectId, out var subject) || subject is null)
                return null;

            if (!subject.AttachedObjects.TryGetValue(BellConstants.HostNotificationAttachedGuid, out var host))
                return null;

            // 用户没启用这一科的上课提示 → 不用它的文案（但不改开关）。
            if (TryGetBool(host, "IsAttachSettingsEnabled", out var enabled) && !enabled) return null;
            if (TryGetBool(host, "IsClassOnPreparingNotificationEnabled", out var notifyEnabled) && !notifyEnabled)
                return null;

            return TryGetString(host, "ClassOnPreparingText");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取科目上课提示失败，使用默认文案。");
            return null;
        }
    }

    private static bool ItemKeyMatchesMoment(TimeLayoutItem item, string momentKey)
    {
        // BellProfileWriter.MakeSlotKey 生成 "layoutId|HH:MM:SS|HH:MM:SS"；
        // 这里只比较结束的 起止时间 片段，避免依赖当前课表的 GUID。
        var suffix = $"|{item.StartTime:hh\\:mm\\:ss}|{item.EndTime:hh\\:mm\\:ss}";
        return momentKey.EndsWith(suffix, StringComparison.Ordinal);
    }

    private static bool TryGetBool(object? obj, string propertyName, out bool value)
    {
        value = false;
        if (obj == null) return false;
        if (obj is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(propertyName, out var prop) &&
                prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = prop.GetBoolean();
                return true;
            }
            return false;
        }
        var pi = obj.GetType().GetProperty(propertyName);
        if (pi != null && pi.PropertyType == typeof(bool))
        {
            value = (bool)(pi.GetValue(obj) ?? false);
            return true;
        }
        return false;
    }

    private static string? TryGetString(object? obj, string propertyName)
    {
        if (obj == null) return null;
        if (obj is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(propertyName, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
            return null;
        }
        var pi = obj.GetType().GetProperty(propertyName);
        return pi?.GetValue(obj)?.ToString();
    }
}
