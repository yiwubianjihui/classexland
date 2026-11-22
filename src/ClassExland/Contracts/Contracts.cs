using System.Text.Json.Serialization;

namespace ClassExland.Contracts;

/// <summary>
/// ClassExland 自己的课堂状态。Harmony 覆盖开启时，宿主状态与本状态保持一致。
/// </summary>
public enum VirtualTimeState
{
    None,
    PrepareOnClass,
    OnClass,
    Breaking,
    AfterSchool
}

public enum BellMomentKind
{
    /// <summary>原上课前提醒，已平移到预备铃前 60 秒。</summary>
    PreClassNotice,
    /// <summary>预备铃，进入上课状态。</summary>
    PrepareBell,
    /// <summary>正式上课，仅作提醒。</summary>
    ClassOnNotice,
    /// <summary>下课，延迟触发值日展示。</summary>
    ClassEnd,
    /// <summary>特殊活动开始前 10 分钟的提前提示。</summary>
    ActivityAdvanceNotice
}

public sealed record BellMoment(
    DateTimeOffset At,
    BellMomentKind Kind,
    string TimeLayoutItemId,
    string? Subject,
    int PrepSeconds);

/// <summary>
/// 唯一时间真相源。其他模块只订阅事件，不自己起定时器。
/// </summary>
public interface IBellTimeline
{
    VirtualTimeState VirtualState { get; }
    BellMoment? Current { get; }
    DateTimeOffset? NextBellAt { get; }
    event EventHandler<BellMoment>? MomentReached;
    event EventHandler<VirtualTimeState>? VirtualStateChanged;
}

public sealed record DutyPerson(string Name, string? Color = null);

public sealed record DutyItemView(
    string Project,
    IReadOnlyList<DutyPerson> Persons,
    bool IsSweep = false,
    string? Color = null);

public sealed record DutyView(
    string SlotLabel,
    DateOnly Date,
    IReadOnlyList<DutyItemView> Items);

/// <summary>
/// 值日查询的只读契约，供提醒模块、组件、CLI 共用。
/// </summary>
public interface IDutyQuery
{
    DutyView? CurrentAt(DateTimeOffset at);
    IReadOnlyList<DutyView> ForDate(DateOnly date);
}

public sealed record CommandRequest(
    string Command,
    IReadOnlyDictionary<string, string?> Args,
    string Source,
    bool Apply,
    bool DryRun);

public sealed record CommandResult(
    bool Ok,
    string Message,
    object? Data = null);

/// <summary>
/// CLI 与远程指令队列共用的命令执行入口。
/// </summary>
public interface ICommandDispatcher
{
    Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct = default);
}

public interface IPluginPaths
{
    string ConfigDirectory { get; }
    string BackupDirectory { get; }
    string AuditLogPath { get; }
    string CacheDirectory { get; }
    string Get(string relativeFileName);
}

public static class ConfigFiles
{
    public const string Bell = "bell.json";
    public const string Duty = "duty.json";
    public const string Activity = "activities.json";
    public const string Layout = "layout.json";
    public const string Remote = "remote.json";
    public const string Bridge = "bridge.json";
    public const string Lucky = "lucky.json";
    public const string CoreSnapshot = "classisland-snapshot.json";
}
