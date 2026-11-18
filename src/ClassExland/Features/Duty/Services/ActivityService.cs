using System.Globalization;
using ClassExland.Contracts;
using ClassExland.Features.Duty.Migration;
using ClassExland.Features.Duty.Models;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Duty.Services;

/// <summary>
/// 特殊活动服务：月度/周度/自定义起始轮换、周确认、名单解析。
/// 活动触发时间由 BellModule 提供，本服务不起定时器。
/// </summary>
public sealed class ActivityService
{
    private static readonly char[] NameSeparators =
        { '、', '，', ',', ' ', '/', '；', ';', '\n', '\r', '\t', '　' };

    private readonly IPluginPaths _paths;
    private readonly ILogger<ActivityService> _logger;

    public ActivitySettings Settings { get; }

    public ActivityService(IPluginPaths paths, ActivitySettings settings, ILogger<ActivityService> logger)
    {
        _paths = paths;
        Settings = settings;
        _logger = logger;
        Settings.ConfigPath = paths.Get(ConfigFiles.Activity);
    }

    private const string OptionsFile = "activity-options.json";

    public static ActivitySettings Load(IPluginPaths paths)
    {
        var path = paths.Get(ConfigFiles.Activity);
        var settings = ActivitySettings.LoadFromFile(path, Path.Combine(paths.ConfigDirectory, OptionsFile));
        if (settings.Activities.Count == 0)
            settings.Activities.Add(ActivityPresets.CreateTrialHomeworkPreset());
        return settings;
    }

    public void Save()
    {
        try
        {
            if (Settings.OptionsPath == null)
                Settings.OptionsPath = Path.Combine(_paths.ConfigDirectory, OptionsFile);
            Settings.SaveToFile();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存特殊活动配置失败。");
        }
    }

    public ActivityGroup? GetEffectiveGroup(SpecialActivity activity, DateTime date)
    {
        var groups = activity.Groups;
        if (groups.Count == 0) return null;
        int index;
        switch (activity.RotationMode)
        {
            case RotationMode.Month:
                index = (date.Month - 1) % groups.Count;
                break;
            case RotationMode.Week:
                var cal = CultureInfo.CurrentCulture.Calendar;
                var week = cal.GetWeekOfYear(date, CalendarWeekRule.FirstDay, DayOfWeek.Monday);
                var startWeek = cal.GetWeekOfYear(activity.CustomStartDate, CalendarWeekRule.FirstDay, DayOfWeek.Monday);
                index = Math.Abs(week - startWeek) % groups.Count;
                break;
            case RotationMode.Day:
                var days = Math.Max(0, (date.Date - activity.CustomStartDate.Date).Days);
                index = (days / 7) % groups.Count;
                break;
            default:
                index = Math.Clamp(activity.ActiveGroupIndex, 0, groups.Count - 1);
                break;
        }
        return groups[Math.Clamp(index, 0, groups.Count - 1)];
    }

    public IReadOnlyList<string> GetNames(SpecialActivity activity, DateTime date)
    {
        var group = GetEffectiveGroup(activity, date);
        if (group == null) return Array.Empty<string>();
        return group[date.DayOfWeek]
            .Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public string GetNamesText(SpecialActivity activity, DateTime date)
        => string.Join("、", GetNames(activity, date));

    public static string WeekKey(DateTime date)
    {
        var cal = CultureInfo.CurrentCulture.Calendar;
        var week = cal.GetWeekOfYear(date, CalendarWeekRule.FirstDay, DayOfWeek.Monday);
        return $"{date.Year}-W{week:00}";
    }

    public bool IsWeekConfirmed(SpecialActivity activity, DateTime date)
        => !activity.WeekConfirmed || activity.ConfirmedWeekKey == WeekKey(date);

    public void ConfirmCurrentWeek(SpecialActivity activity, DateTime date)
    {
        activity.WeekConfirmed = true;
        activity.ConfirmedWeekKey = WeekKey(date);
        Save();
    }

    public void AddTrialHomeworkPreset()
    {
        Settings.Activities.Add(ActivityPresets.CreateTrialHomeworkPreset());
        Save();
    }
}
