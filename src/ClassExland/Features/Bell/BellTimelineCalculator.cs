using ClassExland.Contracts;

namespace ClassExland.Features.Bell;

/// <summary>时间线槽位类型。仅用于纯逻辑计算，不依赖宿主类型。</summary>
public enum BellSlotKind
{
    Class,
    Break
}

/// <summary>
/// 纯逻辑层的课表槽位。由 BellTimelineService 从宿主 TimeLayoutItem 转换而来。
/// </summary>
public sealed record BellSlot(
    string Id,
    TimeSpan Start,
    TimeSpan End,
    BellSlotKind Kind,
    int PrepSeconds,
    string? Subject);

/// <summary>时间轴计算选项（纯逻辑，可单测）。</summary>
public sealed record BellTimelineOptions
{
    public int PrepSecondsDefault { get; init; } = 120;
    public int ClassEndDelaySeconds { get; init; } = 60;
    public int PreNoticeLeadSeconds { get; init; } = 60;
    public PreNoticeOverflowPolicy PreNoticeOverflow { get; init; } = PreNoticeOverflowPolicy.ClipToMidpoint;
    public int MakeupWindowSeconds { get; init; } = 1800;
}

/// <summary>
/// 预备铃时间轴的纯逻辑计算器。不引用任何宿主类型，可独立单元测试。
/// </summary>
public static class BellTimelineCalculator
{
    /// <summary>
    /// 根据当天课表槽位构建完整事件流，已应用边界裁剪与「下一节预备铃优先」规则。
    /// </summary>
    public static IReadOnlyList<BellMoment> Build(
        IReadOnlyList<BellSlot> slots,
        BellTimelineOptions options,
        DateTimeOffset day)
    {
        var result = new List<BellMoment>();
        if (slots == null || slots.Count == 0) return result;

        var lead = TimeSpan.FromSeconds(Math.Max(0, options.PreNoticeLeadSeconds));
        var endDelay = TimeSpan.FromSeconds(Math.Max(0, options.ClassEndDelaySeconds));

        foreach (var slot in slots)
        {
            if (slot.Kind != BellSlotKind.Class) continue;

            var prep = slot.PrepSeconds >= 0 ? slot.PrepSeconds : options.PrepSecondsDefault;
            var prepSpan = TimeSpan.FromSeconds(prep);
            var noticeAt = slot.Start - prepSpan - lead;

            // 边界：N+60 超过前一课间时，按策略裁剪或跳过预告。
            var previousBreak = slots
                .Where(s => s.Kind == BellSlotKind.Break && s.End <= slot.Start)
                .OrderByDescending(s => s.End)
                .FirstOrDefault();

            var addNotice = true;
            if (previousBreak != null && noticeAt < previousBreak.Start)
            {
                switch (options.PreNoticeOverflow)
                {
                    case PreNoticeOverflowPolicy.Skip:
                        addNotice = false;
                        break;
                    case PreNoticeOverflowPolicy.ClipToMidpoint:
                        noticeAt = previousBreak.Start +
                                   TimeSpan.FromTicks((previousBreak.End - previousBreak.Start).Ticks / 2);
                        break;
                    // Keep: 保持原值
                }
            }

            if (addNotice)
            {
                result.Add(new BellMoment(At(day, noticeAt), BellMomentKind.PreClassNotice,
                    slot.Id, slot.Subject, prep));
            }

            result.Add(new BellMoment(At(day, slot.Start - prepSpan), BellMomentKind.PrepareBell,
                slot.Id, slot.Subject, prep));
            result.Add(new BellMoment(At(day, slot.Start), BellMomentKind.ClassOnNotice,
                slot.Id, slot.Subject, prep));
            result.Add(new BellMoment(At(day, slot.End + endDelay), BellMomentKind.ClassEnd,
                slot.Id, slot.Subject, prep));
        }

        result.Sort(static (a, b) =>
        {
            var c = a.At.CompareTo(b.At);
            return c != 0 ? c : ((int)a.Kind).CompareTo((int)b.Kind);
        });

        // 连续两节课：下一节预备铃优先于上一节下课延迟 → 丢弃被抢先的 ClassEnd。
        var prepareBells = result.Where(m => m.Kind == BellMomentKind.PrepareBell).ToList();
        var classEnds = result.Where(m => m.Kind == BellMomentKind.ClassEnd).ToList();
        foreach (var end in classEnds)
        {
            var endSlot = slots.FirstOrDefault(s => s.Id == end.TimeLayoutItemId);
            if (endSlot == null) continue;

            var superseded = prepareBells.Any(p =>
                p.TimeLayoutItemId != end.TimeLayoutItemId &&
                p.At <= end.At &&
                slots.Any(s => s.Id == p.TimeLayoutItemId && s.Kind == BellSlotKind.Class && s.Start >= endSlot.End));

            if (superseded) result.Remove(end);
        }

        return result;
    }

    /// <summary>
    /// 启动补发：在补发窗口内，取最近一个已经过去的事件（只补一条）。
    /// </summary>
    public static BellMoment? FindMakeup(
        IReadOnlyList<BellMoment> moments,
        DateTimeOffset now,
        TimeSpan makeupWindow)
    {
        var from = now - makeupWindow;
        return moments
            .Where(m => m.At <= now && m.At >= from)
            .OrderByDescending(m => m.At)
            .FirstOrDefault();
    }

    /// <summary>取当前时刻之后的下一条事件。</summary>
    public static BellMoment? NextAfter(IReadOnlyList<BellMoment> moments, DateTimeOffset now) =>
        moments.Where(m => m.At > now).OrderBy(m => m.At).FirstOrDefault();

    private static DateTimeOffset At(DateTimeOffset day, TimeSpan timeOfDay) =>
        new(day.DateTime.Date.Add(timeOfDay), day.Offset);
}
