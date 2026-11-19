using ClassExland.Contracts;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Bell;

/// <summary>
/// 唯一时间真相源：从宿主课表构建当天预备铃事件流，维护虚拟状态并为其他模块提供事件。
/// 不重写非虚的 StartAsync/StopAsync 之外的定时器机制——这里自身是 IHostedService，
/// 计时完全依赖 ILessonsService.PostMainTimerTicked。
/// </summary>
public sealed class BellTimelineService : IBellTimeline, IBellStateController, IHostedService
{
    private readonly ILessonsService _lessonsService;
    private readonly IExactTimeService _exactTime;
    private readonly BellSettings _settings;
    private readonly BellSoundService _sound;
    private readonly ILogger<BellTimelineService> _logger;

    private IReadOnlyList<BellMoment> _moments = Array.Empty<BellMoment>();
    private readonly HashSet<string> _fired = new();
    private DateTime _scheduleDate = DateTime.MinValue;
    private string? _scheduleSignature;
    private TimeSpan _lastClassStart = TimeSpan.MinValue;
    private bool _initialized;

    private VirtualTimeState _virtualState = VirtualTimeState.None;
    private VirtualTimeState? _forcedState;
    private readonly object _stateLock = new();

    public BellTimelineService(
        ILessonsService lessonsService,
        IExactTimeService exactTime,
        BellSettings settings,
        BellSoundService sound,
        ILogger<BellTimelineService> logger)
    {
        _lessonsService = lessonsService;
        _exactTime = exactTime;
        _settings = settings;
        _sound = sound;
        _logger = logger;
    }

    public VirtualTimeState VirtualState
    {
        get
        {
            lock (_stateLock) return _forcedState ?? _virtualState;
        }
    }

    public BellMoment? Current { get; private set; }

    public DateTimeOffset? NextBellAt { get; private set; }

    public event EventHandler<BellMoment>? MomentReached;

