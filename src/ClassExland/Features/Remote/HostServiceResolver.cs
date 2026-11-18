using System.Reflection;
using ClassIsland.Shared;

namespace ClassExland.Features.Remote;

/// <summary>
/// 宿主服务解析。IAppHost 在宿主里是静态入口（IAppHost.TryGetService&lt;T&gt;()），
/// 但不同 SDK 版本可能提供静态或实例两种形态；这里用反射同时兼容，避免编译期
/// 因静态/实例写法分歧而失败，也避免依赖注入一个静态类型。
/// </summary>
internal static class HostServiceResolver
{
    public static T? TryGet<T>() where T : class => TryGet(typeof(T)) as T;

    public static object? TryGet(Type serviceType)
    {
        try
        {
            var hostType = typeof(IAppHost);
            var methods = hostType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            var method = methods.FirstOrDefault(m => m.Name == "TryGetService" && m.IsGenericMethodDefinition);
            if (method is null) return null;

            var generic = method.MakeGenericMethod(serviceType);
            var parameters = generic.GetParameters();
            var hasOut = parameters.Length == 1 && parameters[0].IsOut;
            var args = hasOut ? new object?[] { null } : Array.Empty<object?>();

            object? target = null;
            if (!generic.IsStatic)
            {
                var hostField = hostType.GetField("Host", BindingFlags.Public | BindingFlags.Static);
                target = hostField?.GetValue(null);
                if (target is null) return null;
            }

            var result = generic.Invoke(target, args);
            if (hasOut && result is bool ok) return ok ? args[0] : null;
            return result;
        }
        catch
        {
            return null;
        }
    }
}
