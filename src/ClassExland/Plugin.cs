using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassExland.Contracts;
using ClassExland.Features.Bell;
using ClassExland.Features.Bridge;
using ClassExland.Features.Duty;
using ClassExland.Features.Layout;
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

        services.AddBell();
        services.AddDuty();
        services.AddLayout();
        services.AddRemoteSync();
        services.AddCommandBridge();
    }
}
