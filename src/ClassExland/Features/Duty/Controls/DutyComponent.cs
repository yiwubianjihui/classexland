using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared.Enums;
using ClassExland.Contracts;
using ClassExland.Features.Duty.Models;
using ClassExland.Features.Duty.Services;

namespace ClassExland.Features.Duty.Controls;

/// <summary>
/// 主界面值日组件：严格单行“[时段] 项目：姓名 · 项目：姓名”，超长省略；
/// 空数据显示「暂无安排」占位而不是整块消失；编辑模式强制可见。
/// </summary>
[ComponentInfo("B4E8A1C7-6D23-4F90-A5B1-3C7D9E2F4A08", "值日表", "\uE823",
    "单行显示当前时段的值日名单，课间与上课均可用。")]
public partial class DutyComponent : ComponentBase<DutyComponentSettings>
{
    private const string EmptyPlaceholder = "暂无安排";

    private readonly DutyService _dutyService;
    private readonly IBellTimeline _bell;
    private readonly ILessonsService _lessonsService;
    private readonly IComponentsService _componentsService;

    private bool _settingsHooked;

    public DutyComponent(DutyService dutyService, IBellTimeline bell,
        ILessonsService lessonsService, IComponentsService componentsService)
    {
        _dutyService = dutyService;
        _bell = bell;
        _lessonsService = lessonsService;
        _componentsService = componentsService;
        InitializeComponent();

        try
        {
            _bell.MomentReached += OnBellMomentReached;
            _lessonsService.PostMainTimerTicked += OnPostMainTimerTicked;
            _lessonsService.CurrentTimeStateChanged += OnLessonsStateChanged;
        }
        catch
        {
            // 事件订阅失败不应导致插件被禁用。
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookSettings();
        Refresh();
    }

    private void OnBellMomentReached(object? sender, BellMoment e) => RefreshOnUiThread();

    private void OnPostMainTimerTicked(object? sender, EventArgs e) => RefreshOnUiThread();

    private void OnLessonsStateChanged(object? sender, EventArgs e) => RefreshOnUiThread();

    private void HookSettings()
    {
        if (_settingsHooked || Settings == null) return;
        Settings.PropertyChanged += (_, _) => RefreshOnUiThread();
        _settingsHooked = true;
    }

    private void RefreshOnUiThread() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        try
        {
            HookSettings();
            if (Settings == null) return;

            var view = _dutyService.CurrentAt(DateTimeOffset.Now);
            var parts = view?.Items.Take(Math.Max(1, Settings.MaxItems)).Select(item =>
            {
                var names = item.Persons.Count == 0 ? "—" : string.Join("、", item.Persons.Select(p => p.Name));
                return $"{item.Project}：{names}";
            }) ?? Enumerable.Empty<string>();
            var text = string.Join(" · ", parts);
            if (view != null && view.Items.Count > Settings.MaxItems) text += " …";

            var isEmpty = string.IsNullOrWhiteSpace(text);
            TitleText.Text = Settings.Title;
            BadgeText.Text = view == null ? "" : $"[{view.SlotLabel}]";
            BadgeText.IsVisible = !isEmpty && _dutyService.Settings.ShowSlotBadge;
            ItemsText.Text = isEmpty ? EmptyPlaceholder : text;
            ItemsText.Opacity = isEmpty ? 0.5 : 0.85;

            var visible = true;
            var state = _lessonsService.CurrentState;
            if (Settings.ShowOnlyOnBreak && state == TimeState.OnClass) visible = false;
            if (state == TimeState.AfterSchool && !Settings.ShowOnAfterSchool) visible = false;
            if (_componentsService.IsManagementMode) visible = true;

            RootCard.IsVisible = visible;
        }
        catch
        {
            // 刷新异常不应导致插件被禁用；保持上一次渲染结果。
        }
    }
}
