namespace ClassExland.Cli;

internal sealed class ParsedOptions
{
    public bool Json { get; set; }
    public bool Apply { get; set; }
    public bool DryRun { get; set; } = true;
    public string? Profile { get; set; }
    public string? ConfigDir { get; set; }
    public string? BatchFile { get; set; }
}

internal sealed class ParsedCommand
{
    public required string Command { get; init; }
    public Dictionary<string, string?> Args { get; init; } = new();
    public ParsedOptions Options { get; init; } = new();
    public string Display => Command;
}

internal sealed class ParseResult
{
    public ParsedCommand? Command { get; init; }
    public string? Error { get; init; }
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }
    public bool IsOk => Command is not null;
}

internal static class CommandCatalog
{
    public static readonly string[] Commands =
    {
        "status", "version", "migrate",
        "bell.list", "bell.set", "bell.import", "bell.export", "bell.preview",
        "duty.show", "duty.edit", "duty.import", "duty.export", "duty.rotate", "duty.complete",
        "activity.list", "activity.add", "activity.edit", "activity.test",
        "layout.switch", "layout.current",
        "config.get", "config.set", "config.diff", "config.doctor",
        "remote.status", "remote.sync", "remote.poll", "remote.upload", "remote.enable", "remote.disable", "remote.server",
        "backup.list", "backup.create", "backup.restore",
        "audit.list", "audit.export",
        "web.start", "web.stop", "web.status",
        "class.set",
        "lucky.status", "lucky.announce", "lucky.push",
        "profile.list", "profile.switch", "profile.current"
    };

    private static readonly HashSet<string> Set = new(Commands, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string command) => Set.Contains(command);

    /// <summary>web.status 复用 remote.status（其 Data 含 web 状态）。</summary>
    public static string Normalize(string command)
        => command.Equals("web.status", StringComparison.OrdinalIgnoreCase) ? "remote.status" : command;
}

internal static class CommandLine
{
    public static ParseResult Parse(string[] args)
    {
        var opts = new ParsedOptions();
        var positionals = new List<string>();
        var cmdArgs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = a.IndexOf('=');
                var key = eq >= 0 ? a[..eq] : a;
                string? inline = eq >= 0 ? a[(eq + 1)..] : null;

                switch (key.ToLowerInvariant())
                {
                    case "--json": opts.Json = true; break;
                    case "--yes": opts.Apply = true; break;
                    case "--dry-run": opts.DryRun = true; break;
                    case "--help": return new ParseResult { ShowHelp = true };
                    case "--version": return new ParseResult { ShowVersion = true };
                    case "--profile": opts.Profile = TakeValue(args, ref i, inline); break;
                    case "--config-dir": opts.ConfigDir = TakeValue(args, ref i, inline); break;
                    case "--batch": opts.BatchFile = TakeValue(args, ref i, inline); break;
                    case "--confirm": cmdArgs["confirm"] = inline ?? "yes"; break;
                    default:
                        var value = inline ?? TakeValue(args, ref i, null);
                        cmdArgs[key.TrimStart('-')] = value ?? "true";
                        break;
                }
                continue;
            }

            if (a.StartsWith('-') && a.Length == 2)
            {
                if (a == "-h") return new ParseResult { ShowHelp = true };
                cmdArgs[a[1..]] = "true";
                continue;
            }

