using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums;

namespace ClassExland.Features.Bell;

/// <summary>
/// 档案编辑器 →「时间点」→ 附加设置里出现的界面。
///
/// 用户不必打开插件设置页，也不必记 slot id：在课表里选中某节课，
/// 直接在这里填这一节的预备铃提前量，保存档案即生效。
/// 数据落在 <c>TimeLayoutItem.AttachedObjects[我方GUID]</c>，与设置页表格、CLI 共用同一份。
/// </summary>
[AttachedSettingsUsage(AttachedSettingsTargets.TimePoint)]
[AttachedSettingsControlInfo(BellConstants.SlotAttachedGuidText, "预备铃（ClassExland）", "\uE823", true)]
public sealed class BellSlotSettingsControl : AttachedSettingsControlBase<BellSlotSettings>
{
    private const int MinPrepSeconds = 0;
    private const int MaxPrepSeconds = 600;
    private const int FallbackPrepSeconds = 120;

    private readonly TextBox _prepBox;
    private readonly ComboBox _modeBox;
    private readonly TextBlock _hint;
    private bool _syncing;

    public BellSlotSettingsControl()
    {
        _prepBox = new TextBox
        {
            Width = 96,
            MaxLength = 3,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _prepBox.TextChanged += (_, _) => Commit();
        // 只在"设置对象已经注入"时回读，否则会把用户刚输入的数值重置成默认 120，
        // 看起来就是"怎么改都改不动"。输入本身就是提交动作，失焦不需要再刷一次。
        _prepBox.LostFocus += (_, _) =>
        {
            if (Settings is not null && !HasValidText()) Refresh();
        };

        _modeBox = new ComboBox
        {
            Width = 180,
            ItemsSource = new[]
            {
                new ModeItem(BellPrepMode.Fixed, "固定提前量"),
                new ModeItem(BellPrepMode.ByBreak, "按课间时长（10分→2分 / 15分→3分）"),
                new ModeItem(BellPrepMode.Off, "无预备铃")
            },
            SelectedIndex = 0
        };
        _modeBox.SelectionChanged += (_, _) => Commit();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_modeBox);
        _prepLabel = new TextBlock { Text = "提前量", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        row.Children.Add(_prepLabel);
        row.Children.Add(_prepBox);
        _secondsLabel = new TextBlock
        {
            Text = "秒",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75
        };
        row.Children.Add(_secondsLabel);

        _hint = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "预备铃",
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(row);
        panel.Children.Add(_hint);
        Content = panel;

        Refresh();
    }

    private readonly TextBlock _prepLabel;
    private readonly TextBlock _secondsLabel;

    private sealed record ModeItem(BellPrepMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Settings 由宿主在控件构造后、挂到可视化树之前注入，这里才是可靠的读取时机。
        Refresh();
    }

    /// <summary>
    /// 宿主是通过 <c>AttachedSettingsControlPresenter</c> 给控件推 <c>AttachedTimePointTimeType</c> 的，
    /// 推送时机可能晚于构造与挂树。跟着属性变化重算一次，避免读到默认值 0 而把课间当成上课。
    /// 这个属性编译期不存在（PluginSdk 2.0.0.2 没有），只能按名字比对。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property?.Name == "AttachedTimePointTimeType")
            Refresh();
    }

    /// <summary>把设置对象的值拉进输入框。</summary>
    private void Refresh()
    {
        if (_prepBox is null) return;

        // 用户正在输入时不回写，否则宿主推送的刷新会把刚敲进去的数字冲掉，
        // 表现就是"怎么改都改不动"。
        var typing = _prepBox.IsFocused && HasValidText();

        var settings = Settings;
        _syncing = true;
        try
        {
            var mode = settings?.PrepMode ?? BellPrepMode.Fixed;
            _modeBox.SelectedIndex = mode switch
            {
                BellPrepMode.ByBreak => 1,
                BellPrepMode.Off => 2,
                _ => 0
            };
            ApplyModeVisibility(mode);

            if (!typing)
            {
                var value = settings?.PrepSeconds ?? FallbackPrepSeconds;
                _prepBox.Text = Math.Clamp(value, MinPrepSeconds, MaxPrepSeconds)
                    .ToString(CultureInfo.InvariantCulture);
            }
            _prepBox.BorderBrush = null;
        }
        finally
        {
            _syncing = false;
        }

        // 注意：这里只做「显示」，绝不修改 settings 的任何字段。
        //
        // 1.0.0.5/1.0.0.6 曾在非上课时段把 IsAttachSettingsEnabled 置为 false，想让课间不写回档案。
        // 但宿主的「启用开关」就是绑定在这个字段上的（IL 证据：Presenter.UpdateSourceSettings 读写
        // IAttachedSettings.IsAttachSettingsEnabled，且 HasEnabledState=true），于是出现两个毛病：
        //   · 用户一打开开关，控件渲染就把它写回 false → 再点该时间条目显示"又关掉了"；
        //   · 同一段逻辑还把输入框 IsEnabled 锁死 → 提前量根本改不动。
        // 现在输入框永远可编辑，是否真的响铃只用下方说明文字告知。
        _prepBox.IsEnabled = true;
        UpdateHint(TryGetTimePointType());
    }

