using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassExland.Contracts;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;

namespace ClassExland.Features.Layout;

/// <summary>
/// 界面切换设置页：上课 / 下课两套布局、下课延迟与拖堂保护。
/// 所有事件处理器都包 try/catch，避免插件异常导致 ClassIsland 自动禁用插件。
/// </summary>
[SettingsPageInfo("yibianhui.classexland.layout", "ClassExland 界面切换", "\uE823", "\uE823")]
public partial class LayoutSettingsPage : SettingsPageBase
{
    private readonly LayoutConfigStore _store;
    private readonly ILayoutService _layout;
    private readonly ILayoutApplier _applier;
    private readonly IPluginPaths _paths;
    private bool _loading;

    public LayoutSettingsPage(
        LayoutConfigStore store,
        ILayoutService layout,
        ILayoutApplier applier,
        IPluginPaths paths)
    {
        _store = store;
        _layout = layout;
        _applier = applier;
        _paths = paths;
        InitializeComponent();
        PathText.Text = $"配置文件：{_paths.Get(ConfigFiles.Layout)}";
        LoadToUi();
    }

    private void LoadToUi()
    {
        _loading = true;
        try
        {
            var config = _store.Load();
            EnableCheck.IsChecked = config.Enabled;
            ClassConfigBox.Text = config.ClassComponentConfig;
            ClassScaleBox.Text = config.ClassScale.ToString("0.##", CultureInfo.InvariantCulture);
            ClassOpacityBox.Text = config.ClassOpacity.ToString("0.##", CultureInfo.InvariantCulture);
            BreakConfigBox.Text = config.BreakComponentConfig;
            BreakScaleBox.Text = config.BreakScale.ToString("0.##", CultureInfo.InvariantCulture);
            BreakOpacityBox.Text = config.BreakOpacity.ToString("0.##", CultureInfo.InvariantCulture);
            ClassEndDelayBox.Text = config.ClassEndDelaySeconds.ToString(CultureInfo.InvariantCulture);
            PostponeStepBox.Text = config.PostponeStepSeconds.ToString(CultureInfo.InvariantCulture);
            MaxPostponesBox.Text = config.MaxPostpones.ToString(CultureInfo.InvariantCulture);

            var names = _applier.GetComponentConfigNames();
            ClassConfigCombo.ItemsSource = names;
            BreakConfigCombo.ItemsSource = names;
            ClassConfigCombo.IsVisible = names.Count > 0;
            BreakConfigCombo.IsVisible = names.Count > 0;

            StatusText.Text = names.Count > 0
                ? $"已读取 {names.Count} 个组件配置，可从下拉框选择，也可直接输入名称。"
                : "没有读到宿主的组件配置列表，请直接输入配置名（区分大小写）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取配置失败：{ex.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    private void ClassConfigCombo_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ClassConfigCombo.SelectedItem is string name)
            ClassConfigBox.Text = name;
    }

    private void BreakConfigCombo_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (BreakConfigCombo.SelectedItem is string name)
            BreakConfigBox.Text = name;
    }

