using ClassExland.Features.Duty.Models;

namespace ClassExland.Features.Duty.Migration;

/// <summary>迁移结果：追加的内容数量与需要用户决策的冲突。</summary>
public sealed class MigrationResult
{
    public List<string> Messages { get; } = new();
    public List<MigrationConflict> Conflicts { get; } = new();
    public int AddedPlans { get; set; }
    public int AddedItems { get; set; }
    public int AddedActivities { get; set; }
    public int AddedActivityGroups { get; set; }
    public MigrationSource Source { get; set; }
    public string SourceFile { get; set; } = "";

    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>
/// 同名冲突的差异描述。用户可选择 Keep（保留现有）/ Replace（用导入内容替代）/ Skip（跳过该条）。
/// </summary>
public sealed class MigrationConflict
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string ExistingSummary { get; set; } = "";
    public string ImportedSummary { get; set; } = "";
    public MigrationConflictChoice Choice { get; set; } = MigrationConflictChoice.Ask;
}

public enum MigrationConflictChoice
{
    Ask,
    Keep,
    Replace,
    Skip
}