    /// <summary>输入框 → 设置对象。宿主保存档案时会带上这个对象。</summary>
    private void Commit()
    {
        if (_syncing || _prepBox is null) return;

        var settings = Settings;
        if (settings is null) return;

        if (!int.TryParse(_prepBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            _prepBox.BorderBrush = Brushes.OrangeRed;
            return;
        }

        // 模式下拉：null（跟随全局）与 Fixed 都显示为"固定提前量"，选下拉即显式写入。
        if (_modeBox.SelectedItem is ModeItem item && settings.PrepMode != item.Mode)
        {
            settings.PrepMode = item.Mode;
            ApplyModeVisibility(item.Mode);
        }

        var clamped = Math.Clamp(value, MinPrepSeconds, MaxPrepSeconds);
        _prepBox.BorderBrush = null;
        if (settings.PrepSeconds != clamped)
            settings.PrepSeconds = clamped;
    }

    private void ApplyModeVisibility(BellPrepMode mode)
    {
        var showPrep = mode == BellPrepMode.Fixed;
        _prepLabel.IsVisible = showPrep;
        _prepBox.IsVisible = showPrep;
        _secondsLabel.IsVisible = showPrep;
    }

    /// <summary>
    /// 宿主注入的时间点类型，就是 <c>TimeLayoutItem.TimeType</c> 的原值
    /// （IL 证据：Presenter.UpdateContentAttachedTimePointTimeType 里
    /// <c>callvirt TimeLayoutItem.get_TimeType</c> → <c>callvirt set_AttachedTimePointTimeType</c>）。
    /// 0 = 上课，1 = 课间，2 = 其他（放学/分隔点）。
    /// 编译期的 PluginSdk 2.0.0.2 没有这个属性，宿主 2.1 运行时才有，所以走反射；
    /// 读不到时返回 null，按"上课"处理。
    /// </summary>
    private int? TryGetTimePointType()
    {
        try
        {
            var property = GetType().GetProperty("AttachedTimePointTimeType");
            var raw = property?.GetValue(this);
            if (raw is null) return null;
            return raw is int value ? value : Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }
        catch
        {
            // 宿主版本不支持 / 属性类型变化：退回不区分时段。
            return null;
        }
    }

    /// <summary>输入框里是不是一个能用的秒数（用于判断失焦时该不该回读覆盖）。</summary>
    private bool HasValidText()
    {
        var text = _prepBox?.Text?.Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
               && v >= MinPrepSeconds && v <= MaxPrepSeconds;
    }

    private void UpdateHint(int? timePointType)
    {
        if (_hint is null) return;

        if (timePointType is null)
        {
            _hint.Text = $"上课时段开始前 {MinPrepSeconds}–{MaxPrepSeconds} 秒进入预备状态；0 表示不提前。"
                         + "只对上课时段生效，改完保存档案即可。";
            return;
        }

        if (timePointType == 0)
        {
            _hint.Text = $"这节课开始前 {MinPrepSeconds}–{MaxPrepSeconds} 秒进入预备状态；0 表示不提前。"
                         + "改完保存档案即可，不需要重启。";
            return;
        }

        // 课间/分隔点：提前量会照常保存，只是时间轴不会在它之前响铃。
        // 这里只说明事实，不锁输入框、也不动启用开关——那两件事曾经让用户以为"改不动"。
        _hint.Text = "这一条是课间或其他非上课时段，预备铃声不会在它之前响；"
                     + "提前量仍会保存到档案里，改成上课时段后即刻生效。";
    }
}
