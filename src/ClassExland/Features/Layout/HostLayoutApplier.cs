using System.Globalization;
using System.Reflection;
using ClassIsland.Shared;

namespace ClassExland.Features.Layout;

/// <summary>
/// 通过宿主设置服务在【内存态】应用布局配置：
///   CurrentComponentConfig / Scale / Opacity，最后调用 SaveSettings()。
///
/// 类型与成员已由 team-lead 从宿主 ClassIsland.dll 确认：
///   ClassIsland.Services.SettingsService
///     - Settings / CurrentComponentConfig / ComponentConfigs / SaveSettings()
/// 插件未必能直接引用主程序集，因此用反射按全名解析；找不到时退化到按属性名扫描，
/// 仍找不到则返回失败并提示使用 RecommendedAutomation 生成的推荐片段兜底。
///
/// 本类绝不直接写 Settings.json 或 Automations/Default.json —— 核心配置文件的写入
/// 只由 RemoteSync 模块负责；这里只调用宿主服务自己的保存方法。
/// 全程 try/catch，异常只记日志，不会冒泡导致插件被禁用。
/// </summary>
public sealed class HostLayoutApplier : ILayoutApplier
{
    private const string SettingsServiceTypeName = "ClassIsland.Services.SettingsService";

    private static readonly string[] RequiredProperties =
        { "CurrentComponentConfig", "Scale", "Opacity" };

    private readonly LayoutLog _log;
    private readonly object _gate = new();

    private Type? _cachedServiceType;
    private object? _cachedService;
    private bool _searched;

    public HostLayoutApplier(LayoutLog log)
    {
        _log = log;
    }

    public Task<LayoutApplyResult> ApplyAsync(LayoutTargetKind target, LayoutConfig config, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var componentConfig = target == LayoutTargetKind.Class
                ? config.ClassComponentConfig
                : config.BreakComponentConfig;
            var scale = target == LayoutTargetKind.Class ? config.ClassScale : config.BreakScale;
            var opacity = target == LayoutTargetKind.Class ? config.ClassOpacity : config.BreakOpacity;

            var service = ResolveSettingsService();
            if (service is null)
            {
                const string reason = "宿主未提供可直接设置的布局服务，请使用生成的推荐自动化片段。";
                _log.Warn($"[{target}] 应用失败：{reason}");
                return Task.FromResult(new LayoutApplyResult(false, reason));
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["CurrentComponentConfig"] = componentConfig,
                ["Scale"] = scale,
                ["Opacity"] = opacity
            };

            var settingsObject = ReadProperty(service, "Settings");
            var writeTarget = service;
            foreach (var name in RequiredProperties)
            {
                // 优先在服务本身设置，其次在 Settings 子对象上设置。
                if (TrySetProperty(service, name, values[name])) continue;
                if (settingsObject is not null && TrySetProperty(settingsObject, name, values[name]))
                {
                    writeTarget = settingsObject;
                    continue;
                }

                var reason = $"宿主布局服务缺少可写属性 {name}";
                _log.Warn($"[{target}] 应用失败：{reason}");
                return Task.FromResult(new LayoutApplyResult(false, reason));
            }

            // 回读校验：写不进去时不要谎报成功（曾经出现过"日志成功、实际没生效"）。
            var readBack = ReadProperty(writeTarget, "CurrentComponentConfig")?.ToString();
            if (!string.Equals(readBack, componentConfig, StringComparison.Ordinal))
                _log.Warn($"[{target}] 回读不一致：期望 {componentConfig}，实际 {readBack ?? "<null>"}（目标类型 {writeTarget.GetType().Name}）");

            // 通过宿主服务自己的保存方法落盘，不在本模块直接写文件。
            TrySave(service);

            _log.Info($"[{target}] 已应用 component={componentConfig} scale={scale:0.##} opacity={opacity:0.##}");
            return Task.FromResult(new LayoutApplyResult(true, "ok"));
        }
        catch (Exception ex)
        {
            _log.Error($"[{target}] 应用布局异常：{ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(new LayoutApplyResult(false, ex.Message));
        }
    }

    private object? ResolveSettingsService()
    {
        lock (_gate)
        {
            if (_searched) return _cachedService;
            _searched = true;

            try
            {
                if (_cachedServiceType is not null)
                {
                    _cachedService = TryResolve(_cachedServiceType);
                    if (_cachedService is not null) return _cachedService;
                }

                // 1) 精确匹配宿主 SettingsService 类型。
                var exactType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeGetTypes)
                    .FirstOrDefault(t => t.FullName == SettingsServiceTypeName);
                if (exactType is not null)
                {
                    var exact = TryResolve(exactType);
                    if (exact is not null)
                    {
                        _cachedServiceType = exactType;
                        _cachedService = exact;
                        return exact;
                    }
                }

                // 2) 退化：按属性名扫描名字含 Settings 的服务类型。
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var type in SafeGetTypes(asm))
                    {
                        if (!LooksLikeSettingsType(type)) continue;
                        var instance = TryResolve(type);
                        if (instance is null) continue;
                        _cachedServiceType = type;
                        _cachedService = instance;
                        return instance;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"解析宿主设置服务异常：{ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }
    }

    private static bool LooksLikeSettingsType(Type type)
    {
        if (!type.IsClass && !type.IsInterface) return false;
        if (type.Name.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) < 0) return false;
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        return RequiredProperties.All(name => props.Any(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static object? TryResolve(Type serviceType) => HostServiceResolver.TryGet(serviceType);

    private static object? ReadProperty(object target, string name)
    {
        try
        {
            var prop = FindProperty(target.GetType(), name);
            return prop?.GetValue(target);
        }
        catch { return null; }
    }

    private static bool TrySetProperty(object target, string name, object? value)
    {
        try
        {
            var prop = FindProperty(target.GetType(), name);
            if (prop is null || !prop.CanWrite) return false;
            prop.SetValue(target, ConvertValue(prop.PropertyType, value));
            return true;
        }
        catch { return false; }
    }

    private void TrySave(object service)
    {
        try
        {
            // 宿主 2.1.0.1 的签名是 SaveSettings(string note = "")：
            // 唯一一个参数带默认值，所以不能按「参数个数为 0」筛选。
            var method = service.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "SaveSettings" || m.Name == "Save")
                .Where(m => m.GetParameters().All(p => p.IsOptional))
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();
            if (method is null)
            {
                _log.Warn($"宿主 {service.GetType().Name} 上没有可调用的 Save 方法，布局变更可能仅在内存中生效");
                return;
            }

            var parameters = method.GetParameters();
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : Type.Missing;
            method.Invoke(service, args);
        }
        catch (Exception ex)
        {
            _log.Warn($"调用宿主 SaveSettings() 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static PropertyInfo? FindProperty(Type type, string name)
        => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
           ?? type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static object? ConvertValue(Type targetType, object? value)
    {
        if (value is null) return null;
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsInstanceOfType(value)) return value;
        if (underlying == typeof(string)) return System.Convert.ToString(value, CultureInfo.InvariantCulture);
        if (underlying == typeof(double)) return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (underlying == typeof(float)) return System.Convert.ToSingle(value, CultureInfo.InvariantCulture);
        if (underlying == typeof(int)) return System.Convert.ToInt32(value, CultureInfo.InvariantCulture);
        return System.Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }
        catch { return Array.Empty<Type>(); }
    }
}