    private void ButtonSave_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryBuildConfig(out var config, out var error))
            {
                StatusText.Text = error;
                return;
            }

            if (!_store.TrySave(config))
            {
                StatusText.Text = "保存失败：无法写入 layout.json，请检查配置目录权限。";
                return;
            }

            StatusText.Text = config.Enabled
                ? $"已保存。上课：{config.ClassComponentConfig}（{config.ClassScale:0.##} / {config.ClassOpacity:0.##}）；"
                  + $"下课：{config.BreakComponentConfig}（{config.BreakScale:0.##} / {config.BreakOpacity:0.##}）；"
                  + $"延迟 {config.ClassEndDelaySeconds} 秒。"
                : "已保存，但自动切换处于关闭状态（仍可用下面的按钮手动切换）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存异常：{ex.Message}";
        }
    }

    private bool TryBuildConfig(out LayoutConfig config, out string error)
    {
        config = _store.Load();
        error = "";

        config.Enabled = EnableCheck.IsChecked == true;
        config.ClassComponentConfig = (ClassConfigBox.Text ?? "").Trim();
        config.BreakComponentConfig = (BreakConfigBox.Text ?? "").Trim();

        if (!TryDouble(ClassScaleBox.Text, 0.1, 3.0, out var classScale, out error)) return false;
        if (!TryDouble(ClassOpacityBox.Text, 0.05, 1.0, out var classOpacity, out error)) return false;
        if (!TryDouble(BreakScaleBox.Text, 0.1, 3.0, out var breakScale, out error)) return false;
        if (!TryDouble(BreakOpacityBox.Text, 0.05, 1.0, out var breakOpacity, out error)) return false;
        if (!TryInt(ClassEndDelayBox.Text, 0, 300, "下课延迟", out var delay, out error)) return false;
        if (!TryInt(PostponeStepBox.Text, 1, 120, "顺延步长", out var step, out error)) return false;
        if (!TryInt(MaxPostponesBox.Text, 0, 10, "最多顺延次数", out var max, out error)) return false;

        config.ClassScale = classScale;
        config.ClassOpacity = classOpacity;
        config.BreakScale = breakScale;
        config.BreakOpacity = breakOpacity;
        config.ClassEndDelaySeconds = delay;
        config.PostponeStepSeconds = step;
        config.MaxPostpones = max;
        return true;
    }

    private static bool TryDouble(string? text, double min, double max, out double value, out string error)
    {
        error = "";
        if (double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && value >= min && value <= max)
            return true;

        error = $"数值必须在 {min.ToString(CultureInfo.InvariantCulture)}–{max.ToString(CultureInfo.InvariantCulture)} 之间（当前：{text}）。";
        return false;
    }

    private static bool TryInt(string? text, int min, int max, string label, out int value, out string error)
    {
        error = "";
        if (int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= min && value <= max)
            return true;

        error = $"{label}必须是 {min}–{max} 的整数（当前：{text}）。";
        return false;
    }

    private async void ButtonClass_OnClick(object? sender, RoutedEventArgs e)
        => await SwitchAsync("class");

    private async void ButtonBreak_OnClick(object? sender, RoutedEventArgs e)
        => await SwitchAsync("break");

    private async Task SwitchAsync(string target)
    {
        try
        {
            StatusText.Text = target == "class" ? "正在切到上课布局…" : "正在切到下课布局…";
            await _layout.SwitchToAsync(target).ConfigureAwait(true);
            StatusText.Text = target == "class" ? "已切到上课布局。" : "已切到下课布局。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"切换失败：{ex.Message}";
        }
    }

    private void ButtonReset_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!_store.TrySave(new LayoutConfig()))
            {
                StatusText.Text = "恢复默认失败：无法写入 layout.json。";
                return;
            }

            LoadToUi();
            StatusText.Text = "已恢复默认：上课 Default 0.8/0.5，下课 offclass 1/0.7，延迟 60 秒。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"恢复默认异常：{ex.Message}";
        }
    }

    private void ButtonGenerateAutomation_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var config = _store.Load();
            AutomationBox.Text = RecommendedAutomation.Generate(config);
            StatusText.Text = RecommendedAutomation.Description(config);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"生成失败：{ex.Message}";
        }
    }

    private async void ButtonCopyAutomation_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AutomationBox.Text))
                AutomationBox.Text = RecommendedAutomation.Generate(_store.Load());

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                StatusText.Text = "无法访问剪贴板，请手动全选复制。";
                return;
            }

            await clipboard.SetTextAsync(AutomationBox.Text ?? "");
            StatusText.Text = "推荐自动化片段已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void ButtonOpenFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.ConfigDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开文件夹失败：{ex.Message}";
        }
    }

    private void ButtonOpenLog_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var log = _paths.Get("layout.log");
            if (!File.Exists(log))
            {
                StatusText.Text = "还没有日志（本模块尚未触发过切换）。";
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = log, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开日志失败：{ex.Message}";
        }
    }
}
