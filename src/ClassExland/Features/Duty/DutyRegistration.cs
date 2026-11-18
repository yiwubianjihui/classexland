using ClassIsland.Core.Extensions.Registry;
using ClassExland.Contracts;
using ClassExland.Features.Activity;
using ClassExland.Features.Duty.Controls;
using ClassExland.Features.Duty.Migration;
using ClassExland.Features.Duty.Models;
using ClassExland.Features.Duty.Reminders;
using ClassExland.Features.Duty.Services;
using ClassExland.Features.Duty.Views.SettingsPages;
using Microsoft.Extensions.DependencyInjection;

namespace ClassExland.Features.Duty;

/// <summary>
/// 值日模块注册入口。Plugin.cs 通过 services.AddDuty() 调用。
/// </summary>
public static class DutyRegistration
{
    public static IServiceCollection AddDuty(this IServiceCollection services)
    {
        services.AddSingleton(sp => DutyService.Load(sp.GetRequiredService<IPluginPaths>()));
        services.AddSingleton(sp => ActivityService.Load(sp.GetRequiredService<IPluginPaths>()));

        services.AddSingleton<DutyService>();
        services.AddSingleton<IDutyQuery>(sp => sp.GetRequiredService<DutyService>());
        services.AddSingleton<ActivityService>();
        services.AddSingleton<MigrationService>();
        services.AddSingleton<IDutyMigrationService>(sp => sp.GetRequiredService<MigrationService>());
        services.AddSingleton<IActivityNotifier, ActivityNotifier>();

        services.AddSettingsPage<DutySettingsPage>();
        services.AddComponent<DutyComponent, DutyComponentSettings>();
        services.AddNotificationProvider<DutyReminderProvider>();

        return services;
    }
}
