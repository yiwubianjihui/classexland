using System.Reflection;
using ClassExland.Contracts;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using Microsoft.Extensions.Hosting;

namespace ClassExland.Features.Layout;

/// <summary>
/// 界面切换编排，同时实现 ILayoutService 供 Bridge 的 layout.switch/current 命令复用。
///
/// 时间源唯一来自 IBellTimeline（由 BellModule 订阅宿主 ILessonsService.PostMainTimerTicked
/// 驱动），本模块不再自己起轮询定时器，也不重写任何非虚的 NotificationProviderBase.StartAsync。
/// 下层下课延迟只是一次性 Task.Delay，不是轮询计时器。
///
/// 行为：
/// - PrepareBell / ClassOnNotice → 立即应用上课配置。
/// - ClassEnd → 延迟 D 秒应用下课配置；若到点宿主仍 OnClass，按 30s 顺延最多 3 次，
///   之后放弃并记录日志；期间若又进入上课则取消本次下课。
/// </summary>
public sealed class LayoutModule : IHostedService, ILayoutService
{
    private readonly IBellTimeline _timeline;
    private readonly ILayoutApplier _applier;
    private readonly LayoutConfigStore _store;
    private readonly LayoutLog _log;

    private readonly object _gate = new();
    private CancellationTokenSource? _breakCts;
    private string _currentLayout = "";

    public LayoutModule(
        IBellTimeline timeline,
        ILayoutApplier applier,
        LayoutConfigStore store,
        LayoutLog log)
    {
        _timeline = timeline;
        _applier = applier;
        _store = store;
        _log = log;
    }

    // ---- ILayoutService（反射契约：只带 target 一个参数）----

    public string CurrentLayout
    {
        get { lock (_gate) return _currentLayout; }
    }

