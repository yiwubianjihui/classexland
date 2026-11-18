using ClassExland.Features.Duty.Migration;
using ClassExland.Features.Duty.Models;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// 轮换计算的纯逻辑测试。
/// 语义基准：跳过日既不产生名单，也不消耗「第 N 天」计数（对齐 DutyList）。
/// </summary>
public class RotationCalculatorTests
{
    // 2026-03-02 是周一，作为基准起始日。
    private static DateOnly Mon => new(2026, 3, 2);

    private static RotationSettings Settings(
        RotationMode mode = RotationMode.Day,
        int periodDays = 5,
        params int[] skipWeekdays) => new()
        {
            StartDate = Mon.ToDateTime(TimeOnly.MinValue),
            PeriodDays = periodDays,
            Mode = mode,
            SkipWeekdays = skipWeekdays.ToList()
        };

    [Fact]
    public void CountActiveDays_含两端且跳过周末()
    {
        var count = RotationCalculator.CountActiveDays(Mon, new DateOnly(2026, 3, 6), new[] { 0, 6 });
        Assert.Equal(5, count); // 周一至周五
    }

    [Fact]
    public void CountActiveDays_结束早于开始返回零()
    {
        var count = RotationCalculator.CountActiveDays(new DateOnly(2026, 3, 6), Mon, Array.Empty<int>());
        Assert.Equal(0, count);
    }

    [Fact]
    public void GetCycleDay_起始日当天为第一天()
    {
        Assert.Equal(1, RotationCalculator.GetCycleDay(Mon, Settings()));
    }

    [Fact]
    public void GetCycleDay_早于起始日返回null()
    {
        Assert.Null(RotationCalculator.GetCycleDay(new DateOnly(2026, 3, 1), Settings()));
    }

    [Fact]
    public void GetCycleDay_跳过日返回null()
    {
        Assert.Null(RotationCalculator.GetCycleDay(new DateOnly(2026, 3, 7), Settings(skipWeekdays: new[] { 0, 6 })));
    }

    [Fact]
    public void GetCycleDay_五个有效日后回到第一天()
    {
        var s = Settings(periodDays: 5, skipWeekdays: new[] { 0, 6 });
        Assert.Equal(5, RotationCalculator.GetCycleDay(new DateOnly(2026, 3, 6), s)); // 周五 = 第 5 天
        Assert.Equal(1, RotationCalculator.GetCycleDay(new DateOnly(2026, 3, 9), s)); // 下周一 = 第 1 天
    }

    [Fact]
    public void GetCycleDay_周期为零时退化为恒为第一天()
    {
        var s = Settings(periodDays: 0);
        Assert.Equal(1, RotationCalculator.GetCycleDay(new DateOnly(2026, 3, 9), s));
    }

    [Fact]
    public void GetCycleDay_月轮换按自然月推进()
    {
        var s = Settings(mode: RotationMode.Month, periodDays: 3);
        Assert.Equal(2, RotationCalculator.GetCycleDay(new DateOnly(2026, 4, 15), s)); // 相差 1 个月
        Assert.Equal(3, RotationCalculator.GetCycleDay(new DateOnly(2026, 5, 15), s)); // 相差 2 个月
        Assert.Equal(1, RotationCalculator.GetCycleDay(new DateOnly(2026, 6, 15), s)); // 相差 3 个月 → 回绕
    }

    [Fact]
    public void GetCycleDay_周轮换按自然周推进且跳过日不影响周计数()
    {
        var s = Settings(mode: RotationMode.Week, periodDays: 7, skipWeekdays: new[] { 0, 6 });
        Assert.Equal(2, RotationCalculator.GetCycleDay(new DateOnly(2026, 3, 9), s)); // 下一周
        Assert.Equal(3, RotationCalculator.GetCycleDay(new DateOnly(2026, 3, 16), s));
    }

    [Fact]
    public void GetPlanIndex_手动模式取当前批次()
    {
        var s = Settings(mode: RotationMode.Manual);
        Assert.Equal(1, RotationCalculator.GetPlanIndex(Mon, s, 3, activePlanIndex: 1));
        Assert.Equal(2, RotationCalculator.GetPlanIndex(Mon, s, 3, activePlanIndex: 99)); // 越界收敛到末位
    }

    [Fact]
    public void GetPlanIndex_无值日表返回零()
    {
        Assert.Equal(0, RotationCalculator.GetPlanIndex(Mon, Settings(), planCount: 0));
    }

    [Fact]
    public void GetPlanIndex_跳过日沿用最近一个有效日的批次()
    {
        var s = Settings(periodDays: 5, skipWeekdays: new[] { 0, 6 });
        // 周六应沿用周五（第 5 天）的批次
        Assert.Equal(0, RotationCalculator.GetPlanIndex(new DateOnly(2026, 3, 6), s, planCount: 2));
        Assert.Equal(0, RotationCalculator.GetPlanIndex(new DateOnly(2026, 3, 7), s, planCount: 2));
    }

    [Fact]
    public void Preview_按天生成且带跳过标记()
    {
        var s = Settings(periodDays: 5, skipWeekdays: new[] { 0, 6 });
        var days = RotationCalculator.Preview(new DateOnly(2026, 3, 6), s, 3, new[] { "甲组", "乙组" });

        Assert.Equal(3, days.Count);
        Assert.False(days[0].IsSkipped);
        Assert.True(days[1].IsSkipped);       // 周六
        Assert.Equal("甲组", days[1].PlanName); // 按跳过日规则沿用周五批次
    }

    [Fact]
    public void Preview_无值日表时给出占位文本()
    {
        var days = RotationCalculator.Preview(Mon, Settings(), 1, Array.Empty<string>());
        Assert.Equal("（无值日表）", days[0].PlanName);
    }

    [Fact]
    public void Describe_手动模式与跳过日文案区分()
    {
        var manual = RotationCalculator.Describe(Mon, Settings(mode: RotationMode.Manual), "甲组");
        Assert.Contains("手动选择", manual);

        var skipped = RotationCalculator.Describe(new DateOnly(2026, 3, 7),
            Settings(skipWeekdays: new[] { 0, 6 }), "甲组");
        Assert.Contains("跳过日", skipped);
    }
}