            positionals.Add(a);
        }

        if (positionals.Count == 0)
            return new ParseResult { Error = "缺少命令，使用 --help 查看用法" };

        var command = positionals[0].ToLowerInvariant();
        if (positionals.Count > 1) command += "." + positionals[1].ToLowerInvariant();

        if (!CommandCatalog.IsKnown(command))
            return new ParseResult { Error = $"未知命令：{command}（使用 --help 查看支持的命令）" };

        command = CommandCatalog.Normalize(command);

        if (!string.IsNullOrWhiteSpace(opts.Profile)) cmdArgs["profile"] = opts.Profile;
        if (opts.Apply) opts.DryRun = false;

        return new ParseResult
        {
            Command = new ParsedCommand { Command = command, Args = cmdArgs, Options = opts }
        };
    }

    /// <summary>批量文件的一行；继承指定的全局选项，行内选项优先。</summary>
    public static ParseResult ParseBatchLine(string line, ParsedOptions inherited)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return new ParseResult();

        var prefix = new List<string>();
        if (inherited.Json) prefix.Add("--json");
        if (!string.IsNullOrWhiteSpace(inherited.Profile)) { prefix.Add("--profile"); prefix.Add(inherited.Profile); }
        if (!string.IsNullOrWhiteSpace(inherited.ConfigDir)) { prefix.Add("--config-dir"); prefix.Add(inherited.ConfigDir); }

        var lineArgs = SplitLine(trimmed);
        var combined = prefix.Concat(lineArgs).ToArray();
        var result = Parse(combined);
        if (result.IsOk && result.Command is not null)
        {
            var cmd = result.Command;
            var merged = new ParsedCommand
            {
                Command = cmd.Command,
                Args = cmd.Args,
                Options = new ParsedOptions
                {
                    Json = cmd.Options.Json || inherited.Json,
                    Apply = cmd.Options.Apply,
                    DryRun = cmd.Options.DryRun && !cmd.Options.Apply,
                    Profile = cmd.Options.Profile ?? inherited.Profile,
                    ConfigDir = cmd.Options.ConfigDir ?? inherited.ConfigDir
                }
            };
            return new ParseResult { Command = merged };
        }
        return result;
    }

    private static List<string> SplitLine(string line)
    {
        var list = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        foreach (var ch in line)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else current.Append(ch);
            }
            else if (ch is '"' or '\'') quote = ch;
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0) { list.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(ch);
        }
        if (current.Length > 0) list.Add(current.ToString());
        return list;
    }

    private static string? TakeValue(string[] args, ref int i, string? inline)
    {
        if (inline is not null) return inline;
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            return args[++i];
        return null;
    }

    public static string BuildHelp() => """
        cex - ClassExland 运维 CLI（只读优先，写操作需 --yes）

        用法:
          cex [全局选项] <命令> [参数]

        全局选项:
          --json              以 JSON 输出结果
          --yes               真正执行写操作（默认仅预览）
          --dry-run           预览写操作差异（默认）
          --profile <name>    指定 ClassIsland 档案
          --config-dir <path> 指定插件配置目录（覆盖自动发现）
          --batch <file>      批量执行，文件每行一条命令
          --help, -h          显示帮助
          --version           显示版本

        命令:
          status                       当前状态 / 下一节课 / 剩余秒数 / 值日概况
          bell list                    按课表列出每节课的序号、有效提前量与是否覆盖
          bell set --index 3 --prep 90 逐节设置（默认 dry-run，加 --yes 落盘并立即生效）
          bell set --slot <id> --prep 90  用 bell list 输出的 id 精确指定
          bell import|export|preview
          duty show|edit|import|export|rotate|complete
          activity list|add|edit|test
          layout switch|current
          config get|set|diff|doctor
          remote status|sync|poll|upload|enable|disable|server
          backup list|create|restore
          audit list|export
          migrate                      旧插件配置迁移
          web start|stop|status        本机 loopback 管理页
          profile list|switch|current  多班级档案切换（switch 高危，需 --yes --confirm）
          lucky status                  LuckyPicker 联动状态（历史文件是否存在、已见记录数）
          lucky announce --text "张三"  在 ClassIsland 上广播一段文字（写操作，需 --yes）
          class set                    强制课堂状态（高危，需 --yes --confirm）
          version

        安全约定:
          - 写操作默认只预览差异，加 --yes 才落盘；离线写会先备份并做 schema 校验
          - 高危操作（class set / config set / backup restore / migrate / remote server / web start / profile switch）需额外 --confirm
          - 检测到 ClassIsland 正在运行时，离线写命令被拒绝（退出码 2）
          - 退出码: 0成功 1校验失败 2配置占用 3IPC不可达 4安全策略拒绝
        """;
}
