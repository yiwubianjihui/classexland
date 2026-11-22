using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace ClassExland.Features.Lucky;

/// <summary>
/// LuckyPicker 联动注册。
///
/// 刻意不提供设置页：与远程控制同理，避免课堂上被误点。
/// 配置放在插件配置目录的 lucky.json，默认关闭（Enabled=false），
/// 由管理员打开后才会读 LuckyPicker 的历史文件。
/// </summary>
public static class LuckyRegistration
{
    public static IServiceCollection AddLucky(this IServiceCollection services)
    {
        services.AddSingleton<LuckyConfigStore>();

        // AddNotificationProvider<T> 内部只做 AddHostedService<T>()，不注册 T 本身
        // （与 Bell/Duty 两个模块的既有约定一致，见 BellRegistration 的注释）。
        // 千万不要再额外 AddSingleton<IHostedService>(同一实例)——那样 DI 会造出第二个对象，
        // PostMainTimerTicked 被订阅两次，同一条抽选会被广播两遍。
        services.AddNotificationProvider<LuckyBroadcastService>();

        return services;
    }
}
