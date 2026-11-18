using ClassExland.Contracts;
using ClassExland.Features.Bell;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// 预备铃时间轴的纯逻辑测试。
/// 规则基准：预备铃前 60 秒预告 → 预备铃进入上课 → 正式铃仅提醒 → 下课后延迟 60 秒才下课。
/// </summary>
public class BellTimelineCalculatorTests
{
    private static readonly DateTimeOffset Day = new(2026, 3, 2, 0, 0, 0, TimeSpan.FromHours(8));

    private static BellSlot Class(string id, string start, string end, int prep = 120, string? subject = null)
        => new(id, TimeSpan.Parse(start), TimeSpan.Parse(end), BellSlotKind.Class, prep, subject);

    private static BellSlot Break(string id, string start, string end)
        => new(id, TimeSpan.Parse(start), TimeSpan.Parse(end), BellSlotKind.Break, 0, null);

    private static BellTimelineOptions Default => new();

    private static BellMoment Single(IReadOnlyList<BellMoment> moments, BellMomentKind kind)
        => Assert.Single(moments.Where(m => m.Kind == kind));

    private static TimeSpan TimeOf(DateTimeOffset at) => at.TimeOfDay;

    [Fact]
    public void Build_空课表返回空()
    {
        Assert.Empty(BellTimelineCalculator.Build(Array.Empty<BellSlot>(), Default, Day));
    }

    [Fact]
    public void Build_单节课生成四个时刻且时间正确()
    {
        var moments = BellTimelineCalculator.Build(new[] { Class("A", "08:00", "08:40") }, Default, Day);

        Assert.Equal(4, moments.Count);
        Assert.Equal(TimeSpan.Parse("07:57"), TimeOf(Single(moments, BellMomentKind.PreClassNotice).At));
        Assert.Equal(TimeSpan.Parse("07:58"), TimeOf(Single(moments, BellMomentKind.PrepareBell).At));
        Assert.Equal(TimeSpan.Parse("08:00"), TimeOf(Single(moments, BellMomentKind.ClassOnNotice).At));
        Assert.Equal(TimeSpan.Parse("08:41"), TimeOf(Single(moments, BellMomentKind.ClassEnd).At)); // 下课后延迟 60 秒
    }

    [Fact]
    public void Build_按时间升序排列()
    {
        var moments = BellTimelineCalculator.Build(
            new[] { Class("A", "08:00", "08:40"), Class("B", "08:50", "09:30") }, Default, Day);

        var sorted = moments.OrderBy(m => m.At).Select(m => m.At).ToList();
        Assert.Equal(sorted, moments.Select(m => m.At).ToList());
    }

    [Fact]
    public void Build_课间槽位不产生事件()
    {
        var moments = BellTimelineCalculator.Build(
            new[] { Break("B0", "07:50", "08:00"), Class("A", "08:00", "08:40") }, Default, Day);

        Assert.Equal(4, moments.Count);
    }

    [Fact]
    public void Build_预备时长为负时回落到默认值()
    {
        var moments = BellTimelineCalculator.Build(new[] { Class("A", "08:00", "08:40", prep: -1) }, Default, Day);

        // 默认 120 秒 → 预备铃 07:58
        Assert.Equal(TimeSpan.Parse("07:58"), TimeOf(Single(moments, BellMomentKind.PrepareBell).At));
    }

    [Fact]
    public void Build_预告越过课间起点时裁剪到课间中点()
    {
        // 预备 10 分钟 + 预告 1 分钟 = 11 分钟前 → 07:49，早于课间起点 07:50
        var moments = BellTimelineCalculator.Build(
            new[] { Break("B0", "07:50", "08:00"), Class("A", "08:00", "08:40", prep: 600) },
            Default, Day);

        Assert.Equal(TimeSpan.Parse("07:55"), TimeOf(Single(moments, BellMomentKind.PreClassNotice).At));
    }