    public async Task SwitchToAsync(string target)
    {
        try
        {
            LayoutTargetKind kind;
            switch ((target ?? "").Trim().ToLowerInvariant())
            {
                case "class":
                    kind = LayoutTargetKind.Class;
                    break;
                case "break":
                    kind = LayoutTargetKind.Break;
                    break;
                default:
                    _log.Warn($"忽略未知布局目标：{target}（仅支持 class / break）");
                    return;
            }

            // 幂等：已是目标布局则不重复应用，避免 PrepareBell 与 VirtualStateChanged 双触发。
            var desired = kind == LayoutTargetKind.Class ? "class" : "break";
            lock (_gate)
            {
                if (_currentLayout == desired) return;
            }

            var config = _store.Load();
            var result = await _applier.ApplyAsync(kind, config).ConfigureAwait(false);
            if (result.Ok)
            {
                lock (_gate) { _currentLayout = desired; }
                _log.Info($"布局已切换为 {desired}");
            }
            else
            {
                _log.Warn($"布局切换失败（{target}）：{result.Message}");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"SwitchToAsync 异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- IHostedService ----

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = _store.Load();
            if (!config.Enabled) _log.Info("LayoutModule 已启动（Enabled=false，仅响应手动切换）");
            _timeline.MomentReached += OnMomentReached;
            _timeline.VirtualStateChanged += OnVirtualStateChanged;
            _log.Info($"LayoutModule 已订阅 IBellTimeline，Enabled={config.Enabled}");

            // 首次启动重算一次，防止状态残留。
            // 但宿主启动瞬间会连续发出多次状态变化，此刻读到的是瞬态（实测午休启动会读到 OnClass），
            // 因此延后一拍，等时间轴稳定后再按真实状态应用。
            if (config.Enabled) _ = ApplyInitialStateDeferredAsync();
        }
        catch (Exception ex)
        {
            _log.Error($"LayoutModule.StartAsync 异常：{ex.GetType().Name}: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _timeline.MomentReached -= OnMomentReached;
            _timeline.VirtualStateChanged -= OnVirtualStateChanged;
            CancelPendingBreak("宿主停止");
            _log.Info("LayoutModule 已停止");
        }
        catch (Exception ex)
        {
            _log.Error($"LayoutModule.StopAsync 异常：{ex.GetType().Name}: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private async Task ApplyInitialStateDeferredAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            ApplyInitialState();
        }
        catch (Exception ex)
        {
            _log.Error($"ApplyInitialStateDeferred 异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ApplyInitialState()
    {
        try
        {
            var state = _timeline.VirtualState;
            if (state is VirtualTimeState.PrepareOnClass or VirtualTimeState.OnClass)
                _ = SwitchToAsync("class");
            else if (state is VirtualTimeState.Breaking or VirtualTimeState.AfterSchool)
                _ = SwitchToAsync("break");
        }
        catch (Exception ex)
        {
            _log.Error($"ApplyInitialState 异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnMomentReached(object? sender, BellMoment moment)
    {
        try
        {
            if (!_store.Load().Enabled || moment is null) return;
            switch (moment.Kind)
            {
                case BellMomentKind.PrepareBell:
                case BellMomentKind.ClassOnNotice:
                    _ = SwitchToAsync("class");
                    break;
                case BellMomentKind.ClassEnd:
                    ScheduleBreak();
                    break;
                // PreClassNotice / ActivityAdvanceNotice 不改布局。
            }
        }
        catch (Exception ex)
        {
            _log.Error($"OnMomentReached 异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnVirtualStateChanged(object? sender, VirtualTimeState state)
    {
        try
        {
            if (!_store.Load().Enabled) return;
            if (state is VirtualTimeState.PrepareOnClass or VirtualTimeState.OnClass)
            {
                // 新一轮上课：取消可能待触发的下课，切回上课配置（下一节预备铃优先）。
                CancelPendingBreak("进入上课状态");
                _ = SwitchToAsync("class");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"OnVirtualStateChanged 异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ScheduleBreak()
    {
        try
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                _breakCts?.Cancel();
                _breakCts?.Dispose();
                cts = new CancellationTokenSource();
                _breakCts = cts;
            }
            _ = ApplyBreakAfterDelayAsync(cts);
        }
        catch (Exception ex)
        {
            _log.Error($"ScheduleBreak 异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>延迟 D 后应用下课配置，含拖堂顺延与取消逻辑。</summary>
    private async Task ApplyBreakAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            var config = _store.Load();
            var delay = Math.Max(0, config.ClassEndDelaySeconds);
            var maxPostpones = Math.Max(0, config.MaxPostpones);
            var step = Math.Max(1, config.PostponeStepSeconds);

            for (var attempt = 0; attempt <= maxPostpones; attempt++)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _log.Info("下课切换已取消（期间进入新课或宿主停止）");
                    return;
                }

                if (IsHostStillOnClass())
                {
                    if (attempt < maxPostpones)
                    {
                        _log.Warn($"到点仍处于上课（拖堂），第 {attempt + 1}/{maxPostpones} 次顺延 {step}s");
                        delay = step;
                        continue;
                    }

                    _log.Warn($"拖堂超过最大顺延次数（{maxPostpones}），放弃本次下课界面切换");
                    return;
                }

                await SwitchToAsync("break").ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"ApplyBreakAfterDelayAsync 异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_breakCts, cts))
                {
                    _breakCts?.Dispose();
                    _breakCts = null;
                }
            }
        }
    }

    private void CancelPendingBreak(string reason)
    {
        try
        {
            CancellationTokenSource? cts;
            lock (_gate)
            {
                cts = _breakCts;
                _breakCts = null;
            }
            if (cts is null) return;
            cts.Cancel();
            cts.Dispose();
            _log.Info($"取消待触发的下课切换：{reason}");
        }
        catch (Exception ex)
        {
            _log.Error($"CancelPendingBreak 异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 判定到点时宿主是否仍在上课。优先读宿主 ILessonsService 的当前状态；
    /// 读不到时回退到我方的 VirtualState（Harmony 覆盖开启时二者一致）。
    /// 注意：只用于拖堂判定，状态切换本身完全由 IBellTimeline 虚拟状态驱动。
    /// </summary>
    private bool IsHostStillOnClass()
    {
        try
        {
            var lessons = HostServiceResolver.TryGet<ILessonsService>();
            if (lessons is not null)
            {
                var type = lessons.GetType();
                var prop = type.GetProperty("CurrentTimeState", BindingFlags.Public | BindingFlags.Instance)
                           ?? type.GetProperty("TimeState", BindingFlags.Public | BindingFlags.Instance);
                if (prop?.GetValue(lessons)?.ToString() is { } stateText)
                {
                    if (stateText.Contains("OnClass", StringComparison.OrdinalIgnoreCase) ||
                        stateText.Contains("PrepareOnClass", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (stateText.Contains("Breaking", StringComparison.OrdinalIgnoreCase) ||
                        stateText.Contains("AfterSchool", StringComparison.OrdinalIgnoreCase) ||
                        stateText.Contains("None", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"读取宿主状态失败，回退虚拟状态：{ex.GetType().Name}: {ex.Message}");
        }

        return _timeline.VirtualState is VirtualTimeState.OnClass or VirtualTimeState.PrepareOnClass;
    }
}