    public event EventHandler<VirtualTimeState>? VirtualStateChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _lessonsService.PostMainTimerTicked += OnMainTimerTicked;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "订阅主计时器失败，预备铃时间轴将不工作。");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _lessonsService.PostMainTimerTicked -= OnMainTimerTicked;
        }
        catch
        {
            // 关闭阶段忽略异常。
        }
        return Task.CompletedTask;
    }

    /// <summary>配置变更后调用：下一个 tick 强制重建当天时间轴。</summary>
    public void InvalidateSchedule()
    {
        _scheduleSignature = null;
        _initialized = false;
        lock (_stateLock) _forcedState = null;
    }

    /// <summary>
    /// CLI 反射入口（单参数重载）：由于 Bridge 的 TryInvokeAsync 只传 1 个参数，必须保留此重载。
    /// </summary>
    public Task SetVirtualStateAsync(VirtualTimeState state) => SetVirtualStateAsync(state, CancellationToken.None);

    /// <summary>
    /// 强制覆盖虚拟状态。传入 None 表示清除覆盖、恢复时间轴真实状态。
    /// </summary>
    public Task SetVirtualStateAsync(VirtualTimeState state, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        try
        {
            VirtualTimeState previous, current;
            lock (_stateLock)
            {
                previous = _forcedState ?? _virtualState;
                _forcedState = state == VirtualTimeState.None ? null : state;
                current = _forcedState ?? _virtualState;
            }
            if (previous != current) SafeRaise(VirtualStateChanged, current);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "强制设置虚拟状态失败，已忽略。");
        }
        return Task.CompletedTask;
    }

    /// <summary>供 HarmonyPatcher 调用：返回希望宿主呈现的状态；null 表示不覆盖。</summary>
    public TimeState? GetHostOverride()
    {
        if (!_settings.EnableBell) return null;
        return VirtualState switch
        {
            VirtualTimeState.None => null,
            VirtualTimeState.PrepareOnClass => TimeState.PrepareOnClass,
            VirtualTimeState.OnClass => TimeState.OnClass,
            VirtualTimeState.Breaking => TimeState.Breaking,
            VirtualTimeState.AfterSchool => TimeState.AfterSchool,
            _ => null
        };
    }

    private void OnMainTimerTicked(object? sender, EventArgs e)
    {
        try
        {
            var now = ToOffset(_exactTime.GetCurrentLocalDateTime());
            EnsureSchedule(now);

            if (!_settings.EnableBell)
            {
                UpdateNext(now);
                return;
            }

            if (!_initialized)
            {
                _initialized = true;
                FireMakeup(now);
                SeedStateFromHistory(now);
            }

            FireDue(now);
            UpdateNext(now);
        }
        catch (Exception ex)
        {
            // 插件回调抛异常会导致 ClassIsland 自动禁用插件，必须兜住。
            _logger.LogWarning(ex, "推进预备铃时间轴时出现异常，已忽略。");
        }
    }

    private void EnsureSchedule(DateTimeOffset now)
    {
        var plan = _lessonsService.CurrentClassPlan;
        var layout = plan?.TimeLayout;
        var signature = BuildSignature(plan);

        if (_scheduleDate == now.Date && signature == _scheduleSignature && layout != null)
            return;

        _scheduleDate = now.Date;
        _scheduleSignature = signature;
        _fired.Clear();
        Current = null;

        if (layout == null)
        {
            _moments = Array.Empty<BellMoment>();
            _lastClassStart = TimeSpan.MinValue;
            return;
        }

        var slots = new List<BellSlot>();
        var layoutId = plan!.TimeLayoutId;
        foreach (var item in layout.Layouts)
        {
            if (item.TimeType is not (0 or 1)) continue;

            var key = BellProfileWriter.MakeSlotKey(layoutId, item);
            var attached = item.GetAttachedObject<BellSlotSettings>(BellConstants.SlotAttachedGuid);
            var prep = attached?.PrepSeconds ?? _settings.GetPrep(key);

            slots.Add(new BellSlot(
                key,
                item.StartTime,
                item.EndTime,
                item.TimeType == 0 ? BellSlotKind.Class : BellSlotKind.Break,
                prep,
                null));
        }

        _lastClassStart = slots.Where(s => s.Kind == BellSlotKind.Class)
            .Select(s => s.Start)
            .DefaultIfEmpty(TimeSpan.MinValue)
            .Max();

        _moments = BellTimelineCalculator.Build(slots, _settings.ToOptions(), now);
        _logger.LogInformation("已重建今日预备铃时间轴，共 {Count} 个事件。", _moments.Count);
    }

    private static string BuildSignature(ClassPlan? plan)
    {
        if (plan == null) return "null";
        var changed = string.Join(",", plan.Classes.Select(c => c.IsChangedClass ? "1" : "0"));
        // 临时调课会写入 ClassPlan.AttachedObjects；把这些键纳入签名，一旦变化立即重算。
        var attachedKeys = string.Join(",", plan.AttachedObjects.Keys.OrderBy(k => k).Select(k => k.ToString("D")));
        return $"{plan.Name}|{plan.TimeLayoutId}|{plan.IsOverlay}|{plan.OverlaySourceId}|{changed}|{attachedKeys}";
    }

    private void FireMakeup(DateTimeOffset now)
    {
        var window = TimeSpan.FromSeconds(Math.Max(0, _settings.MakeupWindowSeconds));
        var makeup = BellTimelineCalculator.FindMakeup(_moments, now, window);
        if (makeup == null) return;
        // 只补发最近一个：更早的时点直接标记为已处理，避免启动时一次性弹出多条。
        foreach (var earlier in _moments.Where(m => m.At < makeup.At)) _fired.Add(Key(earlier));
        _logger.LogInformation("补发错过的预备铃事件：{Kind} @ {At}", makeup.Kind, makeup.At);
        Fire(makeup);
    }

    /// <summary>
    /// 启动（或长时间休眠恢复）后，用"已经过去的时点"直接推导当前状态。
    /// 不这么做的话 FireDue 会从当天最早的事件开始逐 tick 补发，等于把一上午的铃声
    /// 与提醒全部回放一遍（实测午休启动连发 5 次上课状态切换）。
    /// </summary>
    private void SeedStateFromHistory(DateTimeOffset now)
    {
        var state = _virtualState;
        foreach (var moment in _moments.OrderBy(m => m.At))
        {
            if (moment.At > now) break;
            _fired.Add(Key(moment));
            switch (moment.Kind)
            {
                case BellMomentKind.PrepareBell:
                    state = VirtualTimeState.OnClass;
                    break;
                case BellMomentKind.ClassEnd:
                    state = IsLastClass(moment.TimeLayoutItemId)
                        ? VirtualTimeState.AfterSchool
                        : VirtualTimeState.Breaking;
                    break;
                // 预告与正式上课仅提醒，不切换状态。
            }
        }

        VirtualTimeState previous, current;
        lock (_stateLock)
        {
            previous = _forcedState ?? _virtualState;
            _virtualState = state;
            current = _forcedState ?? _virtualState;
        }

        if (previous != current)
        {
            _logger.LogInformation("按历史时点推导当前状态：{Previous} -> {Current}", previous, current);
            SafeRaise(VirtualStateChanged, current);
        }
    }

    private void FireDue(DateTimeOffset now)
    {
        // 每 tick 最多推进一个事件，避免长时间卡顿后一次性弹出多条提醒。
        var due = _moments
            .Where(m => !_fired.Contains(Key(m)) && m.At <= now)
            .OrderBy(m => m.At)
            .FirstOrDefault();
        if (due != null) Fire(due);
    }

    private void Fire(BellMoment moment)
    {
        var key = Key(moment);
        if (!_fired.Add(key)) return;

        Current = moment;

        var newRealState = _virtualState;
        switch (moment.Kind)
        {
            case BellMomentKind.PrepareBell:
                newRealState = VirtualTimeState.OnClass;
                _sound.PlayBell();
                break;
            case BellMomentKind.ClassEnd:
                newRealState = IsLastClass(moment.TimeLayoutItemId)
                    ? VirtualTimeState.AfterSchool
                    : VirtualTimeState.Breaking;
                break;
            // 预告与正式上课仅提醒，不切换状态。
        }

        VirtualTimeState previous, current;
        lock (_stateLock)
        {
            previous = _forcedState ?? _virtualState;
            _virtualState = newRealState;
            current = _forcedState ?? _virtualState;
        }

        SafeRaise(MomentReached, moment);
        if (previous != current) SafeRaise(VirtualStateChanged, current);
    }

    private bool IsLastClass(string slotKey)
    {
        // _lastClassStart 对应的槽位键末尾含开始时间；用最后一次课的时刻比较即可。
        var suffix = $"|{_lastClassStart:hh\\:mm\\:ss}";
        return _lastClassStart != TimeSpan.MinValue && slotKey.EndsWith(suffix, StringComparison.Ordinal);
    }

    private void UpdateNext(DateTimeOffset now) =>
        NextBellAt = BellTimelineCalculator.NextAfter(_moments, now)?.At;

    private static string Key(BellMoment moment) =>
        $"{moment.At:O}|{(int)moment.Kind}|{moment.TimeLayoutItemId}";

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Local));

    private void SafeRaise<T>(EventHandler<T>? handler, T arg)
    {
        if (handler == null) return;
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)subscriber)(this, arg);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "预备铃事件处理器抛出异常，已隔离。");
            }
        }
    }
}
