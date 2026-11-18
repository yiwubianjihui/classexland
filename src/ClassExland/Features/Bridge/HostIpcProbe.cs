using System.Collections;
using System.Reflection;

namespace ClassExland.Features.Bridge;

/// <summary>
/// 以反射方式读取 ClassIsland 官方 IPC（ClassIsland.Shared.IPC.dll）暴露的公共服务。
/// 这样桥在插件进程内复用宿主已加载的 IPC，CLI 进程无需引用该程序集。
/// 服务或属性不存在时返回 null，不抛异常。
/// </summary>
public sealed class HostIpcProbe
{
    private readonly IServiceProvider _sp;

    public HostIpcProbe(IServiceProvider sp) => _sp = sp;

    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        ProbeInterface(
            "ClassIsland.Shared.IPC.IPublicLessonsService",
            new[] { "CurrentState", "CurrentTimeLayoutItem", "NextClassTimeLayoutItem", "IsLessonConfirmed", "OnClassLeftTime", "IsTimerRunning", "GetClassPlanByDate" },
            result);

        ProbeInterface(
            "ClassIsland.Shared.IPC.IPublicProfileService",
            new[] { "Profile", "CurrentProfilePath" },
            result);

        return result;
    }

    public object? GetProperty(string interfaceShortName, string propertyName)
    {
        var type = FindType(interfaceShortName);
        if (type is null) return null;
        var svc = _sp.GetService(type);
        if (svc is null) return null;
        var pi = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (pi is null) return null;
        try { return Simplify(pi.GetValue(svc)); } catch { return null; }
    }

    private void ProbeInterface(string typeFullName, IEnumerable<string> members, Dictionary<string, object?> result)
    {
        try
        {
            var type = FindType(typeFullName);
            if (type is null)
            {
                result["ipc." + Short(typeFullName) + ".available"] = false;
                return;
            }
            var svc = _sp.GetService(type);
            if (svc is null)
            {
                result["ipc." + Short(typeFullName) + ".available"] = false;
                return;
            }
            result["ipc." + Short(typeFullName) + ".available"] = true;

            foreach (var member in members)
            {
                var pi = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
                if (pi is null) continue;
                try { result[member] = Simplify(pi.GetValue(svc)); }
                catch (Exception ex) { result[member] = "<error: " + ex.GetType().Name + ">"; }
            }
        }
        catch (Exception ex)
        {
            result["ipc." + Short(typeFullName) + ".available"] = false;
            result["ipc." + Short(typeFullName) + ".error"] = ex.Message;
        }
    }

    private static string Short(string fullName)
    {
        var i = fullName.LastIndexOf('.');
        return i >= 0 ? fullName[(i + 1)..] : fullName;
    }

    private Type? FindType(string fullName)
    {
        var type = Type.GetType(fullName);
        if (type is not null) return type;
        var simple = fullName[(fullName.LastIndexOf('.') + 1)..];
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                type = asm.GetTypes().FirstOrDefault(t => t.FullName == fullName || t.Name == simple);
                if (type is not null) return type;
            }
            catch (ReflectionTypeLoadException ex)
            {
                type = ex.Types.FirstOrDefault(t => t is not null && (t.FullName == fullName || t.Name == simple));
                if (type is not null) return type;
            }
            catch { /* 跳过无法枚举的程序集 */ }
        }
        return null;
    }

    private static object? Simplify(object? value, int depth = 0)
    {
        if (value is null) return null;
        if (depth > 3) return value.ToString();
        var t = value.GetType();
        if (t.IsPrimitive || t.IsEnum || value is string or decimal or DateTime or DateTimeOffset or Guid or TimeSpan)
            return value is Enum e ? e.ToString() : value;

        if (value is IDictionary dict)
        {
            var d = new Dictionary<string, object?>();
            foreach (DictionaryEntry kv in dict)
                d[kv.Key?.ToString() ?? ""] = Simplify(kv.Value, depth + 1);
            return d;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                if (list.Count >= 50) { list.Add("..."); break; }
                list.Add(Simplify(item, depth + 1));
            }
            return list;
        }

        var obj = new Dictionary<string, object?>();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Take(40))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            try { obj[p.Name] = Simplify(p.GetValue(value), depth + 1); }
            catch { obj[p.Name] = "<error>"; }
        }
        return obj.Count > 0 ? obj : value.ToString();
    }
}
