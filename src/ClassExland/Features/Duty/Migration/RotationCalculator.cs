using ClassExland.Features.Duty.Models;

namespace ClassExland.Features.Duty.Migration;

/// <summary>
/// 轮换计算纯逻辑，供单测使用。
/// 语义对齐 DutyList 的 RotationStartDate / RotationPeriodDays / SkipDays：
/// 从起始日期到目标日期，只累计“非跳过日”；跳过日既不产生名单，也不消耗“第 N 天”计数。
/// </summary>
public static class RotationCalculator
{
    private static int NormalizePeriod(int periodDays) => Math.Max(1, periodDays);

    /// <summary>
    /// 统计 [start, end] 区间内非跳过日的天数（含两端）。
    /// </summary>
    public static int CountActiveDays(DateOnly start, DateOnly end, IReadOnlyCollection<int> skipWeekdays)
    {
        if (end < start) return 0;
        var count = 0;
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            if (!skipWeekdays.Contains((int)day.DayOfWeek)) count++;
        }
        return count;
    }

    /// <summary>
    /// 目标日期在轮换周期中的“第 N 天”（1 基）。
    /// 返回 null 表示：日期早于起始日，或当天是跳过日。
    /// </summary>
    public static int? GetCycleDay(DateOnly date, RotationSettings rotation)
    {
        var start = DateOnly.FromDateTime(rotation.StartDate.Date);
        if (date < start) return null;

        var skip = rotation.SkipWeekdays ?? new List<int>();
        if (skip.Contains((int)date.DayOfWeek)) return null;

        var mode = rotation.Mode;
        if (mode == RotationMode.Month)
        {
            var months = (date.Year - start.Year) * 12 + (date.Month - start.Month);
            return months < 0 ? null : months % NormalizePeriod(rotation.PeriodDays) + 1;
        }

        if (mode == RotationMode.Week)
        {
            // 周轮换按自然周计数；跳过日不影响“第 N 周”。
            var startWeek = start.AddDays(-(int)start.DayOfWeek);
            var weeks = Math.Max(0, (date.ToDateTime(TimeOnly.MinValue) - startWeek.ToDateTime(TimeOnly.MinValue)).Days / 7);
            return weeks % NormalizePeriod(rotation.PeriodDays) + 1;
        }

        var activeDays = CountActiveDays(start, date, skip);
        return activeDays <= 0 ? null : (activeDays - 1) % NormalizePeriod(rotation.PeriodDays) + 1;
    }

    /// <summary>
    /// 取给定日期应选择的值日表下标（0 基）。
    /// 跳过日沿用最近一个有效日的批次，避免“今天没有值日表”。
    /// </summary>
    public static int GetPlanIndex(DateOnly date, RotationSettings rotation, int planCount, int activePlanIndex = 0)
    {
        if (planCount <= 0) return 0;
        if (rotation.Mode == RotationMode.Manual) return Math.Clamp(activePlanIndex, 0, planCount - 1);

        var cycleDay = GetCycleDay(date, rotation);
        if (!cycleDay.HasValue)
        {
            var previous = date.AddDays(-1);
            var guard = 0;
            while (previous >= DateOnly.FromDateTime(rotation.StartDate.Date) && guard++ < 400)
            {
                if (!rotation.SkipWeekdays.Contains((int)previous.DayOfWeek))
                {
                    cycleDay = GetCycleDay(previous, rotation);
                    break;
                }
                previous = previous.AddDays(-1);
            }
        }

        var day = cycleDay ?? 1;
        return (day - 1) % planCount;
    }

    /// <summary>
    /// 生成未来若干天的轮换预览（日历视图用）。
    /// </summary>
    public static IReadOnlyList<RotationPreviewDay> Preview(DateOnly start, RotationSettings rotation, int days, IReadOnlyList<string> planNames)
    {
        var result = new List<RotationPreviewDay>();
        if (days <= 0) return result;
        for (var i = 0; i < days; i++)
        {
            var date = start.AddDays(i);
            var cycle = GetCycleDay(date, rotation);
            var index = GetPlanIndex(date, rotation, planNames.Count);
            result.Add(new RotationPreviewDay(
                date,
                cycle,
                rotation.SkipWeekdays.Contains((int)date.DayOfWeek),
                planNames.Count == 0 ? "（无值日表）" : planNames[Math.Clamp(index, 0, planNames.Count - 1)]));
        }
        return result;
    }

    /// <summary>状态栏描述文本，例如“当前：第1批 第4天（跳过1种日期）”。</summary>
    public static string Describe(DateOnly date, RotationSettings rotation, string planName)
    {
        if (rotation.Mode == RotationMode.Manual) return $"当前：{planName}（手动选择）";
        var cycle = GetCycleDay(date, rotation);
        if (!cycle.HasValue) return $"{date:yyyy-MM-dd} 为跳过日，沿用最近批次：{planName}";
        var skipCount = rotation.SkipWeekdays?.Count ?? 0;
        var suffix = skipCount > 0 ? $"（跳过 {skipCount} 种日期）" : "";
        return $"当前：{planName} 第{cycle}天{suffix}";
    }
}

/// <summary>轮换预览中的一天。</summary>
public sealed record RotationPreviewDay(DateOnly Date, int? CycleDay, bool IsSkipped, string PlanName);
