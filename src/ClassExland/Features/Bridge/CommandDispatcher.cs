using ClassExland.Contracts;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Bridge;

/// <summary>
/// CLI 与远程指令队列共用的命令执行入口。
/// 只读优先；写操作需 Apply(--yes)，高危操作需 --confirm yes；默认 DryRun 预览差异。
/// </summary>
public sealed partial class CommandDispatcher : ICommandDispatcher
{
    private readonly IServiceProvider _sp;
    private readonly IPluginPaths _paths;
    private readonly FileStore _store;
    private readonly CommandAudit _audit;
    private readonly HostIpcProbe _ipc;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(
        IServiceProvider sp,
        IPluginPaths paths,
        FileStore store,
        CommandAudit audit,
        HostIpcProbe ipc,
        ILogger<CommandDispatcher> logger)
    {
        _sp = sp;
        _paths = paths;
        _store = store;
        _audit = audit;
        _ipc = ipc;
        _logger = logger;
    }

    /// <summary>
    /// 惰性解析 LoopbackWebHost，避免与 CommandDispatcher 形成构造期循环依赖
    /// （LoopbackWebHost 构造依赖 CommandDispatcher，二者均为 Singleton）。
    /// </summary>
    private LoopbackWebHost? WebHost => _sp.GetService(typeof(LoopbackWebHost)) as LoopbackWebHost;

    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct = default)
    {
        CommandResult? result = null;
        Exception? error = null;
        try
        {
            result = await ExecuteCoreAsync(request, ct).ConfigureAwait(false);
        }
        catch (CommandAbortedException ex)
        {
            result = Fail(ex.ExitCode, ex.Message);
        }
        catch (Exception ex)
        {
            error = ex;
            _logger.LogError(ex, "命令 {Command} 执行失败", request.Command);
            result = Fail(ExitCodes.Validation, "执行失败：" + ex.Message);
        }

        try { await _audit.WriteAsync(request, result, error).ConfigureAwait(false); }
        catch { /* 审计失败不影响命令结果 */ }
        return result;
    }

    private async Task<CommandResult> ExecuteCoreAsync(CommandRequest r, CancellationToken ct)
    {
        var command = (r.Command ?? "").Trim().ToLowerInvariant();
        if (command.Length == 0) return Fail(ExitCodes.Validation, "缺少命令名");

        if (CommandNames.IsWrite(command) && !r.Apply && !r.DryRun)
            return Fail(ExitCodes.Security,
                $"写操作 {command} 必须显式 --yes 执行，或 --dry-run 预览差异。未做任何更改。");

        if (CommandNames.IsDangerous(command) && r.Apply &&
            !string.Equals(GetArg(r, "confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            return Fail(ExitCodes.Security,
                $"高危操作 {command} 需 --confirm yes 二次确认。未做任何更改。");

        return await DispatchAsync(command, r, ct).ConfigureAwait(false);
    }

    private Task<CommandResult> DispatchAsync(string command, CommandRequest r, CancellationToken ct)
        => command switch
        {
            CommandNames.Status => QueryStatusAsync(r, ct),
            CommandNames.Version => QueryVersionAsync(r, ct),
            CommandNames.Bell.List => QueryBellListAsync(r, ct),
            CommandNames.Bell.Preview => QueryBellPreviewAsync(r, ct),
            CommandNames.Duty.Show => QueryDutyShowAsync(r, ct),
            CommandNames.Activity.List => QueryActivityListAsync(r, ct),
            CommandNames.Layout.Current => QueryLayoutCurrentAsync(r, ct),
            CommandNames.Config.Get => QueryConfigGetAsync(r, ct),
            CommandNames.Config.Diff => QueryConfigDiffAsync(r, ct),
            CommandNames.Config.Doctor => QueryConfigDoctorAsync(r, ct),
            CommandNames.Backup.List => QueryBackupListAsync(r, ct),
            CommandNames.Audit.List => QueryAuditListAsync(r, ct),
            CommandNames.Remote.Status => QueryRemoteStatusAsync(r, ct),
            CommandNames.Profile.List => QueryProfileListAsync(r, ct),
            CommandNames.Profile.Current => QueryProfileCurrentAsync(r, ct),

            CommandNames.Bell.Set => WriteBellSetAsync(r, ct),
            CommandNames.Bell.Import => WriteBellImportAsync(r, ct),
            CommandNames.Bell.Export => WriteBellExportAsync(r, ct),
            CommandNames.Duty.Edit => WriteDutyEditAsync(r, ct),
            CommandNames.Duty.Import => WriteDutyImportAsync(r, ct),
            CommandNames.Duty.Export => WriteDutyExportAsync(r, ct),
            CommandNames.Duty.Rotate => WriteDutyRotateAsync(r, ct),
            CommandNames.Duty.Complete => WriteDutyCompleteAsync(r, ct),
            CommandNames.Activity.Add => WriteActivityAddAsync(r, ct),
            CommandNames.Activity.Edit => WriteActivityEditAsync(r, ct),
            CommandNames.Activity.Test => WriteActivityTestAsync(r, ct),
            CommandNames.Layout.Switch => WriteLayoutSwitchAsync(r, ct),
            CommandNames.Config.Set => WriteConfigSetAsync(r, ct),
            CommandNames.Remote.Sync => WriteRemoteSyncAsync(r, ct),
            CommandNames.Remote.Poll => WriteRemotePollAsync(r, ct),
            CommandNames.Remote.Enable => WriteRemoteToggleAsync(r, ct, true),
            CommandNames.Remote.Disable => WriteRemoteToggleAsync(r, ct, false),
            CommandNames.Remote.Server => WriteRemoteServerAsync(r, ct),
            CommandNames.Backup.Create => WriteBackupCreateAsync(r, ct),
            CommandNames.Backup.Restore => WriteBackupRestoreAsync(r, ct),
            CommandNames.Audit.Export => WriteAuditExportAsync(r, ct),
            CommandNames.Migrate => WriteMigrateAsync(r, ct),
            CommandNames.Web.Start => WriteWebStartAsync(r, ct),
            CommandNames.Web.Stop => WriteWebStopAsync(r, ct),
            CommandNames.ClassState.Set => WriteClassSetAsync(r, ct),
            CommandNames.Profile.Switch => WriteProfileSwitchAsync(r, ct),

            _ => Task.FromResult(Fail(ExitCodes.Validation, $"未知命令：{command}"))
        };

    // ---------- 结果构造 ----------

    private static CommandResult Ok(object? data, string message = "OK")
        => new(true, message, new ResultEnvelope(ExitCodes.Ok, data));

    private static CommandResult Fail(int exitCode, string message, object? data = null)
        => new(false, message, new ResultEnvelope(exitCode, data));

    private static CommandResult Preview(object preview, string message = "预览（未写入，需 --yes 生效）")
        => new(true, message, new ResultEnvelope(ExitCodes.Ok, preview, preview));

    // ---------- 参数与工具 ----------

    private static string? GetArg(CommandRequest r, string name)
    {
        if (r.Args.TryGetValue(name, out var v)) return v;
        if (r.Args.TryGetValue("--" + name, out v)) return v;
        return null;
    }

    private static int GetArgInt(CommandRequest r, string name, int fallback = 0)
        => int.TryParse(GetArg(r, name), out var n) ? n : fallback;

    private static bool GetArgBool(CommandRequest r, string name, bool fallback = false)
    {
        var v = GetArg(r, name);
        if (v is null) return fallback;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private T? TryService<T>() where T : class
    {
        try { return _sp.GetService(typeof(T)) as T; } catch { return null; }
    }

    private object? TryResolveInterface(string fullName)
    {
        try
        {
            var type = Type.GetType(fullName);
            if (type is null)
            {
                var simple = fullName[(fullName.LastIndexOf('.') + 1)..];
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        type = asm.GetTypes().FirstOrDefault(t => t.FullName == fullName || t.Name == simple);
                        if (type is not null) break;
                    }
                    catch (System.Reflection.ReflectionTypeLoadException ex)
                    {
                        type = ex.Types.FirstOrDefault(t => t is not null && (t.FullName == fullName || t.Name == simple));
                        if (type is not null) break;
                    }
                    catch { /* 跳过无法枚举的程序集 */ }
                }
            }
            return type is null ? null : _sp.GetService(type);
        }
        catch { return null; }
    }

    private static DateOnly? ParseDate(string? s)
        => DateOnly.TryParse(s, out var d) ? d : null;

    private IReadOnlyList<string> ConfigFileNames()
        => new[]
        {
            ConfigFiles.Bell, ConfigFiles.Duty, ConfigFiles.Activity, ConfigFiles.Layout,
            ConfigFiles.Remote, ConfigFiles.Bridge, ConfigFiles.CoreSnapshot
        };
}
