using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassExland.Features.Bell;

/// <summary>
/// 设置页"每节课提前量"表格的一行。一行对应档案里一个上课段（TimeType=0），
/// 键与时间轴使用同一套 <see cref="BellProfileWriter.MakeSlotKey"/> 规则。
/// </summary>
public sealed class BellSlotRow : ObservableObject
{
    private bool _useOverride;
    private string _prepText;

    public BellSlotRow(string slotKey, string layoutName, int index, string timeText, int effectivePrep)
    {
        SlotKey = slotKey;
        LayoutName = layoutName;
        Index = index;
        TimeText = timeText;
        _useOverride = false;
        _prepText = effectivePrep.ToString();
    }

    public string SlotKey { get; }

    public string LayoutName { get; }

    /// <summary>该时间表内的第几节课（从 1 开始）。</summary>
    public int Index { get; }

    public string TimeText { get; }

    public string Display => $"{LayoutName} · 第 {Index} 节 {TimeText}";

    /// <summary>是否单独设置（勾选后写覆盖；取消则回退到默认提前量）。</summary>
    public bool UseOverride
    {
        get => _useOverride;
        set { if (value == _useOverride) return; _useOverride = value; OnPropertyChanged(); }
    }

    /// <summary>用字符串承载，便于 DataGrid 内联编辑时容忍空值与临时输入。</summary>
    public string PrepText
    {
        get => _prepText;
        set { if (value == _prepText) return; _prepText = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>解析当前输入，非法时返回 null。</summary>
    public int? TryParsePrep() =>
        int.TryParse(_prepText?.Trim(), out var value) && value is >= 0 and <= 600 ? value : null;
}
