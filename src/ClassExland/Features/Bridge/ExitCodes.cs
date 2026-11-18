namespace ClassExland.Features.Bridge;

/// <summary>稳定退出码，CLI 与远程指令队列共用同一语义。</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Validation = 1;
    public const int ConfigBusy = 2;
    public const int IpcUnavailable = 3;
    public const int Security = 4;
}

/// <summary>CommandResult.Data 内统一信封，CLI 据此映射进程退出码。</summary>
public sealed record ResultEnvelope(
    int ExitCode,
    object? Data = null,
    object? Preview = null,
    IReadOnlyList<string>? Notes = null);
