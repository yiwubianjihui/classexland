using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassExland.Contracts;
using ClassExland.Features.Bell;
using ClassExland.Features.Bridge;
using ClassExland.Features.Duty;
using ClassExland.Features.Layout;
using ClassExland.Features.Lucky;
using ClassExland.Features.Remote;
using ClassExland.Infra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassExland;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        var paths = new PluginPaths(PluginConfigFolder);
        services.AddSingleton<IPluginPaths>(paths);

        // 配置持久化保障：必须在任何配置类被解析之前跑，
        // 这样即使上一轮运行把某个 json 写坏了（空文件/非法 JSON），
        // 也是先从快照补回来，再让各模块去读——而不是让模块拿默认值覆盖用户配置。
        var guard = new ConfigSnapshotGuard(paths);
        guard.EnsureHealthy();
        services.AddSingleton(guard);

        services.AddBell();
        services.AddDuty();
        services.AddLayout();
        services.AddRemoteSync();
        services.AddLucky();
        services.AddCommandBridge();
    }
}
