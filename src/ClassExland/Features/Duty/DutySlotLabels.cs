using System.Text.RegularExpressions;
using ClassExland.Features.Duty.Models;

namespace ClassExland.Features.Duty;

/// <summary>
/// 时段名字。值日表里时段本来就用「上午 / 下午 / 傍晚」这种粗粒度说法，
/// 界面与主界面徽章都不该出现 07:00 这种具体时间——那是边界，不是名字。
///
/// 旧配置（以及从 DutyList 迁移过来的数据）常常把开始时间直接当标签，
/// <see cref="Normalize"/> 会把它们换成粗粒度名字，并把值日项的时段引用一起改掉。
/// </summary>
public static class DutySlotLabels
{
    private static readonly Regex TimeLike =
        new(@"^\s*\d{1,2}\s*[:：.]\s*\d{1,2}\s*$", RegexOptions.Compiled);

    /// <summary>标签是不是「07:00」这类时间串。</summary>
    public static bool LooksLikeTime(string? label)
        => !string.IsNullOrWhiteSpace(label) && TimeLike.IsMatch(label);

    /// <summary>
    /// 按开始时间给一个粗粒度名字。只有一个时段且跨度 ≥ 6 小时时叫「全天」。
    /// </summary>
    public static string Describe(TimeSpan start, TimeSpan end, int slotCount)
    {
        var span = end > start ? end - start : TimeSpan.Zero;
        var hour = start.TotalHours;

        if (slotCount <= 1 && span >= TimeSpan.FromHours(6))
        {
            return "全天";
        }

        return hour switch
        {
            < 11.5 => "上午",
            < 15.5 => "下午",
            < 18.5 => "傍晚",
            _ => "晚上"
        };
    }

    /// <summary>
    /// 把方案里「像时间」的时段标签改成粗粒度名字，并同步改掉值日项的 SlotLabel。
    /// 返回是否发生了改动（调用方据此决定要不要落盘）。
    /// </summary>
    public static bool Normalize(DutyPlan plan)
    {
        if (plan.TimeSlots.Count == 0) return false;

        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in plan.TimeSlots)
        {
            if (!LooksLikeTime(slot.Label) && !string.IsNullOrWhiteSpace(slot.Label))
                taken.Add(slot.Label);
        }

        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in plan.TimeSlots)
        {
            if (!LooksLikeTime(slot.Label)) continue;

            var old = slot.Label;
            var baseName = Describe(slot.Start, slot.End, plan.TimeSlots.Count);

            var candidate = baseName;
            var suffix = 2;
            while (!taken.Add(candidate))
            {
                candidate = baseName + suffix;
                suffix++;
            }

            slot.Label = candidate;
            renamed[old] = candidate;
        }

        if (renamed.Count == 0) return false;

        foreach (var item in plan.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.SlotLabel)
                && renamed.TryGetValue(item.SlotLabel, out var updated))
            {
                item.SlotLabel = updated;
            }
        }

        return true;
    }
}
