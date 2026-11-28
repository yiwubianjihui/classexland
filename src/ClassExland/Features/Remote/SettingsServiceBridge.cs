using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace ClassExland.Features.Remote;

/// <summary>
/// 远程应用 Settings.json 时优先走宿主官方 services：
/// 反射定位 ClassIsland.Services.SettingsService，设置白名单内的键后调用 SaveSettings()。
/// 只有无法走服务时才回退到「备份→校验→原子写文件」，并由 CoreConfigWriter 记特殊审计事件。
/// </summary>
public static class SettingsServiceBridge
{
    private const string SettingsServiceTypeName = "ClassIsland.Services.SettingsService";

    public static bool TryApply(string remoteSettingsJson, RemoteAuditLogger audit, out string message)
    {
        message = "";
        try
        {
            using var doc = JsonDocument.Parse(remoteSettingsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                message = "远程 Settings 不是对象";
                return false;
            }

            var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(t => t.FullName == SettingsServiceTypeName);
            if (serviceType is null)
            {
                message = "未找到 ClassIsland.Services.SettingsService";
                return false;
            }

            var service = HostServiceResolver.TryGet(serviceType);
            if (service is null)
            {
                message = "宿主未注册 SettingsService";
                return false;
            }

            var settings = ReadProperty(service, "Settings");
            var changed = 0;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!CoreConfigPolicy.AllowedSettingKeys.Contains(prop.Name, StringComparer.OrdinalIgnoreCase)) continue;

                var value = ConvertJson(prop.Value);
                if (TrySet(service, prop.Name, value) ||
                    (settings is not null && TrySet(settings, prop.Name, value)))
                {
                    changed++;
                }
                else
                {
                    // 有白名单键无法通过服务设置（如复杂对象 AttachedObjects），
                    // 必须回退到文件合并，避免该变更被静默丢弃。
                    message = $"白名单键 {prop.Name} 无法通过 SettingsService 设置，回退文件合并";
                    return false;
                }
            }

            if (changed == 0)
            {
                message = "SettingsService 无可应用的白名单键";
                return false;
            }

            var methods = service.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            // 宿主 2.1 的 SaveSettings 是 1 参数 SaveSettings(string note)（已由
            // tools/probe_host_metadata.py 只读核实）；老版本是 0 参数。两者都兼容。
            var save = methods.FirstOrDefault(m =>
                (m.Name == "SaveSettings" || m.Name == "Save") && m.GetParameters().Length == 0)
                ?? methods.FirstOrDefault(m =>
                    (m.Name == "SaveSettings" || m.Name == "Save") &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(string));
            if (save is null)
            {
                message = "SettingsService 缺少 SaveSettings()/SaveSettings(note)";
                return false;
            }
            if (save.GetParameters().Length == 0) save.Invoke(service, null);
            else save.Invoke(service, new object[] { "ClassExland 远程配置同步" });

            message = $"已通过 SettingsService 反射应用 {changed} 个键并调用 {save.Name}()";
            return true;
        }
        catch (Exception ex)
        {
            message = $"SettingsService 反射失败：{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static object? ConvertJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetDouble(out var d) ? d : element.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };

    private static object? ReadProperty(object target, string name)
    {
        try { return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target); }
        catch { return null; }
    }

    private static bool TrySet(object target, string name, object? value)
    {
        try
        {
            var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null || !prop.CanWrite) return false;
            var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (underlying.IsInstanceOfType(value)) prop.SetValue(target, value);
            else prop.SetValue(target, System.Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture));
            return true;
        }
        catch { return false; }
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).Cast<Type>().ToArray(); }
        catch { return Array.Empty<Type>(); }
    }
}
