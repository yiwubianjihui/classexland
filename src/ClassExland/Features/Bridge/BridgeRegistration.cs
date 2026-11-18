using ClassExland.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace ClassExland.Features.Bridge;

public static class BridgeRegistration
{
    /// <summary>
    /// 注册 CLI / 远程指令队列共用的命令桥：端点、文件存储、审计、宿主 IPC 探针、
    /// 本机命名管道服务器、Loopback 管理页。
    /// </summary>
    public static IServiceCollection AddCommandBridge(this IServiceCollection services)
    {
        services.AddSingleton<BridgeEndpoint>();
        services.AddSingleton<FileStore>();
        services.AddSingleton<CommandAudit>();
        services.AddSingleton<HostIpcProbe>();
        services.AddSingleton<CommandDispatcher>();
        services.AddSingleton<ICommandDispatcher>(sp => sp.GetRequiredService<CommandDispatcher>());

        services.AddHostedService<BridgeServer>();

        services.AddSingleton<LoopbackWebHost>();
        services.AddHostedService(sp => sp.GetRequiredService<LoopbackWebHost>());

        return services;
    }
}
