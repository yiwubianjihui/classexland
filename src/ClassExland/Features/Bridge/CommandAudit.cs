using System.Text.Json;
using ClassExland.Contracts;

namespace ClassExland.Features.Bridge;

/// <summary>所有命令（含只读）写入 IPluginPaths.AuditLogPath，JSON Lines 格式，参数脱敏。</summary>
public sealed class CommandAudit
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "password", "secret", "credential", "authorization", "apikey", "api-key", "key"
    };

    private readonly IPluginPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CommandAudit(IPluginPaths paths) => _paths = paths;

    public async Task WriteAsync(CommandRequest request, CommandResult? result, Exception? error)
    {
        var exitCode = ExtractExitCode(result?.Data);
        var record = new
        {
            at = DateTimeOffset.UtcNow,
            command = request.Command,
            args = Redact(request.Args),
            source = request.Source,
            apply = request.Apply,
            dryRun = request.DryRun,
            ok = result?.Ok ?? false,
            exitCode,
            message = result?.Message,
            error = error?.Message
        };
        var line = JsonSerializer.Serialize(record, BridgeProtocol.Json);
        await _gate.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(_paths.AuditLogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(_paths.AuditLogPath, line + Environment.NewLine);
        }
        catch
        {
            // 审计失败不影响命令结果
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<JsonDocument> ReadRecent(int take = 200)
    {
        var list = new List<JsonDocument>();
        try
        {
            if (!File.Exists(_paths.AuditLogPath)) return list;
            var lines = File.ReadAllLines(_paths.AuditLogPath);
            foreach (var line in lines.AsEnumerable().Reverse().Take(take))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { list.Add(JsonDocument.Parse(line)); } catch { /* skip bad line */ }
            }
        }
        catch { /* ignore */ }
        return list;
    }

    public static int ExtractExitCode(object? data)
    {
        if (data is ResultEnvelope env) return env.ExitCode;
        if (data is JsonElement je && je.ValueKind == JsonValueKind.Object && je.TryGetProperty("exitCode", out var v))
            return v.TryGetInt32(out var n) ? n : 0;
        return 0;
    }

    private static Dictionary<string, string?> Redact(IReadOnlyDictionary<string, string?> args)
    {
        var result = new Dictionary<string, string?>();
        foreach (var kv in args)
            result[kv.Key] = SensitiveKeys.Contains(kv.Key) ? "***" : kv.Value;
        return result;
    }
}
