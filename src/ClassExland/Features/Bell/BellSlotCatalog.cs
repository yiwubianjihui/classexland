using ClassIsland.Shared.Models.Profile;

namespace ClassExland.Features.Bell;

/// <summary>课表里的一节上课段（TimeType=0），供设置页与 CLI 共用同一套序号。</summary>
public sealed record BellSlotInfo(
    int Index,
    Guid LayoutId,
    string LayoutName,
    int IndexInLayout,
    string SlotKey,
    string Display,
    TimeSpan Start,
    TimeSpan End,
    int EffectivePrep,
    bool Overridden);

/// <summary>
/// 把档案时间表展平成稳定的上课段列表。排序规则固定为「时间表名 → 开始时间」，
/// 保证设置页表格与 <c>cex bell list --index</c> 的序号永远对得上。
/// </summary>
public static class BellSlotCatalog
{
    public static IReadOnlyList<BellSlotInfo> Enumerate(Profile? profile, BellSettings settings)
    {
        var result = new List<BellSlotInfo>();
        if (profile?.TimeLayouts == null) return result;

        var layouts = profile.TimeLayouts
            .OrderBy(p => p.Value?.Name ?? "", StringComparer.Ordinal)
            .ThenBy(p => p.Key);

        var index = 0;
        foreach (var (layoutId, layout) in layouts)
        {
            if (layout?.Layouts == null) continue;

            var inLayout = 0;
            foreach (var item in layout.Layouts.Where(s => s.TimeType == 0).OrderBy(s => s.StartTime))
            {
                index++;
                inLayout++;
                var key = BellProfileWriter.MakeSlotKey(layoutId, item);
                var attached = item.GetAttachedObject<BellSlotSettings>(BellConstants.SlotAttachedGuid);
                // 宿主在附加设置上提供了启用开关，关掉等同于"未设置"，要退回 bell.json / 默认值。
                var active = attached is { IsAttachSettingsEnabled: true };
                var overridden = active || settings.HasPrepOverride(key);
                var effective = active ? attached!.PrepSeconds : settings.GetPrep(key);
                var name = string.IsNullOrWhiteSpace(layout.Name) ? "时间表" : layout.Name;
                var time = $"{item.StartTime:hh\\:mm}-{item.EndTime:hh\\:mm}";

                result.Add(new BellSlotInfo(
                    index,
                    layoutId,
                    name,
                    inLayout,
                    key,
                    $"{name} · 第 {inLayout} 节 {time}",
                    item.StartTime,
                    item.EndTime,
                    effective,
                    overridden));
            }
        }

        return result;
    }
}
