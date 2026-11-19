using System.Text.Json;

namespace ClassExland.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(CommandLine.BuildHelp());
            return CliExit.Validation;
        }

        var parsed = CommandLine.Parse(args);
        if (parsed.ShowHelp)
        {
            Console.Out.WriteLine(CommandLine.BuildHelp());
            return CliExit.Ok;
        }
        if (parsed.ShowVersion)
        {
            var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
            Console.Out.WriteLine($"cex {version} (ClassExland CLI)");
            return CliExit.Ok;
        }
        if (!string.IsNullOrWhiteSpace(parsed.Error))
        {
            WriteError(parsed.Error, CliExit.Validation, false);
            return CliExit.Validation;
        }

        var cmd = parsed.Command!;
        var options = cmd.Options;

        if (!string.IsNullOrWhiteSpace(options.BatchFile))
            return await RunBatchAsync(options.BatchFile, options).ConfigureAwait(false);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var outcome = await ExecuteOneAsync(cmd, ct: cts.Token).ConfigureAwait(false);
            WriteOutcome(outcome, options.Json);
            return outcome.ExitCode;
        }
        catch (BridgeConnectionException ex)
        {
            WriteError(ex.Message, ex.ExitCode, options.Json);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            WriteError("CLI 异常：" + ex.Message, CliExit.Validation, options.Json);
            return CliExit.Validation;
        }
    }

    private static async Task<CommandOutcome> ExecuteOneAsync(ParsedCommand cmd, BridgeClient? client = null, CancellationToken ct = default)
    {
        client ??= new BridgeClient(EndpointResolver.Resolve(cmd.Options.ConfigDir));
        var request = new CommandRequestDto
        {
            Command = cmd.Command,
            Args = cmd.Args,
            Source = "cli",
            Apply = cmd.Options.Apply,
            DryRun = cmd.Options.DryRun
        };
        return await client.ExecuteAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task<int> RunBatchAsync(string file, ParsedOptions inherited)
    {
        if (!File.Exists(file))
        {
            WriteError($"批量命令文件不存在：{file}", CliExit.Validation, inherited.Json);
            return CliExit.Validation;
        }

        BridgeClient client;
        try
        {
            client = new BridgeClient(EndpointResolver.Resolve(inherited.ConfigDir));
        }
        catch (BridgeConnectionException ex)
        {
            WriteError(ex.Message, ex.ExitCode, inherited.Json);
            return ex.ExitCode;
        }

        var outcomes = new List<CommandOutcome>();
        foreach (var raw in File.ReadAllLines(file))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim().StartsWith('#')) continue;
            var parsed = CommandLine.ParseBatchLine(raw, inherited);
            if (!parsed.IsOk || parsed.Command is null)
            {
                outcomes.Add(new CommandOutcome
                {
                    Command = raw.Trim(),
                    Ok = false,
                    Message = parsed.Error ?? "解析失败",
                    ExitCode = CliExit.Validation
                });
                continue;
            }

            try
            {
                var outcome = await ExecuteOneAsync(parsed.Command, client).ConfigureAwait(false);
                outcomes.Add(outcome);
            }
            catch (BridgeConnectionException ex)
            {
                outcomes.Add(new CommandOutcome
                {
                    Command = parsed.Command.Command,
                    Ok = false,
                    Message = ex.Message,
                    ExitCode = ex.ExitCode
                });
            }
        }

        if (inherited.Json)
        {
            var payload = outcomes.Select(o => new
            {
                command = o.Command,
                ok = o.Ok,
                exitCode = o.ExitCode,
                message = o.Message,
                data = o.Data
            });
            Console.Out.WriteLine(JsonSerializer.Serialize(payload, CliJson.Pretty));
        }
        else
        {
            foreach (var o in outcomes) WriteOutcome(o, false);
            Console.Out.WriteLine($"--- 批量完成：{outcomes.Count} 条，失败 {outcomes.Count(o => o.ExitCode != 0)} 条 ---");
        }

        return outcomes.FirstOrDefault(o => o.ExitCode != 0)?.ExitCode ?? CliExit.Ok;
    }

    private static void WriteOutcome(CommandOutcome o, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                command = o.Command,
                ok = o.Ok,
                exitCode = o.ExitCode,
                message = o.Message,
                data = o.Data
            }, CliJson.Pretty));
            return;
        }

        Console.Out.WriteLine($"[{o.Command}] {(o.Ok ? "OK" : "FAIL")} (exit {o.ExitCode}) {o.Message}");
        if (o.Data is JsonElement data && data.ValueKind == JsonValueKind.Object)
        {
            var payload = data.TryGetProperty("data", out var inner) ? inner : data;
            if (payload.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                Console.Out.WriteLine(JsonSerializer.Serialize(payload, CliJson.Pretty));
        }
    }

    private static void WriteError(string message, int exitCode, bool json)
    {
        if (json)
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, exitCode, message }, CliJson.Pretty));
        else
            Console.Error.WriteLine($"错误（exit {exitCode}）：{message}");
    }
}
