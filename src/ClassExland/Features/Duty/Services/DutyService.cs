using System.Globalization;
using System.Text;
using ClassExland.Contracts;
using ClassExland.Features.Duty.Models;
using ClassExland.Features.Duty.Migration;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Duty.Services;

/// <summary>
/// 值日查询服务：实现 IDutyQuery，并承担配置持久化、预览、勾选、导出等本地逻辑。
/// 所有数据仅存本地，不上传任何学生数据。
/// </summary>
public sealed class DutyService : IDutyQuery
{
    private readonly IPluginPaths _paths;
    private readonly ILogger<DutyService> _logger;

    public DutySettings Settings { get; }

    public DutyService(IPluginPaths paths, DutySettings settings, ILogger<DutyService> logger)
    {
        _paths = paths;
        Settings = settings;
        _logger = logger;
        Settings.ConfigPath = paths.Get(ConfigFiles.Duty);
    }

    public static DutySettings Load(IPluginPaths paths)
    {
        var path = paths.Get(ConfigFiles.Duty);
        var settings = ConfigureFileHelper.LoadConfig<DutySettings>(path);
        settings.ConfigPath = path;
        if (settings.Plans.Count == 0)
        {
            settings.Plans.Add(new DutyPlan
            {
                Name = "值日表",
                TimeSlots = new System.Collections.ObjectModel.ObservableCollection<TimeSlot>
                {
                    new() { Label = "上午", Start = new TimeSpan(7, 0, 0), End = new TimeSpan(11, 50, 0) },
                    new() { Label = "下午", Start = new TimeSpan(13, 30, 0), End = new TimeSpan(17, 5, 0) }
                }
            });
        }

        // 旧配置常把「07:00」当标签用（迁移脚本早期版本就是这样），界面上会直接露出具体时间。
        // 这里换成上午/下午/傍晚，并把值日项的时段引用一起改掉，改完顺手落盘一次。
        var changed = false;
        foreach (var plan in settings.Plans)
        {
            changed |= DutySlotLabels.Normalize(plan);
        }
        if (changed)
        {
            try
            {
                ConfigureFileHelper.SaveConfig(path, settings);
            }
            catch
            {
                // 落盘失败不影响本次使用：内存里已经是新标签了。
            }
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            ConfigureFileHelper.SaveConfig(Settings.ConfigPath ?? _paths.Get(ConfigFiles.Duty), Settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存值日表配置失败。");
        }
    }

    public DutyView? CurrentAt(DateTimeOffset at)
    {
        try
        {
            var views = BuildSlotViews(DateOnly.FromDateTime(at.LocalDateTime));
            if (views.Count == 0) return null;
            var time = at.LocalDateTime.TimeOfDay;

            var inSlot = views.FirstOrDefault(v => v.Start <= time && time < v.End);
            if (inSlot != null) return inSlot.View;

            // 课间/放学后：展示最近一个已开始的时段；首节课前展示第一个时段。
            var started = views.Where(v => v.Start <= time).OrderByDescending(v => v.Start).FirstOrDefault();
            return (started ?? views[0]).View;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询当前值日失败。");
            return null;
        }
    }

    public IReadOnlyList<DutyView> ForDate(DateOnly date)
    {
        try
        {
            return BuildSlotViews(date).Select(v => v.View).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询某日值日失败。");
            return Array.Empty<DutyView>();
        }
    }

    /// <summary>当前生效的值日表（供设置页与预览使用）。</summary>
    public DutyPlan? GetActivePlan(DateOnly date)
    {
        var plans = Settings.Plans.Where(p => p.Enabled).ToList();
        if (plans.Count == 0) return null;
        var index = RotationCalculator.GetPlanIndex(date, Settings.Rotation, plans.Count, Settings.ActivePlanIndex);
        return plans[Math.Clamp(index, 0, plans.Count - 1)];
    }

    private List<SlotView> BuildSlotViews(DateOnly date)
    {
        if (!Settings.Enabled) return new List<SlotView>();
        if (Settings.EnableHolidaySkip && IsHoliday(date)) return new List<SlotView>();

        var plan = GetActivePlan(date);
        if (plan == null) return new List<SlotView>();

        var weekday = (int)date.DayOfWeek;
        if (weekday >= 0 && weekday < plan.WeekDays.Length && !plan.WeekDays[weekday])
            return new List<SlotView>();

        var result = new List<SlotView>();
        foreach (var slot in plan.TimeSlots)
        {
            var items = plan.Items
                .Where(i => i.Weekday == -1 || i.Weekday == weekday)
                .Where(i => string.IsNullOrWhiteSpace(i.SlotLabel) || i.SlotLabel == slot.Label)
                .Select(ToItemView)
                .ToList();
            result.Add(new SlotView(
                new DutyView(slot.Label, date, items),
                slot.Start,
                slot.End));
        }
        return result;
    }

    private static DutyItemView ToItemView(DutyItem item)
    {
        var persons = DutyPersonParser.Parse(item.Persons)
            .Select(name => new DutyPerson(name, item.Color))
            .ToList();
        return new DutyItemView(item.Project, persons, item.IsSweep, item.Color);
    }

    private sealed record SlotView(DutyView View, TimeSpan Start, TimeSpan End);

    /// <summary>供提醒使用的纯文本，格式“项目：张三、李四 · 项目：王五”。</summary>
    public string GetDutyText(DateTime date, int maxItems = 6)
    {
        var view = CurrentAt(new DateTimeOffset(date));
        if (view == null || view.Items.Count == 0) return "";
        var parts = view.Items.Take(maxItems).Select(item =>
            $"{item.Project}：{(item.Persons.Count == 0 ? "—" : string.Join("、", item.Persons.Select(p => p.Name)))}");
        var text = string.Join(" · ", parts);
        if (view.Items.Count > maxItems) text += " …";
        return text;
    }

    public bool IsHoliday(DateOnly date)
    {
        foreach (var holiday in Settings.Holidays)
        {
            var start = DateOnly.FromDateTime(holiday.Start.Date);
            var end = DateOnly.FromDateTime(holiday.End.Date);
            if (date >= start && date <= end) return true;
        }
        return false;
    }

    public IReadOnlyList<RotationPreviewDay> GetRotationPreview(int days)
    {
        var names = Settings.Plans.Select(p => p.Name).ToList();
        return RotationCalculator.Preview(DateOnly.FromDateTime(DateTime.Today), Settings.Rotation, days, names);
    }

    public string DescribeRotation()
    {
        var plan = GetActivePlan(DateOnly.FromDateTime(DateTime.Today));
        return RotationCalculator.Describe(DateOnly.FromDateTime(DateTime.Today), Settings.Rotation, plan?.Name ?? "（无）");
    }

    // ---- 值日完成勾选 / 交接记录（纯本地） ----

    public bool IsChecked(DateOnly date, string project, string person)
    {
        var key = CheckKey(date, project, person);
        return Settings.Checks.Any(c => c == key);
    }

    public void ToggleCheck(DateOnly date, string project, string person)
    {
        var key = CheckKey(date, project, person);
        if (Settings.Checks.Contains(key)) Settings.Checks.Remove(key);
        else Settings.Checks.Add(key);
        Save();
    }

    private static string CheckKey(DateOnly date, string project, string person)
        => $"{date:yyyy-MM-dd}|{project}|{person}";

    /// <summary>导出值日历史 CSV（本地文件）。</summary>
    public string ExportHistoryCsv(string outputPath, int days = 30)
    {
        var sb = new StringBuilder();
        sb.AppendLine("日期,时段,项目,人员,大扫除,已完成");
        var start = DateOnly.FromDateTime(DateTime.Today.AddDays(-days + 1));
        for (var i = 0; i < days; i++)
        {
            var date = start.AddDays(i);
            foreach (var view in ForDate(date))
            {
                foreach (var item in view.Items)
                {
                    foreach (var person in item.Persons)
                    {
                        var name = person.Name.Replace("\"", "\"\"");
                        var project = item.Project.Replace("\"", "\"\"");
                        var done = IsChecked(date, item.Project, person.Name) ? "是" : "否";
                        sb.AppendLine($"\"{date:yyyy-MM-dd}\",\"{view.SlotLabel}\",\"{project}\",\"{name}\",{(item.IsSweep ? "是" : "否")},{done}");
                    }
                }
            }
        }
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        return outputPath;
    }

    /// <summary>LuckyPicker 联动：从本地名单文件或当天名单中随机抽一名值日生（不联网）。</summary>
    public string DrawLuckyPerson()
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(Settings.LuckyPickerNamesFile) && File.Exists(Settings.LuckyPickerNamesFile))
        {
            try
            {
                names.AddRange(DutyPersonParser.Parse(File.ReadAllText(Settings.LuckyPickerNamesFile)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取本地抽签名单失败。");
            }
        }
        if (names.Count == 0)
        {
            var date = DateOnly.FromDateTime(DateTime.Today);
            names.AddRange(ForDate(date)
                .SelectMany(v => v.Items)
                .SelectMany(i => i.Persons)
                .Select(p => p.Name)
                .Distinct());
        }
        return names.Count == 0 ? "（暂无可抽取的名单）" : names[Random.Shared.Next(names.Count)];
    }
}
