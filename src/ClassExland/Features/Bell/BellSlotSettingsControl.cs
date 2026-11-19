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
        _prepBox.LostFocus += (_, _) => Refresh();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_prepBox);
        row.Children.Add(new TextBlock
        {
            Text = "秒",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75
        });

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
            Text = "预备铃提前量",
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(row);
        panel.Children.Add(_hint);
        Content = panel;

        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Settings 由宿主在控件构造后、挂到可视化树之前注入，这里才是可靠的读取时机。
        Refresh();
    }

    /// <summary>把设置对象的值拉进输入框。</summary>
    private void Refresh()
    {
        if (_prepBox is null) return;

        var settings = Settings;
        _syncing = true;
        try
        {
            var value = settings?.PrepSeconds ?? FallbackPrepSeconds;
            _prepBox.Text = Math.Clamp(value, MinPrepSeconds, MaxPrepSeconds)
                .ToString(CultureInfo.InvariantCulture);
            _prepBox.BorderBrush = null;
        }
        finally
        {
            _syncing = false;
        }

        UpdateHint();
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

        var clamped = Math.Clamp(value, MinPrepSeconds, MaxPrepSeconds);
        _prepBox.BorderBrush = null;
        if (settings.PrepSeconds != clamped)
            settings.PrepSeconds = clamped;
    }

    private void UpdateHint()
    {
        if (_hint is null) return;

        // PluginSdk 2.0.0.2 的基类没有暴露时间点类型（更高版本才有 AttachedTimePointTimeType），
        // 所以这里不区分上课/课间，统一提示"只对上课时段生效"。
        _hint.Text = $"上课时段开始前 {MinPrepSeconds}–{MaxPrepSeconds} 秒进入预备状态；0 表示不提前。"
                     + "只对上课时段生效，改完保存档案即可。";
    }
}
