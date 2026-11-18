using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;

namespace ClassExland.Features.Bell;

/// <summary>
/// 预备铃设置页：全局参数、Harmony 开关、铃声试听、档案写入预览/确认/回滚。
/// 所有事件处理器都包 try/catch，避免插件异常导致 ClassIsland 自动禁用插件。
/// </summary>
[SettingsPageInfo(BellConstants.SettingsPageId, "ClassExland 预备铃", "\uE823", "\uE823")]
public partial class BellSettingsPage : SettingsPageBase
{
    private readonly BellSettings _settings;
    private readonly BellProfileWriter _writer;
    private readonly BellSoundService _sound;
    private readonly BellTimelineService _timeline;
    private readonly HarmonyPatcher _patcher;

    public BellSettingsPage(
        BellSettings settings,
        BellProfileWriter writer,
        BellSoundService sound,
        BellTimelineService timeline,
        HarmonyPatcher patcher)
    {
        _settings = settings;
        _writer = writer;
        _sound = sound;
        _timeline = timeline;
        _patcher = patcher;
        InitializeComponent();
        DataContext = this;
        LoadToUi();
    }

    private void LoadToUi()
    {
        try
        {
            EnableBellCheck.IsChecked = _settings.EnableBell;
            HostOverrideCheck.IsChecked = _settings.EnableHostStateOverride;
            PrepSecondsBox.Text = _settings.DefaultPrepSeconds.ToString();
            ClassEndDelayBox.Text = _settings.ClassEndDelaySeconds.ToString();
            PreNoticeLeadBox.Text = _settings.PreNoticeLeadSeconds.ToString();
            MakeupWindowBox.Text = _settings.MakeupWindowSeconds.ToString();
            OverflowCombo.SelectedIndex = (int)_settings.PreNoticeOverflow;
            EnableSoundCheck.IsChecked = _settings.EnableBellSound;
            SoundPathBox.Text = _settings.BellSoundPath;
            VolumeBox.Text = _settings.BellVolume.ToString("0.##");
            UpdatePatchStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"载入设置失败：{ex.Message}";
        }
    }

    private void UpdatePatchStatus()
    {
        var status = HarmonyPatcher.IsPatched
            ? "已启用（宿主 T−N 即进入 OnClass）"
            : HarmonyPatcher.LastPatchFailed
                ? "失败，已自动降级为虚拟状态"
                : "未启用";
        PatchStatusText.Text = $"宿主状态覆盖：{status}";
    }

    private void SaveFromUi()
    {
        _settings.EnableBell = EnableBellCheck.IsChecked == true;
        _settings.EnableHostStateOverride = HostOverrideCheck.IsChecked == true;
        _settings.DefaultPrepSeconds = Math.Clamp(ParseInt(PrepSecondsBox.Text, 120), 0, 3600);
        _settings.ClassEndDelaySeconds = Math.Clamp(ParseInt(ClassEndDelayBox.Text, 60), 0, 300);
        _settings.PreNoticeLeadSeconds = Math.Clamp(ParseInt(PreNoticeLeadBox.Text, 60), 0, 600);
        _settings.MakeupWindowSeconds = Math.Clamp(ParseInt(MakeupWindowBox.Text, 1800), 0, 86400);
        _settings.PreNoticeOverflow = (PreNoticeOverflowPolicy)Math.Clamp(OverflowCombo.SelectedIndex, 0, 2);
        _settings.EnableBellSound = EnableSoundCheck.IsChecked == true;
        _settings.BellSoundPath = SoundPathBox.Text?.Trim() ?? "";
        _settings.BellVolume = Math.Clamp(ParseFloat(VolumeBox.Text, 0.8f), 0f, 1f);
        _settings.Save();

        // 配置变更后立即重算时间轴，并按新开关应用/撤销 Harmony patch。
        _timeline.InvalidateSchedule();
        _patcher.Refresh();
        UpdatePatchStatus();
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text?.Trim(), out var value) ? value : fallback;

    private static float ParseFloat(string? text, float fallback) =>
        float.TryParse(text?.Trim(), out var value) ? value : fallback;

    private void ButtonSave_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            SaveFromUi();
            StatusText.Text = "设置已保存。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
        }
    }

    private void ButtonTestSound_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            SaveFromUi();
            _sound.PlayTest();
            StatusText.Text = "已尝试试听；若未出声，请检查铃声路径与宿主音频设备。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"试听失败：{ex.Message}";
        }
    }

    private void ButtonPreview_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var preview = _writer.BuildPreview();
            PreviewList.ItemsSource = preview;
            StatusText.Text = preview.Count == 0
                ? "没有可配置的上课段。"
                : $"预览 {preview.Count} 条变更；确认后才会写入档案。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"生成预览失败：{ex.Message}";
        }
    }

    private void ButtonApply_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = _writer.Apply()
                ? "已写入档案，可随时点「回滚」恢复。"
                : "写入失败，请查看日志。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"写入失败：{ex.Message}";
        }
    }

    private void ButtonRollback_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = _writer.Rollback()
                ? "已回滚到写入前的状态。"
                : "当前没有可回滚的写入。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"回滚失败：{ex.Message}";
        }
    }
}
