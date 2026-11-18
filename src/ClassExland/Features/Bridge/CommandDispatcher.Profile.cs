using System.Collections.Generic;
using System.Reflection;
using ClassExland.Contracts;

namespace ClassExland.Features.Bridge;

/// <summary>
/// 多班级档案切换命令。反射探测 sys-dev 暴露的
/// ClassExland.Features.Remote.IProfileService（CurrentProfile / ListProfiles / SwitchProfileAsync），
/// 未加载时返回明确错误码而非崩溃。
/// </summary>
public sealed partial class CommandDispatcher
{
    private const string ProfileServiceTypeName = "ClassExland.Features.Remote.IProfileService";

    private Task<CommandResult> QueryProfileCurrentAsync(CommandRequest r, CancellationToken ct)
    {
        var svc = TryResolveInterface(ProfileServiceTypeName);
        if (svc is null)
            return Task.FromResult(Fail(ExitCodes.IpcUnavailable,
                "档案服务不可用（RemoteModule 未提供 IProfileService）"));

        var current = TryGetPropertyValue(svc, "CurrentProfile") as string;
        var ipcPath = _ipc.GetProperty("IPublicProfileService", "CurrentProfilePath");
        return Task.FromResult(Ok(new { currentProfile = current, currentProfilePath = ipcPath }));
    }

    private async Task<CommandResult> QueryProfileListAsync(CommandRequest r, CancellationToken ct)
    {
        var svc = TryResolveInterface(ProfileServiceTypeName);
        if (svc is null)
            return Fail(ExitCodes.IpcUnavailable, "档案服务不可用（RemoteModule 未提供 IProfileService）");

        var result = await TryInvokeAsync(svc, "ListProfiles", Array.Empty<object?>(), ct).ConfigureAwait(false);
        var profiles = new List<string>();
        if (result is IEnumerable<string> enumerable)
            profiles.AddRange(enumerable);

        var current = TryGetPropertyValue(svc, "CurrentProfile") as string;
        return Ok(new { profiles, count = profiles.Count, currentProfile = current });
    }

    private async Task<CommandResult> WriteProfileSwitchAsync(CommandRequest r, CancellationToken ct)
    {
        // --to 为主参数；同时兼容全局 --profile / --name，方便脚本复用
        var name = GetArg(r, "to") ?? GetArg(r, "profile") ?? GetArg(r, "name");
        if (string.IsNullOrWhiteSpace(name))
            return Fail(ExitCodes.Validation, "缺少 --to <档案名>（例如 profile switch --to YBH）");

        var svc = TryResolveInterface(ProfileServiceTypeName);
        if (svc is null)
            return Fail(ExitCodes.IpcUnavailable, "档案服务不可用（RemoteModule 未提供 IProfileService）");

        var current = TryGetPropertyValue(svc, "CurrentProfile") as string;
        if (!r.Apply)
            return Preview(new { from = current, to = name, action = "switch class profile" });

        // 定稿签名为 SwitchProfileAsync(string) 单参数；TryInvokeAsync 也兼容带尾部 CancellationToken 的重载
        var result = await TryInvokeAsync(svc, "SwitchProfileAsync", new object?[] { name }, ct).ConfigureAwait(false);

        if (result is bool ok)
            return ok
                ? Ok(new { from = current, to = name, switched = true })
                : Fail(ExitCodes.Validation, $"切换档案失败：{name} 可能不存在或切换被拒绝");

        return Ok(new { from = current, to = name, switched = true });
    }

    private static object? TryGetPropertyValue(object service, string propertyName)
    {
        try
        {
            var prop = service.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            return prop?.GetValue(service);
        }
        catch { return null; }
    }
}
