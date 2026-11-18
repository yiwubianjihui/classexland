using ClassIsland.Shared;
using ClassExland.Features.Duty.Reminders;
using Microsoft.Extensions.Hosting;

namespace ClassExland.Features.Activity;

/// <summary>
/// 活动测试提醒入口，供 Bridge 通过 IAppHost 反射调用（弱依赖契约，勿改名/命名空间）。
/// 返回 Task（内部返回 Task&lt;bool&gt;），确保反射取得非 null 的 Result。
/// </summary>
public interface IActivityNotifier
{
    Task TestAsync(string id);

    Task TestAsync(string id, CancellationToken ct);
}

/// <summary>
/// 桥接到值日模块的提醒提供方；AddNotificationProvider 只注册为 IHostedService，
/// 因此这里用 IAppHost.TryGetService 取出，而不是直接构造注入。
/// </summary>
public sealed class ActivityNotifier : IActivityNotifier
{
    public Task TestAsync(string id)
    {
        try
        {
            var provider = IAppHost.TryGetService<IEnumerable<IHostedService>>()
                ?.OfType<DutyReminderProvider>()
                .FirstOrDefault();
            provider?.ShowActivityReminder(string.IsNullOrWhiteSpace(id) || id == "all" ? null : id);
        }
        catch
        {
            // 事件/反射调用异常不应导致插件被禁用。
        }
        return Task.FromResult(true);
    }

    public Task TestAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return TestAsync(id);
    }
}
