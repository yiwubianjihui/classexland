using System.Text.Json;
using ClassExland.Contracts;
using ClassExland.Features.Duty.Models;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Duty.Migration;

/// <summary>
/// 旧插件配置迁移：只读旧文件、追加到我方配置、冲突时给出差异供用户选择。
/// 绝不修改旧插件文件。
/// </summary>
public sealed class MigrationService : IDutyMigrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly DutySettings _duty;
    private readonly ActivitySettings _activity;
    private readonly IPluginPaths _paths;
    private readonly ILogger<MigrationService> _logger;

    public MigrationService(DutySettings duty, ActivitySettings activity, IPluginPaths paths,
        ILogger<MigrationService> logger)
    {
        _duty = duty;
        _activity = activity;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>供 CLI 通过 IDutyMigrationService 反射调用（2 参数形式）。</summary>
    public Task<string> MigrateAsync(string from, string plan)
    {
        return MigrateAsync(from, plan, CancellationToken.None);
    }

    /// <summary>供显式传入取消令牌的调用方使用（3 参数重载）。</summary>
    public Task<string> MigrateAsync(string from, string plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = ResolveImportPath(from);
        var result = Apply(path, null);
        var summary = $"来源：{result.Source}；目标计划：{plan}；新增值日表 {result.AddedPlans} 张、值日项 {result.AddedItems} 条、活动 {result.AddedActivities} 个。";
        if (result.HasConflicts) summary += $" 有 {result.Conflicts.Count} 处同名冲突未自动处理（请到设置页逐条选择）。";
        summary += " " + string.Join(" ", result.Messages);
        return Task.FromResult(summary);
    }

    private static string ResolveImportPath(string from)
    {
        if (string.IsNullOrWhiteSpace(from)) return from;
        if (File.Exists(from)) return from;
        if (!Directory.Exists(from)) return from;
        foreach (var name in new[] { "duty.json", "值日表.txt", "Settings.json", "Config.json" })
        {
            var candidate = Path.Combine(from, name);
            if (File.Exists(candidate)) return candidate;
        }
        return from;
    }

    /// <summary>仅预览，不改动任何配置。</summary>
    public MigrationResult Preview(string path) => Build(path, apply: false, null);

    /// <summary>应用迁移；冲突按用户选择处理。</summary>
    public MigrationResult Apply(string path, IReadOnlyDictionary<string, MigrationConflictChoice>? choices)
        => Build(path, apply: true, choices);

    public static MigrationSource DetectSource(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.Contains("duty.json")) return MigrationSource.DutyList;
        if (name.EndsWith(".txt")) return MigrationSource.DutyRosterText;
        if (name == "settings.json") return MigrationSource.TrialHomework;
        if (name == "config.json") return MigrationSource.Lrs2187;

        var head = "";
        try
        {
            using var reader = new StreamReader(path);
            head = reader.ReadToEnd()[..Math.Min(512, (int)new FileInfo(path).Length)];
        }
        catch { /* 读取失败时在 Build 中统一报告 */ }

        if (head.Contains("DayConfig")) return MigrationSource.DutyList;
        if (head.Contains("AutoRotateByMonth")) return MigrationSource.TrialHomework;
        if (head.Contains("WorkerDictionary")) return MigrationSource.Lrs2187;
        if (head.Contains("值日表") || head.Contains("轮换起始")) return MigrationSource.DutyRosterText;
        return MigrationSource.Auto;
    }

    private MigrationResult Build(string path, bool apply, IReadOnlyDictionary<string, MigrationConflictChoice>? choices)
    {
        var result = new MigrationResult { SourceFile = path };
        try
        {
            if (!File.Exists(path))
            {
                result.Messages.Add($"文件不存在：{path}");
                return result;
            }

            result.Source = DetectSource(path);
            var text = File.ReadAllText(path);

            var plans = new List<DutyPlan>();
            var activities = new List<SpecialActivity>();
            RotationSettings? importedRotation = null;

            switch (result.Source)
            {
                case MigrationSource.DutyList:
                    ParseDutyList(text, plans, activities, ref importedRotation, result);
                    break;
                case MigrationSource.DutyRosterText:
                    ParseDutyRoster(text, plans, ref importedRotation, result);
                    break;
                case MigrationSource.TrialHomework:
                    ParseTrialHomework(text, activities, result);
                    break;
                case MigrationSource.Lrs2187:
                    ParseLrs2187(text, activities, result);
                    break;
                default:
                    result.Messages.Add("无法识别配置文件格式，请确认来源（DutyList / DutyRoster / 试写作业 / lrs2187）。");
                    return result;
            }

            if (apply)
            {
                MergeRotation(importedRotation);
                MergePlans(plans, choices, result);
                MergeActivities(activities, choices, result);
                SaveAll();
                result.Messages.Add("迁移完成，已追加到 ClassExland 配置；旧插件文件未作任何修改。");
            }
            else
            {
                result.Messages.Add("这只是预览，未修改任何配置。确认后请点击“应用导入”。");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "迁移配置时出现异常：{Path}", path);
            result.Messages.Add($"迁移失败：{ex.Message}");
        }
        return result;
    }

    private static void ParseDutyList(string text, List<DutyPlan> plans, List<SpecialActivity> activities,
        ref RotationSettings? importedRotation, MigrationResult result)
    {
        var dto = JsonSerializer.Deserialize<DutyListFileDto>(text, JsonOptions);
        if (dto?.Groups == null) { result.Messages.Add("duty.json 中没有 Groups。"); return; }

        importedRotation = new RotationSettings
        {
            StartDate = dto.RotationStartDate ?? DateTime.Today,
            PeriodDays = dto.RotationPeriodDays > 0 ? dto.RotationPeriodDays : 7,
            Mode = RotationMode.Day
        };

        foreach (var group in dto.Groups)
        {
            var plan = new DutyPlan
            {
                Name = string.IsNullOrWhiteSpace(group.Name) ? "导入的值日表" : group.Name!,
                Scope = DutyScope.Custom,
                Enabled = true,
                Color = "#00BFFF"
            };

            // SkipDays 同时作为轮换跳过日（语义对齐 DutyList）。
            if (group.SkipDays != null)
            {
                foreach (var d in group.SkipDays.Distinct())
                {
                    if (d is >= 0 and <= 6 && !importedRotation.SkipWeekdays.Contains(d))
                        importedRotation.SkipWeekdays.Add(d);
                    if (d is >= 0 and <= 6) plan.WeekDays[d] = false;
                }
            }

            if (group.DayConfig != null)
            {
                foreach (var (key, slots) in group.DayConfig)
                {
                    if (!int.TryParse(key, out var weekday) || weekday is < 0 or > 6) continue;
                    plan.WeekDays[weekday] = true;
                    foreach (var slot in slots ?? new List<DutyListSlotDto>())
                    {
                        var start = ParseTime(slot.Start) ?? ParseTime(slot.StartText) ?? TimeSpan.Zero;
                        var end = ParseTime(slot.End) ?? ParseTime(slot.EndText) ?? start.Add(TimeSpan.FromHours(1));
                        var label = string.IsNullOrWhiteSpace(slot.StartText) ? start.ToString(@"hh\:mm") : slot.StartText!;
                        if (plan.TimeSlots.All(s => s.Label != label))
                            plan.TimeSlots.Add(new TimeSlot { Label = label, Start = start, End = end });

                        foreach (var item in slot.Items ?? new List<DutyListItemDto>())
                        {
                            var persons = string.Join("、",
                                new[] { item.Person1, item.Person2, item.Person3 }
                                .Where(p => !string.IsNullOrWhiteSpace(p))!
                                .Select(p => p!.Trim()));
                            plan.Items.Add(new DutyItem
                            {
                                Project = item.Project ?? "项目",
                                Persons = persons,
                                Color = item.Color,
                                IsSweep = (item.Project ?? "").Contains("扫"),
                                Weekday = weekday,
                                SlotLabel = label
                            });
                            result.AddedItems++;
                        }
                    }
                }
            }
            // 旧插件把开始时间当分组名（07:00），搬过来后统一换成上午/下午/傍晚。
            DutySlotLabels.Normalize(plan);
            plans.Add(plan);
            result.AddedPlans++;
        }
        result.Messages.Add($"DutyList：识别到 {plans.Count} 个批次、{result.AddedItems} 条值日项。");
    }

    private static void ParseDutyRoster(string text, List<DutyPlan> plans,
        ref RotationSettings? importedRotation, MigrationResult result)
    {
        var parsed = DutyRosterTextParser.Parse(text);
        importedRotation = parsed.Rotation;
        plans.AddRange(parsed.Plans);
        result.AddedPlans += parsed.Plans.Count;
        result.AddedItems += parsed.Plans.Sum(p => p.Items.Count);
        result.Messages.AddRange(parsed.Warnings);
        result.Messages.Add($"DutyRoster：识别到 {parsed.Plans.Count} 批、{result.AddedItems} 条值日项。");
    }

    private static void ParseTrialHomework(string text, List<SpecialActivity> activities, MigrationResult result)
    {
        var dto = JsonSerializer.Deserialize<TrialHomeworkFileDto>(text, JsonOptions);
        if (dto?.Groups == null) { result.Messages.Add("Settings.json 中没有 Groups。"); return; }

        var activity = new SpecialActivity
        {
            Name = "试写作业（导入）",
            RotationMode = dto.AutoRotateByMonth ? RotationMode.Month : RotationMode.Manual,
            ActiveGroupIndex = Math.Max(0, dto.ActiveGroupIndex),
            MessageTemplate = string.IsNullOrWhiteSpace(dto.ReminderMessage)
                ? "试写作业：{names}"
                : dto.ReminderMessage!,
            SchoolDaysOnly = dto.ReminderOnSchoolDaysOnly,
            IncludeDutyNames = true
        };
        if (TimeSpan.TryParse((dto.ReminderTime ?? "").Replace('：', ':'), out var t))
            activity.ReminderTimes.Add(t);

        foreach (var g in dto.Groups)
        {
            activity.Groups.Add(new ActivityGroup
            {
                Name = g.Name ?? "组",
                Monday = g.Monday ?? "", Tuesday = g.Tuesday ?? "", Wednesday = g.Wednesday ?? "",
                Thursday = g.Thursday ?? "", Friday = g.Friday ?? "", Saturday = g.Saturday ?? "",
                Sunday = g.Sunday ?? ""
            });
            result.AddedActivityGroups++;
        }

        activities.Add(activity);
        result.AddedActivities++;
        result.Messages.Add($"试写作业：识别到 {activity.Groups.Count} 个名单组。");
    }

    private static void ParseLrs2187(string text, List<SpecialActivity> activities, MigrationResult result)
    {
        var dto = JsonSerializer.Deserialize<Lrs2187FileDto>(text, JsonOptions);
        var profile = dto?.Profile;
        if (profile?.DutyPlans == null) { result.Messages.Add("Config.json 中没有 Profile.DutyPlans。"); return; }

        foreach (var (planId, plan) in profile.DutyPlans)
        {
            var templateName = plan.TemplateGuid != null &&
                               profile.DutyPlanTemplates != null &&
                               profile.DutyPlanTemplates.TryGetValue(plan.TemplateGuid, out var tpl)
                ? tpl.TemplateName ?? "模板"
                : "值日";
            var activity = new SpecialActivity
            {
                Name = $"{plan.Name ?? "值日表"}（{templateName}）",
                RotationMode = profile.Rolling?.SkipWeekend == true ? RotationMode.Week : RotationMode.Day,
                CustomStartDate = profile.Rolling?.LastChangedDate ?? DateTime.Today,
                MessageTemplate = "{names}"
            };

            if (plan.WorkerDictionary != null)
            {
                foreach (var (_, group) in plan.WorkerDictionary)
                {
                    var names = string.Join("、", (group.Workers ?? new List<LrsWorkerDto>())
                        .Where(w => !string.IsNullOrWhiteSpace(w.Name))
                        .Select(w => w.Name!));
                    // lrs2187 没有按星期拆分，放到周一至周五。
                    activity.Groups.Add(new ActivityGroup
                    {
                        Name = templateName,
                        Monday = names, Tuesday = names, Wednesday = names, Thursday = names, Friday = names
                    });
                    result.AddedActivityGroups++;
                }
            }

            if (activity.Groups.Count == 0)
            {
                activity.Groups.Add(new ActivityGroup { Name = templateName });
            }
            activities.Add(activity);
            result.AddedActivities++;
        }
        result.Messages.Add($"lrs2187.duty：识别到 {activities.Count} 个值日计划。");
    }

    private void MergeRotation(RotationSettings? imported)
    {
        if (imported == null) return;
        if (_duty.Plans.Count == 0 && _duty.Rotation.StartDate.Date == DateTime.Today)
        {
            _duty.Rotation = imported;
            return;
        }
        foreach (var d in imported.SkipWeekdays)
        {
            if (!_duty.Rotation.SkipWeekdays.Contains(d)) _duty.Rotation.SkipWeekdays.Add(d);
        }
        if (_duty.Rotation.PeriodDays <= 0) _duty.Rotation.PeriodDays = imported.PeriodDays;
    }

    private void MergePlans(List<DutyPlan> imported, IReadOnlyDictionary<string, MigrationConflictChoice>? choices,
        MigrationResult result)
    {
        foreach (var plan in imported)
        {
            var existing = _duty.Plans.FirstOrDefault(p => p.Name == plan.Name);
            if (existing == null)
            {
                _duty.Plans.Add(plan);
                continue;
            }

            var key = $"plan:{plan.Name}";
            var choice = choices != null && choices.TryGetValue(key, out var c)
                ? c
                : MigrationConflictChoice.Ask;
            if (choice == MigrationConflictChoice.Ask)
            {
                result.Conflicts.Add(new MigrationConflict
                {
                    Key = key,
                    Title = $"值日表「{plan.Name}」已存在",
                    ExistingSummary = Summarize(existing.Items),
                    ImportedSummary = Summarize(plan.Items),
                    Choice = MigrationConflictChoice.Ask
                });
                continue;
            }
            if (choice == MigrationConflictChoice.Replace)
            {
                _duty.Plans.Remove(existing);
                _duty.Plans.Add(plan);
                result.Messages.Add($"已用导入内容替代值日表「{plan.Name}」。");
            }
        }
    }

    private void MergeActivities(List<SpecialActivity> imported, IReadOnlyDictionary<string, MigrationConflictChoice>? choices,
        MigrationResult result)
    {
        foreach (var activity in imported)
        {
            var existing = _activity.Activities.FirstOrDefault(a => a.Name == activity.Name);
            if (existing == null)
            {
                _activity.Activities.Add(activity);
                continue;
            }

            var key = $"activity:{activity.Name}";
            var choice = choices != null && choices.TryGetValue(key, out var c)
                ? c
                : MigrationConflictChoice.Ask;
            if (choice == MigrationConflictChoice.Ask)
            {
                result.Conflicts.Add(new MigrationConflict
                {
                    Key = key,
                    Title = $"特殊活动「{activity.Name}」已存在",
                    ExistingSummary = $"{existing.Groups.Count} 个名单组",
                    ImportedSummary = $"{activity.Groups.Count} 个名单组",
                    Choice = MigrationConflictChoice.Ask
                });
                continue;
            }
            if (choice == MigrationConflictChoice.Replace)
            {
                _activity.Activities.Remove(existing);
                _activity.Activities.Add(activity);
                result.Messages.Add($"已用导入内容替代活动「{activity.Name}」。");
            }
        }
    }

    private static string Summarize(IEnumerable<DutyItem> items)
    {
        var list = items.Take(3).Select(i => $"{i.Project}（{i.Persons}）");
        var suffix = items.Count() > 3 ? "…" : "";
        return string.Join("；", list) + suffix;
    }

    private static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Trim().Replace('：', ':');
        return TimeSpan.TryParse(normalized, out var t) ? t : null;
    }

    private void SaveAll()
    {
        try
        {
            ConfigureFileHelper.SaveConfig(_paths.Get(ConfigFiles.Duty), _duty);
            _activity.ConfigPath = _paths.Get(ConfigFiles.Activity);
            _activity.OptionsPath = Path.Combine(_paths.ConfigDirectory, "activity-options.json");
            _activity.SaveToFile();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存迁移后的配置失败。");
        }
    }
}

/// <summary>内置预设，供设置页一键添加。</summary>
public static class ActivityPresets
{
    public static SpecialActivity CreateTrialHomeworkPreset() => new()
    {
        Name = "试写作业",
        RotationMode = RotationMode.Month,
        MessageTemplate = "试写作业：{names}",
        SchoolDaysOnly = true,
        IncludeDutyNames = true,
        ReminderTimes = new System.Collections.ObjectModel.ObservableCollection<TimeSpan> { new(16, 20, 0) },
        Groups = new System.Collections.ObjectModel.ObservableCollection<ActivityGroup>
        {
            new() { Name = "第 1 组" },
            new() { Name = "第 2 组" },
            new() { Name = "第 3 组" }
        }
    };
}
