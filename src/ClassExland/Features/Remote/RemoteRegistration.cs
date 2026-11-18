using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassExland.Features.Remote;

public static class RemoteRegistration
{
    /// <summary>注册远程配置同步与多档案切换。Web 管理页由 Bridge 模块提供，不在此重复注册。</summary>
    public static IServiceCollection AddRemoteSync(this IServiceCollection services)
    {
        // 注意：ICommandDispatcher 由 Bridge（AddCommandBridge）注册。本模块不注册它的任何
        // 实现，改用 IServiceProvider 惰性解析，缺失时安全降级，避免注册顺序冲突。
        services.AddSingleton<RemoteConfigStore>();
        services.AddSingleton<RemoteStateStore>();
        services.AddSingleton<RemoteAuditLogger>();
        services.AddSingleton<SafeConfigWriter>();
        services.AddSingleton<CorePaths>();
        services.AddSingleton<CoreConfigWriter>();

        // Bridge 按接口全名反射探测，必须注册为 DI 服务。
        services.AddSingleton<IProfileService, ProfileSwitchService>();

        // 同一实例既作为 HostedService 运行，也作为 IRemoteSyncService 供 Bridge 反射调用。
        services.AddSingleton<RemoteSyncModule>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RemoteSyncModule>());
        services.AddSingleton<IRemoteSyncService>(sp => sp.GetRequiredService<RemoteSyncModule>());
        return services;
    }
}
