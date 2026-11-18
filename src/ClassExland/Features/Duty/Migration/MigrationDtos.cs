using System.Text.Json.Serialization;

namespace ClassExland.Features.Duty.Migration;

/// <summary>DutyList (dutylist.jimmyxiao/duty.json) 的只读 DTO。</summary>
public sealed class DutyListFileDto
{
    public List<DutyListGroupDto>? Groups { get; set; }
    public DateTime? RotationStartDate { get; set; }
    public int RotationPeriodDays { get; set; } = 7;
    public bool EnableReminder { get; set; }
    public bool EnableReminderSound { get; set; }
    public string? TtsVoice { get; set; }
    public string? StatusText { get; set; }
}

public sealed class DutyListGroupDto
{
    public string? Name { get; set; }
    public bool EnableReminder { get; set; }
    public List<int>? SkipDays { get; set; }
    public Dictionary<string, List<DutyListSlotDto>>? DayConfig { get; set; }
}

public sealed class DutyListSlotDto
{
    public string? Start { get; set; }
    public string? End { get; set; }
    public string? StartText { get; set; }
    public string? EndText { get; set; }
    public List<DutyListItemDto>? Items { get; set; }
}

public sealed class DutyListItemDto
{
    public string? Project { get; set; }
    public string? Person1 { get; set; }
    public string? Person2 { get; set; }
    public string? Person3 { get; set; }
    public string? Color { get; set; }
}

/// <summary>试写作业 (yibianhui.trialhomework/Settings.json) 的只读 DTO。</summary>
public sealed class TrialHomeworkFileDto
{
    public List<TrialHomeworkGroupDto>? Groups { get; set; }
    public int ActiveGroupIndex { get; set; }
    public bool AutoRotateByMonth { get; set; } = true;
    public bool EnableReminder { get; set; } = true;
    public string? ReminderTime { get; set; }
    public bool ReminderOnSchoolDaysOnly { get; set; } = true;
    public string? ReminderMessage { get; set; }
}

public sealed class TrialHomeworkGroupDto
{
    public string? Name { get; set; }
    public string? Monday { get; set; }
    public string? Tuesday { get; set; }
    public string? Wednesday { get; set; }
    public string? Thursday { get; set; }
    public string? Friday { get; set; }
    public string? Saturday { get; set; }
    public string? Sunday { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>lrs2187.duty/Config.json 的只读 DTO（只关心模板与 WorkerDictionary）。</summary>
public sealed class Lrs2187FileDto
{
    public LrsProfileDto? Profile { get; set; }
}

public sealed class LrsProfileDto
{
    public Dictionary<string, LrsDutyPlanDto>? DutyPlans { get; set; }
    public Dictionary<string, LrsTemplateDto>? DutyPlanTemplates { get; set; }
    public LrsRollingDto? Rolling { get; set; }
}

public sealed class LrsDutyPlanDto
{
    public string? TemplateGuid { get; set; }
    public string? Name { get; set; }
    public Dictionary<string, LrsWorkerGroupDto>? WorkerDictionary { get; set; }
}

public sealed class LrsWorkerGroupDto
{
    public List<LrsWorkerDto>? Workers { get; set; }
}

public sealed class LrsWorkerDto
{
    public string? Name { get; set; }
    public bool IsActive { get; set; }
}

public sealed class LrsTemplateDto
{
    public string? TemplateName { get; set; }
    public Dictionary<string, LrsWorkerTemplateDto>? WorkerTemplateDictionary { get; set; }
}

public sealed class LrsWorkerTemplateDto
{
    public string? Name { get; set; }
    public int WorkerCount { get; set; }
}

public sealed class LrsRollingDto
{
    public int RollIndex { get; set; }
    public int RollDays { get; set; }
    public bool SkipWeekend { get; set; }
    public DateTime? LastChangedDate { get; set; }
}

/// <summary>迁移来源枚举。</summary>
public enum MigrationSource
{
    Auto,
    DutyList,
    DutyRosterText,
    TrialHomework,
    Lrs2187
}