    [Fact]
    public void Build_预告越过课间且策略为跳过时不产生预告()
    {
        var options = Default with { PreNoticeOverflow = PreNoticeOverflowPolicy.Skip };
        var moments = BellTimelineCalculator.Build(
            new[] { Break("B0", "07:50", "08:00"), Class("A", "08:00", "08:40", prep: 600) },
            options, Day);

        Assert.DoesNotContain(moments, m => m.Kind == BellMomentKind.PreClassNotice);
        Assert.Equal(3, moments.Count);
    }

    [Fact]
    public void Build_无前序课间时不裁剪预告()
    {
        var moments = BellTimelineCalculator.Build(
            new[] { Class("A", "08:00", "08:40", prep: 600) }, Default, Day);

        Assert.Equal(TimeSpan.Parse("07:49"), TimeOf(Single(moments, BellMomentKind.PreClassNotice).At));
    }

    [Fact]
    public void Build_下一节预备铃早于上节下课延迟时丢弃上节下课事件()
    {
        // A 08:00–08:40，下课延迟到 08:41；B 08:45 上课但预备 5 分钟 → 预备铃 08:40，抢先于 A 的下课
        var moments = BellTimelineCalculator.Build(new[]
        {
            Class("A", "08:00", "08:40"),
            Break("B0", "08:40", "08:45"),
            Class("B", "08:45", "09:25", prep: 300)
        }, Default, Day);

        Assert.DoesNotContain(moments, m => m.Kind == BellMomentKind.ClassEnd && m.TimeLayoutItemId == "A");
        Assert.Contains(moments, m => m.Kind == BellMomentKind.ClassEnd && m.TimeLayoutItemId == "B");
        Assert.Contains(moments, m => m.Kind == BellMomentKind.PrepareBell && m.TimeLayoutItemId == "B");
    }

    [Fact]
    public void Build_预备铃不抢先时保留下课事件()
    {
        var moments = BellTimelineCalculator.Build(new[]
        {
            Class("A", "08:00", "08:40"),
            Break("B0", "08:40", "08:50"),
            Class("B", "08:50", "09:30") // 预备铃 08:48，晚于 A 的下课 08:41
        }, Default, Day);

        Assert.Contains(moments, m => m.Kind == BellMomentKind.ClassEnd && m.TimeLayoutItemId == "A");
    }

    [Fact]
    public void FindMakeup_取窗口内最近一条已过去的事件()
    {
        var moments = BellTimelineCalculator.Build(new[] { Class("A", "08:00", "08:40") }, Default, Day);
        var now = Day.AddHours(8).AddSeconds(5); // 08:00:05，刚过正式铃

        var makeup = BellTimelineCalculator.FindMakeup(moments, now, TimeSpan.FromMinutes(30));
        Assert.NotNull(makeup);
        Assert.Equal(BellMomentKind.ClassOnNotice, makeup!.Kind);
    }

    [Fact]
    public void FindMakeup_窗口外不补发()
    {
        var moments = BellTimelineCalculator.Build(new[] { Class("A", "08:00", "08:40") }, Default, Day);
        var now = Day.AddHours(8).AddMinutes(20);

        Assert.Null(BellTimelineCalculator.FindMakeup(moments, now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void NextAfter_严格晚于当前时刻()
    {
        var moments = BellTimelineCalculator.Build(new[] { Class("A", "08:00", "08:40") }, Default, Day);

        var next = BellTimelineCalculator.NextAfter(moments, Day.AddHours(8)); // 恰好 08:00
        Assert.NotNull(next);
        Assert.Equal(BellMomentKind.ClassEnd, next!.Kind);
    }

    [Fact]
    public void NextAfter_全部过去时返回null()
    {
        var moments = BellTimelineCalculator.Build(new[] { Class("A", "08:00", "08:40") }, Default, Day);
        Assert.Null(BellTimelineCalculator.NextAfter(moments, Day.AddHours(12)));
    }
}
