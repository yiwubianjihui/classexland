using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassExland.Cli;

/// <summary>CLI 侧与插件桥 JSON 兼容的请求 DTO（不引用插件程序集）。</summary>
internal sealed class CommandRequestDto
{
    public string Command { get; set; } = "";
    public Dictionary<string, string?> Args { get; set; } = new();
    public string Source { get; set; } = "cli";
    public bool Apply { get; set; }
    public bool DryRun { get; set; }
}

internal sealed class BridgeResponseDto
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public JsonElement? Data { get; set; }
}

internal static class CliJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}

internal static class CliExit
{
    public const int Ok = 0;
    public const int Validation = 1;
    public const int ConfigBusy = 2;
    public const int IpcUnavailable = 3;
    public const int Security = 4;
}

internal sealed class CommandOutcome
{
    public required string Command { get; init; }
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public JsonElement? Data { get; init; }
    public int ExitCode { get; init; }

    public static CommandOutcome From(string command, BridgeResponseDto r)
        => new()
        {
            Command = command,
            Ok = r.Ok,
            Message = r.Message,
            Data = r.Data,
            ExitCode = ExtractExitCode(r.Data, r.Ok)
        };

    public static int ExtractExitCode(JsonElement? data, bool ok)
    {
        if (data is { ValueKind: JsonValueKind.Object } je && je.TryGetProperty("exitCode", out var v))
            return v.TryGetInt32(out var n) ? n : 0;
        return ok ? CliExit.Ok : CliExit.Validation;
    }
}
