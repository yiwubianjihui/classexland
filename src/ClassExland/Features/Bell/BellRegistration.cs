using ClassExland.Contracts;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassExland.Features.Bell;

/// <summary>
/// BellModule 的注册入口，由 Plugin.Initialize 调用 services.AddBell()。
/// </summary>
public static class BellRegistration
{
    public static IServiceCollection AddBell(this IServiceCollection services)
    {
        services.AddSingleton<BellSettings>();
        services.AddSingleton<BellProfileWriter>();
        services.AddSingleton<BellSoundService>();

        // 同一实例同时作为 IBellTimeline 与其他模块的 IHostedService。
        services.AddSingleton<BellTimelineService>();
        services.AddSingleton<IBellTimeline>(sp => sp.GetRequiredService<BellTimelineService>());
        services.AddSingleton<IBellStateController>(sp => sp.GetRequiredService<BellTimelineService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BellTimelineService>());

        // Harmony 覆盖在 BellTimelineService 之后启动，确保能读到虚拟状态。
        services.AddSingleton<HarmonyPatcher>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<HarmonyPatcher>());

        // AddNotificationProvider<T> 只登记为 IHostedService，不注册 T 本身。
        services.AddNotificationProvider<BellNotificationProvider>();
        services.AddSettingsPage<BellSettingsPage>();

        return services;
    }
}
