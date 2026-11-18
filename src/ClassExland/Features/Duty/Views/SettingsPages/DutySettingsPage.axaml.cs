using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using ClassExland.Contracts;
using ClassExland.Features.Duty.Migration;
using ClassExland.Features.Duty.Models;
using ClassExland.Features.Duty.Reminders;
using ClassExland.Features.Duty.Services;
using Microsoft.Extensions.Hosting;

namespace ClassExland.Features.Duty.Views.SettingsPages;

/// <summary>
/// 值日设置页：值日表（DataGrid 内联编辑）/ 时段与显示 / 特殊活动 / 导入导出 / 诊断。
/// </summary>
[SettingsPageInfo("yibianhui.classexland.duty", "值日表与提醒", "\uE823", "\uE823")]
public partial class DutySettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    private readonly DutyService _duty;
    private readonly ActivityService _activity;
    private readonly MigrationService _migration;

    private DutyPlan? _selectedPlan;
    private SpecialActivity? _selectedActivity;
    private ActivityGroup? _effectiveGroup;
    private string _validationHint = "";
    private string _rotationSummary = "";
    private string _previewText = "";
    private string _importPath = "";
    private string _importResult = "";
    private string _luckyPickResult = "";
    private string _weekStatus = "";
    private string _activityNamesText = "";
    private readonly ObservableCollection<MigrationConflict> _conflicts = new();
    private readonly Stack<string> _undoStack = new();

    private bool _effectiveGroupHooked;

    public new event PropertyChangedEventHandler? PropertyChanged;

    public DutySettingsPage(DutyService duty, ActivityService activity, MigrationService migration)
    {
        _duty = duty;
        _activity = activity;
        _migration = migration;
        InitializeComponent();
        DataContext = this;

        SelectedPlan = duty.Settings.Plans.FirstOrDefault();
        SelectedActivity = activity.Settings.Activities.FirstOrDefault();
        Refresh();
    }

    public DutySettings Settings => _duty.Settings;
    public ActivitySettings ActivitySettings => _activity.Settings;
    public ObservableCollection<DutyPlan> Plans => Settings.Plans;
    public ObservableCollection<SpecialActivity> Activities => ActivitySettings.Activities;
    public ObservableCollection<MigrationConflict> Conflicts => _conflicts;

    public ObservableCollection<DutyItem>? Items => SelectedPlan?.Items;
    public ObservableCollection<TimeSlot>? Slots => SelectedPlan?.TimeSlots;

    public DutyPlan? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (Equals(value, _selectedPlan)) return;
            _selectedPlan = value;
            _undoStack.Clear();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Items));
            OnPropertyChanged(nameof(Slots));
            Refresh();
        }
    }

    public SpecialActivity? SelectedActivity
    {
        get => _selectedActivity;
        set
        {
            if (Equals(value, _selectedActivity)) return;
            _selectedActivity = value;
            _effectiveGroupHooked = false;
            OnPropertyChanged();
            RefreshActivity();
        }
    }

    public ActivityGroup? EffectiveGroup
    {
        get => _effectiveGroup;
        private set
        {
            if (Equals(value, _effectiveGroup)) return;
            if (_effectiveGroup != null) _effectiveGroup.PropertyChanged -= OnEffectiveGroupChanged;
            _effectiveGroup = value;
            if (_effectiveGroup != null && !_effectiveGroupHooked)
            {
                _effectiveGroup.PropertyChanged += OnEffectiveGroupChanged;
                _effectiveGroupHooked = true;
            }
            OnPropertyChanged();
        }
    }

    public string EffectiveGroupName => EffectiveGroup?.Name ?? "（无名单组）";
    public string ActivityNamesText => _activityNamesText;
    public string WeekStatus => _weekStatus;

    public string ValidationHint => _validationHint;
    public bool HasValidationHint => !string.IsNullOrWhiteSpace(_validationHint);

    public string RotationSummary => _rotationSummary;
    public string PreviewText => _previewText;

    public string ImportPath
    {
        get => _importPath;
        set
        {
            if (value == _importPath) return;
            _importPath = value;
            OnPropertyChanged();
        }
    }

    public string ImportResult
    {
        get => _importResult;
        set
        {
            if (value == _importResult) return;
            _importResult = value;
            OnPropertyChanged();
        }
    }

    public string LuckyPickResult
    {
        get => _luckyPickResult;
        set
        {
            if (value == _luckyPickResult) return;
            _luckyPickResult = value;
            OnPropertyChanged();
        }
    }

    public bool HasConflicts => _conflicts.Count > 0;

    /// <summary>迁移冲突下拉框选项：枚举值 + 中文标签，避免设置页出现英文枚举。</summary>
    public IReadOnlyList<ConflictChoiceOption> ConflictChoices { get; } = new[]
    {
        new ConflictChoiceOption(MigrationConflictChoice.Ask, "待决定"),
        new ConflictChoiceOption(MigrationConflictChoice.Keep, "保留现有"),
        new ConflictChoiceOption(MigrationConflictChoice.Replace, "用导入的替换"),
        new ConflictChoiceOption(MigrationConflictChoice.Skip, "跳过这条")
    };

    // ---- 值日表编辑 ----

    private void ButtonAddPlan_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var plan = new DutyPlan
            {
                Name = $"值日表 {Settings.Plans.Count + 1}",
                TimeSlots = new ObservableCollection<TimeSlot>
                {
                    new() { Label = "上午", Start = new TimeSpan(7, 0, 0), End = new TimeSpan(11, 50, 0) },
                    new() { Label = "下午", Start = new TimeSpan(13, 30, 0), End = new TimeSpan(17, 5, 0) }
                }
            };
            Settings.Plans.Add(plan);
            SelectedPlan = plan;
            _duty.Save();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ButtonRemovePlan_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedPlan == null || Settings.Plans.Count <= 1) return;
            Settings.Plans.Remove(SelectedPlan);
            SelectedPlan = Settings.Plans.FirstOrDefault();
            _duty.Save();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void PushUndo()
    {
        if (SelectedPlan == null) return;
        try
        {
            _undoStack.Push(JsonSerializer.Serialize(SelectedPlan));
            if (_undoStack.Count > 30) TrimUndo();
        }
        catch { /* 内存中的撤销快照失败不影响编辑 */ }
    }

    private void TrimUndo()
    {
        while (_undoStack.Count > 30) _undoStack.Pop();
    }

    private void ButtonUndo_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedPlan == null || _undoStack.Count == 0) return;
            var restored = JsonSerializer.Deserialize<DutyPlan>(_undoStack.Pop());
            if (restored == null) return;
            var index = Settings.Plans.IndexOf(SelectedPlan);
            if (index < 0) return;
            Settings.Plans[index] = restored;
            SelectedPlan = restored;
            _duty.Save();
            Refresh();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ButtonAddItem_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedPlan == null) return;
            PushUndo();
            SelectedPlan.Items.Add(new DutyItem
            {
                Project = "新项目",
                Persons = "",
                SlotLabel = "",
                Weekday = -1
            });
            _duty.Save();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ButtonRemoveItem_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedPlan == null || ItemsGrid.SelectedItem is not DutyItem item) return;
            PushUndo();
            SelectedPlan.Items.Remove(item);
            _duty.Save();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ButtonAddSlot_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedPlan == null) return;
            PushUndo();
            SelectedPlan.TimeSlots.Add(new TimeSlot { Label = "新时段", Start = new TimeSpan(8, 0, 0), End = new TimeSpan(9, 0, 0) });
            _duty.Save();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ButtonRemoveSlot_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedPlan == null || SlotsGrid.SelectedItem is not TimeSlot slot) return;
            PushUndo();
            SelectedPlan.TimeSlots.Remove(slot);
            _duty.Save();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ItemsGrid_OnCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        try
        {
            Validate();
            if (SelectedPlan != null && SelectedPlan.TimeSlots.Count == 0)
            {
                _validationHint = "请至少添加一个时段，否则组件无法显示当前时段。";
                OnPropertyChanged(nameof(ValidationHint));
                OnPropertyChanged(nameof(HasValidationHint));
            }
            _duty.Save();
            Refresh();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ItemsGrid_OnRowEditEnded(object? sender, DataGridRowEditEndedEventArgs e)
    {
        try
        {
            Validate();
            _duty.Save();
            Refresh();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Validate()
    {
        _validationHint = "";
        if (SelectedPlan == null) return;
        foreach (var item in SelectedPlan.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Project))
            {
                _validationHint = "存在空的项目名，请填写。";
                break;
            }
            if (item.Weekday < -1 || item.Weekday > 6)
            {
                _validationHint = "星期只能是 -1（每天）或 0–6。";
                break;
            }
        }
        OnPropertyChanged(nameof(ValidationHint));
        OnPropertyChanged(nameof(HasValidationHint));
    }

    private void ButtonSave_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _duty.Save();
            _activity.Save();
            ImportResult = "配置已保存。";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    // ---- 提醒测试 ----

    private void ButtonTestDuty_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            GetProvider()?.ShowDutyReminder();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ButtonTestActivity_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            GetProvider()?.ShowActivityReminder(SelectedActivity?.Name);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static DutyReminderProvider? GetProvider()
        => IAppHost.TryGetService<IEnumerable<IHostedService>>()
            ?.OfType<DutyReminderProvider>()
            .FirstOrDefault();

    // ---- 特殊活动 ----

    private void ButtonAddPreset_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _activity.AddTrialHomeworkPreset();
            SelectedActivity = _activity.Settings.Activities.LastOrDefault();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ButtonConfirmWeek_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedActivity == null) return;
            _activity.ConfirmCurrentWeek(SelectedActivity, DateTime.Now);
            RefreshActivity();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnEffectiveGroupChanged(object? sender, PropertyChangedEventArgs e) => _activity.Save();

    private void RefreshActivity()
    {
        try
        {
            var now = DateTime.Now;
            var group = SelectedActivity == null ? null : _activity.GetEffectiveGroup(SelectedActivity, now);
            EffectiveGroup = group;
            _activityNamesText = SelectedActivity == null
                ? ""
                : "今日名单：" + _activity.GetNamesText(SelectedActivity, now);
            _weekStatus = SelectedActivity == null
                ? ""
                : (_activity.IsWeekConfirmed(SelectedActivity, now) ? "本周已确认" : "本周未确认，将不弹活动提醒");
            OnPropertyChanged(nameof(ActivityNamesText));
            OnPropertyChanged(nameof(WeekStatus));
            OnPropertyChanged(nameof(EffectiveGroupName));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    // ---- 导入导出 ----

    private async void ButtonPickImport_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择旧插件配置文件",
                AllowMultiple = false
            };
            var window = TopLevel.GetTopLevel(this) as Window;
            var files = await dialog.ShowAsync(window);
            if (files != null && files.Length > 0) ImportPath = files[0];
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ButtonPreviewImport_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var result = _migration.Preview(ImportPath);
            _conflicts.Clear();
            foreach (var conflict in result.Conflicts) _conflicts.Add(conflict);
            ImportResult = string.Join("\n", result.Messages);
            OnPropertyChanged(nameof(HasConflicts));
            Refresh();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ButtonApplyImport_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var choices = _conflicts
                .Where(c => c.Choice != MigrationConflictChoice.Ask)
                .ToDictionary(c => c.Key, c => c.Choice);
            var result = _migration.Apply(ImportPath, choices);
            ImportResult = string.Join("\n", result.Messages)
                           + (result.HasConflicts ? "\n仍有未解决的冲突，请逐条选择后再次应用。" : "");
            _conflicts.Clear();
            OnPropertyChanged(nameof(HasConflicts));
            SelectedPlan = Settings.Plans.FirstOrDefault();
            SelectedActivity = ActivitySettings.Activities.FirstOrDefault();
            Refresh();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ButtonExportCsv_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出值日历史 CSV",
                DefaultExtension = "csv",
                InitialFileName = $"值日历史-{DateTime.Today:yyyyMMdd}.csv"
            };
            var window = TopLevel.GetTopLevel(this) as Window;
            var path = await dialog.ShowAsync(window);
            if (string.IsNullOrWhiteSpace(path)) return;
            _duty.ExportHistoryCsv(path);
            ImportResult = $"已导出：{path}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ButtonLuckyDraw_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            LuckyPickResult = "今日幸运值日生：" + _duty.DrawLuckyPerson();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    // ---- 通用 ----

    private void Refresh()
    {
        try
        {
            _rotationSummary = _duty.DescribeRotation();
            var preview = _duty.GetRotationPreview(21);
            _previewText = string.Join("\n", preview.Select(p =>
                $"{p.Date:MM-dd dddd}  {p.PlanName}" + (p.IsSkipped ? "（跳过日，沿用上批次）" : "")));
            OnPropertyChanged(nameof(RotationSummary));
            OnPropertyChanged(nameof(PreviewText));

            var diag = new List<string>
            {
                $"配置目录：{_duty.Settings.ConfigPath}",
                $"启用：{Settings.Enabled}",
                $"值日表数量：{Settings.Plans.Count}",
                $"特殊活动数量：{ActivitySettings.Activities.Count}",
                $"当前查询：{(_duty.CurrentAt(DateTimeOffset.Now) == null ? "无" : _duty.CurrentAt(DateTimeOffset.Now)!.SlotLabel)}",
                $"今日值日：{_duty.GetDutyText(DateTime.Now)}",
                $"节假日跳过：{(Settings.EnableHolidaySkip ? "开" : "关")}",
                $"人名朗读：{(Settings.EnableTtsSpeech ? "开" : "关")}"
            };
            DiagnosticText = string.Join("\n", diag);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private string _diagnosticText = "";
    public string DiagnosticText
    {
        get => _diagnosticText;
        set
        {
            if (value == _diagnosticText) return;
            _diagnosticText = value;
            OnPropertyChanged();
        }
    }

    private void ShowError(Exception ex)
    {
        ImportResult = "操作失败：" + ex.Message;
        _validationHint = ex.Message;
        OnPropertyChanged(nameof(ValidationHint));
        OnPropertyChanged(nameof(HasValidationHint));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>迁移冲突下拉框的选项（枚举值 + 中文标签）。</summary>
public sealed record ConflictChoiceOption(MigrationConflictChoice Value, string Label);
