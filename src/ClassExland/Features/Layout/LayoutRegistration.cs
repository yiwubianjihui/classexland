using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassExland.Features.Layout;

public static class LayoutRegistration
{
    /// <summary>注册界面切换模块。必须调用 AddBell()，因为本模块依赖 IBellTimeline。</summary>
    public static IServiceCollection AddLayout(this IServiceCollection services)
    {
        services.AddSingleton<LayoutLog>();
        services.AddSingleton<LayoutConfigStore>();

        // IAppHost 是宿主静态入口，不需要也不可以作为 DI 服务注入；
        // 由 HostServiceResolver 反射兼容静态/实例两种形态，缺失时在 Apply 内安全降级。
        services.AddSingleton<ILayoutApplier, HostLayoutApplier>();

        // 同一实例既作为 HostedService 运行，也作为 ILayoutService 供 Bridge 反射调用。
        services.AddSingleton<LayoutModule>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<LayoutModule>());
        services.AddSingleton<ILayoutService>(sp => sp.GetRequiredService<LayoutModule>());

        // 面向普通用户的设置页：上课/下课布局、缩放、不透明度、延迟与拖堂保护。
        services.AddSettingsPage<LayoutSettingsPage>();
        return services;
    }
}

/// <summary>宿主缺少 IAppHost 时的安全空实现，保证 DI 链完整且插件不被禁用。</summary>
internal sealed class NoOpLayoutApplier : ILayoutApplier
{
    private readonly LayoutLog _log;
    private readonly string _reason;

    public NoOpLayoutApplier(LayoutLog log, string reason)
    {
        _log = log;
        _reason = reason;
    }

    public Task<LayoutApplyResult> ApplyAsync(LayoutTargetKind target, LayoutConfig config, CancellationToken ct = default)
    {
        _log.Warn($"[{target}] 跳过应用：{_reason}");
        return Task.FromResult(new LayoutApplyResult(false, _reason));
    }

    public IReadOnlyList<string> GetComponentConfigNames() => Array.Empty<string>();
}
