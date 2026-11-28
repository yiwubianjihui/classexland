using ClassExland.Contracts;
using ClassExland.Features.Bell;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// 2026-11-28：无预备铃选项 + 按课间时长的动态提前量。
/// 规则：预备铃在课间结束前提醒；10 分钟课间提前 2 分钟、15 分钟提前 3 分钟；无课间不触发。
/// 无预备铃的节次也必须生成"正式上课"事件（界面照常切上课态）。
/// </summary>
public class BellPrepModeTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 30, 0, 0, 0, TimeSpan.FromHours(8));

    private static BellSlot Class(string id, string start, string end, BellPrepMode mode, int prep = 120)
        => new(id, TimeSpan.Parse(start), TimeSpan.Parse(end), BellSlotKind.Class, prep, null, mode);

    private static BellSlot Break(string id, string start, string end)
        => new(id, TimeSpan.Parse(start), TimeSpan.Parse(end), BellSlotKind.Break, 0, null);

    private static BellTimelineOptions Options(BellPrepMode mode) => new()
    {
        PrepMode = mode,
        BreakPrepRules = BellBreakPrepRule.Defaults
    };

    // ---------- 纯规则解析 ----------

    [Theory]
    [InlineData(15, 180)]
    [InlineData(10, 120)]
    public void 规则_课间分钟数匹配提前量(int minutes, int expected)
    {
        Assert.Equal(expected, BellBreakPrepRules.Resolve(TimeSpan.FromMinutes(minutes), BellBreakPrepRule.Defaults));
    }

    [Fact]
    public void 规则_课间不足10分钟或无课间返回NoPrep()
    {
        Assert.Equal(BellSettings.NoPrep, BellBreakPrepRules.Resolve(TimeSpan.FromMinutes(9), BellBreakPrepRule.Defaults));
        Assert.Equal(BellSettings.NoPrep, BellBreakPrepRules.Resolve(null, BellBreakPrepRule.Defaults));
        Assert.Equal(BellSettings.NoPrep, BellBreakPrepRules.Resolve(TimeSpan.FromMinutes(12), null));
    }

    [Fact]
    public void 规则_取满足阈值中最大的一条()
    {
        Assert.Equal(180, BellBreakPrepRules.Resolve(TimeSpan.FromMinutes(20), BellBreakPrepRule.Defaults));
    }

    // ---------- ByBreak 时间轴 ----------

    [Fact]
    public void ByBreak_15分钟课间提前3分钟响铃()
    {
        var moments = BellTimelineCalculator.Build(
            new[] { Break("B", "08:35", "08:50"), Class("A", "08:50", "09:25", BellPrepMode.ByBreak) },
            Options(BellPrepMode.ByBreak), Day);

        var bell = Assert.Single(moments.Where(m => m.Kind == BellMomentKind.PrepareBell));
        Assert.Equal(TimeSpan.Parse("08:47"), bell.At.TimeOfDay);
        Assert.Contains(moments, m => m.Kind == BellMomentKind.ClassOnNotice);
    }

    [Fact]
    public void ByBreak_10分钟课间提前2分钟响铃()
    {
        var moments = BellTimelineCalculator.Build(
            new[] { Break("B", "09:25", "09:35"), Class("A", "09:35", "10:10", BellPrepMode.ByBreak) },
            Options(BellPrepMode.ByBreak), Day);

        var bell = Assert.Single(moments.Where(m => m.Kind == BellMomentKind.PrepareBell));
        Assert.Equal(TimeSpan.Parse("09:33"), bell.At.TimeOfDay);
    }

    [Fact]
    public void ByBreak_无课间不响预备铃但仍生成正式上课事件()
    {
        // 单节课前面没有课间 → 不响预备铃；但 ClassOnNotice 必须在，界面才能切上课态。
        var moments = BellTimelineCalculator.Build(
            new[] { Class("A", "08:00", "08:40", BellPrepMode.ByBreak) },
            Options(BellPrepMode.ByBreak), Day);

        Assert.DoesNotContain(moments, m => m.Kind == BellMomentKind.PrepareBell);
        Assert.DoesNotContain(moments, m => m.Kind == BellMomentKind.PreClassNotice);
        Assert.Contains(moments, m => m.Kind == BellMomentKind.ClassOnNotice);
        Assert.Contains(moments, m => m.Kind == BellMomentKind.ClassEnd);
    }

    // ---------- Off 无预备铃 ----------

    [Fact]
    public void Off_即使前面有课间也不响预备铃_但切上课态()
    {
        var moments = BellTimelineCalculator.Build(
            new[] { Break("B", "08:35", "08:50"), Class("A", "08:50", "09:25", BellPrepMode.Off) },
            Options(BellPrepMode.Off), Day);

        Assert.DoesNotContain(moments, m => m.Kind == BellMomentKind.PrepareBell);
        Assert.DoesNotContain(moments, m => m.Kind == BellMomentKind.PreClassNotice);
        var on = Assert.Single(moments.Where(m => m.Kind == BellMomentKind.ClassOnNotice));
        Assert.Equal(TimeSpan.Parse("08:50"), on.At.TimeOfDay);
    }

    [Fact]
    public void 单节Off覆盖全局ByBreak()
    {
        // 全局 ByBreak，但这一节显式 Off → 不响。
        var moments = BellTimelineCalculator.Build(
            new[] { Break("B", "08:35", "08:50"), Class("A", "08:50", "09:25", BellPrepMode.Off) },
            Options(BellPrepMode.ByBreak), Day);
        Assert.DoesNotContain(moments, m => m.Kind == BellMomentKind.PrepareBell);
    }

    [Fact]
    public void 单节ByBreak覆盖全局Fixed()
    {
        // 全局 Fixed(120)，这一节 ByBreak + 15 分钟课间 → 提前 180 秒。
        var moments = BellTimelineCalculator.Build(
            new[] { Break("B", "08:35", "08:50"), Class("A", "08:50", "09:25", BellPrepMode.ByBreak) },
            Options(BellPrepMode.Fixed), Day);
        var bell = Assert.Single(moments.Where(m => m.Kind == BellMomentKind.PrepareBell));
        Assert.Equal(TimeSpan.Parse("08:47"), bell.At.TimeOfDay);
    }
}
