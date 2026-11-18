using System.Reflection;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using HarmonyLib;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Bell;

/// <summary>
/// 用户显式开启的宿主状态覆盖：patch LessonsService.ProcessLessons 的收尾，
/// 在宿主算完原生状态后，把 CurrentState 同步为我们的虚拟状态（T−N 即 OnClass）。
/// 幂等、可回滚；任何失败只记录日志并降级为虚拟状态，不会让插件崩溃或被禁用。
/// </summary>
public sealed class HarmonyPatcher : IHostedService
{
    private static ILogger<HarmonyPatcher>? _staticLogger;
    private static BellTimelineService? _activeTimeline;
    private static PropertyInfo? _currentStateProperty;

    private readonly ILessonsService _lessonsService;
    private readonly BellSettings _settings;
    private readonly BellTimelineService _timeline;
    private readonly ILogger<HarmonyPatcher> _logger;
    private Harmony? _harmony;
    private MethodInfo? _target;
    private bool _patched;

    public HarmonyPatcher(
        ILessonsService lessonsService,
        BellSettings settings,
        BellTimelineService timeline,
        ILogger<HarmonyPatcher> logger)
    {
        _lessonsService = lessonsService;
        _settings = settings;
        _timeline = timeline;
        _logger = logger;
        _staticLogger = logger;
        _activeTimeline = timeline;
    }

    /// <summary>当前是否真的 patch 成功。</summary>
    public static bool IsPatched { get; private set; }

    /// <summary>最近一次 patch 是否失败（供设置页提示降级）。</summary>
    public static bool LastPatchFailed { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.EnableHostStateOverride)
        {
            _logger.LogInformation("EnableHostStateOverride 为 false，跳过宿主状态覆盖。");
            return Task.CompletedTask;
        }
        if (_patched) return Task.CompletedTask;

        try
        {
            var type = _lessonsService.GetType();
            _target = type.GetMethod("ProcessLessons", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?? type.GetMethod("UpdateTimeState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _currentStateProperty = type.GetProperty("CurrentState", BindingFlags.Instance | BindingFlags.Public);

            if (_target == null || _currentStateProperty == null)
            {
                LastPatchFailed = true;
                _logger.LogWarning("未找到宿主状态计算方法，已降级为虚拟状态。");
                return Task.CompletedTask;
            }

            _harmony = new Harmony(BellConstants.HarmonyId);
            _harmony.Patch(_target, postfix: new HarmonyMethod(typeof(HarmonyPatcher), nameof(AfterProcessLessons)));
            _patched = true;
            IsPatched = true;
            LastPatchFailed = false;
            _logger.LogInformation("宿主状态覆盖已启用（patch {Method}）。", _target.Name);
        }
        catch (Exception ex)
        {
            LastPatchFailed = true;
            IsPatched = false;
            _logger.LogWarning(ex, "Harmony patch 宿主状态失败，已降级为虚拟状态。");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unpatch();
        return Task.CompletedTask;
    }

    /// <summary>设置页切换开关时调用，幂等。</summary>
    public void Refresh()
    {
        if (_settings.EnableHostStateOverride) StartAsync(default);
        else Unpatch();
    }

    private void Unpatch()
    {
        try
        {
            if (_patched && _harmony != null && _target != null)
            {
                _harmony.Unpatch(_target, HarmonyPatchType.Postfix, BellConstants.HarmonyId);
                _harmony.UnpatchAll(BellConstants.HarmonyId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "撤销 Harmony patch 时出现异常，已忽略。");
        }
        finally
        {
            _patched = false;
            IsPatched = false;
        }
    }

    /// <summary>
    /// Harmony postfix：签名只声明 object __instance，避免编译期依赖宿主具体类型。
    /// </summary>
    private static void AfterProcessLessons(object __instance)
    {
        try
        {
            var timeline = _activeTimeline;
            if (timeline == null) return;
            var overrideState = timeline.GetHostOverride();
            if (overrideState == null) return;

            var prop = _currentStateProperty ?? __instance.GetType().GetProperty("CurrentState");
            if (prop == null || !prop.CanWrite) return;

            if (prop.GetValue(__instance) is TimeState current && current == overrideState.Value) return;
            prop.SetValue(__instance, overrideState.Value);
        }
        catch (Exception ex)
        {
            _staticLogger?.LogWarning(ex, "同步宿主时间状态时出现异常，已忽略并保持宿主原生状态。");
        }
    }
}
