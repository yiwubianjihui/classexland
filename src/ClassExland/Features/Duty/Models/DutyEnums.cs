namespace ClassExland.Features.Duty.Models;

/// <summary>
/// 值日表的覆盖时段。由预设自动展开为具体的 <see cref="TimeSlot"/>。
/// </summary>
public enum DutyScope
{
    FullDay,
    Morning,
    Afternoon,
    Custom
}

/// <summary>
/// 轮换方式。Day/Week/Month 均使用 <see cref="RotationSettings.SkipWeekdays"/> 过滤跳过日，
/// 语义与 DutyList 的 RotationStartDate / RotationPeriodDays / SkipDays 对齐：跳过日不消耗“第 N 天”计数。
/// </summary>
public enum RotationMode
{
    Day,
    Week,
    Month,
    Manual
}
