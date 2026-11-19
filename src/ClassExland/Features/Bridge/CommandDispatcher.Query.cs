using System.Reflection;
using System.Text.Json;
using ClassExland.Contracts;
using ClassExland.Features.Bell;

namespace ClassExland.Features.Bridge;

public sealed partial class CommandDispatcher
{
    private Task<CommandResult> QueryStatusAsync(CommandRequest r, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var data = new Dictionary<string, object?>
        {
            ["now"] = now,
            ["ipc"] = _ipc.Snapshot(),
            ["hostRunning"] = FileStore.IsHostRunning()
        };

        var bell = TryService<IBellTimeline>();
        if (bell is not null)
        {
            data["virtualState"] = bell.VirtualState.ToString();
            data["currentMoment"] = bell.Current;
            data["nextBellAt"] = bell.NextBellAt;
            data["secondsToNextBell"] = bell.NextBellAt is { } nb
                ? Math.Max(0, (int)(nb - now).TotalSeconds)
                : null;
        }
        else
        {
            data["virtualState"] = "<BellModule 未加载>";
            data["secondsToNextBell"] = null;
        }

        var duty = TryService<IDutyQuery>();
        if (duty is not null)
        {
            data["duty"] = duty.CurrentAt(now);
            data["profile"] = _ipc.GetProperty("IPublicProfileService", "CurrentProfilePath");
        }
        else
        {
            data["duty"] = "<DutyModule 未加载>";
        }

        data["bellConfiguredSlots"] = CountConfigItems(ConfigFiles.Bell);
        return Task.FromResult(Ok(data));
    }

    private Task<CommandResult> QueryVersionAsync(CommandRequest r, CancellationToken ct)
    {
        var asm = typeof(CommandDispatcher).Assembly;
        var snap = _ipc.Snapshot();
        var ipcReady = snap.TryGetValue("ipc.IPublicLessonsService.available", out var v) && v is true;
        var data = new
        {
            product = "ClassExland",
            version = asm.GetName().Version?.ToString() ?? "1.0.0.0",
            bridgeProtocol = "1.0",
            channels = ipcReady ? "official-ipc+local-pipe" : "local-pipe",
            runtime = Environment.Version.ToString()
        };
        return Task.FromResult(Ok(data));
    }

    private Task<CommandResult> QueryBellListAsync(CommandRequest r, CancellationToken ct)
    {
        // 优先按"课表里的全部上课段"输出：bell.json 可能还是空的，
        // 用户需要先看到 index / id 才能逐节设置。
        var slots = EnumerateBellSlots();
        if (slots.Count > 0)
        {
            var settings = TryService<BellSettings>();
            var items = slots.Select(s => new
            {
                index = s.Index,
                id = s.SlotKey,
                slot = s.Display,
                layout = s.LayoutName,
                time = $"{s.Start:hh\\:mm}-{s.End:hh\\:mm}",
                effectivePrepSeconds = s.EffectivePrep,
                prepSeconds = s.Overridden ? s.EffectivePrep : (int?)null,
                overridden = s.Overridden
            }).ToList();

            return Task.FromResult(Ok(new
            {
                file = ConfigFiles.Bell,
                count = items.Count,
                source = "档案课表 + bell.json",
                defaultPrepSeconds = settings?.DefaultPrepSeconds,
                items
            }));
        }

        using var doc = _store.ReadDocument(ConfigFiles.Bell);
        if (doc is null)
            return Task.FromResult(Ok(new { items = Array.Empty<object>(), note = "bell.json 不存在（尚未配置预备铃）" }));

        var rawItems = ExtractRootArray(doc.RootElement);
        var simplified = new List<object?>();
        foreach (var item in rawItems)
        {
            simplified.Add(new
            {
                id = ReadString(item, "id", "slotId", "timeLayoutItemId"),
                slot = ReadString(item, "slot", "label", "name"),
                subject = ReadString(item, "subject"),
                prepSeconds = ReadInt(item, "prepSeconds", "prep", "PrepSeconds"),
                enabled = ReadBool(item, "enabled", "Enabled")
            });
        }
        return Task.FromResult(Ok(new { file = ConfigFiles.Bell, count = simplified.Count, items = simplified }));
    }

    private Task<CommandResult> QueryBellPreviewAsync(CommandRequest r, CancellationToken ct)
        => BuildBellSetPreview(r, apply: false);

    private Task<CommandResult> QueryDutyShowAsync(CommandRequest r, CancellationToken ct)
    {
        var dateText = GetArg(r, "date");
        var date = ParseDate(dateText) ?? DateOnly.FromDateTime(DateTime.Today);
        var data = new Dictionary<string, object?> { ["date"] = date };

        var duty = TryService<IDutyQuery>();
        if (duty is not null)
        {
            data["current"] = duty.CurrentAt(DateTimeOffset.Now);
            data["views"] = duty.ForDate(date);
            data["source"] = "DutyModule";
        }
        else
        {
            using var doc = _store.ReadDocument(ConfigFiles.Duty);
            data["source"] = "duty.json (DutyModule 未加载)";
            data["config"] = doc is null ? null : JsonSerializer.SerializeToElement(doc.RootElement);
        }
        return Task.FromResult(Ok(data));
    }

    private Task<CommandResult> QueryActivityListAsync(CommandRequest r, CancellationToken ct)
    {
        using var doc = _store.ReadDocument(ConfigFiles.Activity);
        var items = doc is null ? new List<object?>() : ExtractRootArray(doc.RootElement)
            .Select(x => (object?)JsonSerializer.SerializeToElement(x)).ToList();
        return Task.FromResult(Ok(new
        {
            file = ConfigFiles.Activity,
            count = items.Count,
            items,
            note = doc is null ? "activities.json 不存在" : null
        }));
    }

    private Task<CommandResult> QueryLayoutCurrentAsync(CommandRequest r, CancellationToken ct)
    {
        using var doc = _store.ReadDocument(ConfigFiles.Layout);
        var data = new Dictionary<string, object?>
        {
            ["config"] = doc is null ? null : JsonSerializer.SerializeToElement(doc.RootElement),
            ["virtualState"] = TryService<IBellTimeline>()?.VirtualState.ToString() ?? "<BellModule 未加载>"
        };

        // ILayoutService.CurrentLayout（class / break / 空）
        var svc = TryResolveInterface("ClassExland.Features.Layout.ILayoutService");
        if (svc is not null)
        {
            var prop = svc.GetType().GetProperty("CurrentLayout", BindingFlags.Public | BindingFlags.Instance);
            if (prop is not null) data["currentLayout"] = prop.GetValue(svc)?.ToString();
        }

        return Task.FromResult(Ok(data));
    }

    private Task<CommandResult> QueryConfigGetAsync(CommandRequest r, CancellationToken ct)
    {
        var file = GetArg(r, "file") ?? GetArg(r, "key");
        if (string.IsNullOrWhiteSpace(file))
        {
            var summary = ConfigFileNames().Select(f => new
            {
                file = f,
                exists = _store.Exists(f),
                size = _store.Exists(f) ? new FileInfo(_store.PathOf(f)).Length : 0
            });
            return Task.FromResult(Ok(new { files = summary }));
        }

        var raw = _store.ReadRaw(file);
        if (raw is null) return Task.FromResult(Fail(ExitCodes.Validation, $"配置文件不存在：{file}"));
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return Task.FromResult(Ok(new { file, config = JsonSerializer.SerializeToElement(doc.RootElement) }));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(Fail(ExitCodes.Validation, $"{file} JSON 无效：{ex.Message}"));
        }
    }

    private Task<CommandResult> QueryConfigDiffAsync(CommandRequest r, CancellationToken ct)
    {
        var diffs = new List<object?>();
        foreach (var file in ConfigFileNames())
        {
            var current = _store.ReadRaw(file);
            if (current is null) continue;
            var backup = LatestBackupFor(file);
            if (backup is null)
            {
                diffs.Add(new { file, status = "no-backup" });
                continue;
            }
            var previous = File.ReadAllText(backup);
            diffs.Add(new
            {
                file,
                status = previous == current ? "same" : "changed",
                backup = Path.GetFileName(backup)
            });
        }
        return Task.FromResult(Ok(new { diffs }));
    }

    private Task<CommandResult> QueryConfigDoctorAsync(CommandRequest r, CancellationToken ct)
    {
        var issues = new List<string>();
        var warnings = new List<string>();

        foreach (var file in ConfigFileNames())
        {
            if (!_store.Exists(file)) continue;
            var raw = _store.ReadRaw(file)!;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    issues.Add($"{file}: 根节点类型无效");

                if (file == ConfigFiles.Bell && doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var prep = ReadInt(item, "prepSeconds", "prep", "PrepSeconds");
                        if (prep is < 0 or > 600)
                            issues.Add($"bell.json: 提前量 {prep} 超出 0–600 秒范围（id={ReadString(item, "id", "slotId", "timeLayoutItemId")}）");
                    }
                }

                if (file == ConfigFiles.Duty && doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // 真实模型：批次数组在根级 plans，当前批次为根级 activePlanIndex
                    if (doc.RootElement.TryGetProperty("activePlanIndex", out var idx) && idx.ValueKind == JsonValueKind.Number)
                    {
                        var planCount = doc.RootElement.TryGetProperty("plans", out var plans) && plans.ValueKind == JsonValueKind.Array
                            ? plans.GetArrayLength() : 0;
                        if (planCount > 0 && idx.TryGetInt32(out var n) && (n < 0 || n >= planCount))
                            issues.Add($"duty.json: activePlanIndex={n} 超出 plans 批次范围 0–{planCount - 1}");
                    }
                }

                if (file == ConfigFiles.Activity && doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // 真实模型：活动提醒时点为 reminderTimes（HH:mm 字符串数组），不是 periods
                    if (doc.RootElement.TryGetProperty("activities", out var acts) && acts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in acts.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object && !item.TryGetProperty("reminderTimes", out _) &&
                                item.TryGetProperty("periods", out _))
                                issues.Add("activities.json: 检测到旧字段 periods，应为 reminderTimes（HH:mm 数组）");
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                issues.Add($"{file}: JSON 无效 - {ex.Message}");
            }
        }

        if (!_store.Exists(ConfigFiles.Bell)) warnings.Add("bell.json 不存在：预备铃尚未配置");
        if (!_store.Exists(ConfigFiles.Duty)) warnings.Add("duty.json 不存在：值日表尚未配置");
        if (FileStore.IsHostRunning()) warnings.Add("ClassIsland 正在运行：离线写命令将被拒绝，请改用在线命令");

        var endpointPath = _store.PathOf(ConfigFiles.Bridge);
        if (!File.Exists(endpointPath)) issues.Add("bridge.json 不存在：指令桥端点未生成");
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(endpointPath));
                var token = ReadString(doc.RootElement, "token");
                if (string.IsNullOrEmpty(token) || token.Length < 16) issues.Add("bridge.json 令牌无效");
            }
            catch (JsonException) { issues.Add("bridge.json JSON 无效"); }
        }

        var discover = BridgeEndpoint.DiscoverFilePath;
        if (!File.Exists(discover)) warnings.Add("CLI 发现文件缺失（插件可能尚未启动过）");

        var ipc = _ipc.Snapshot();
        var lessonsOk = ipc.TryGetValue("ipc.IPublicLessonsService.available", out var lv) && lv is true;
        var profileOk = ipc.TryGetValue("ipc.IPublicProfileService.available", out var pv) && pv is true;
        if (!lessonsOk) warnings.Add("官方 IPC IPublicLessonsService 不可用：在线状态查询回退到本地模块");
        if (!profileOk) warnings.Add("官方 IPC IPublicProfileService 不可用：档案相关查询受限");

        return Task.FromResult(Ok(new
        {
            healthy = issues.Count == 0,
            checkedAt = DateTimeOffset.UtcNow,
            issueCount = issues.Count,
            issues,
            warningCount = warnings.Count,
            warnings
        }));
    }

    private Task<CommandResult> QueryBackupListAsync(CommandRequest r, CancellationToken ct)
    {
        var dir = _paths.BackupDirectory;
        if (!Directory.Exists(dir))
            return Task.FromResult(Ok(new { backups = Array.Empty<object>(), directory = dir }));

        var backups = Directory.GetFiles(dir)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new { name = f.Name, size = f.Length, createdAt = f.LastWriteTime })
            .ToList();
        return Task.FromResult(Ok(new { directory = dir, count = backups.Count, backups }));
    }

    private Task<CommandResult> QueryAuditListAsync(CommandRequest r, CancellationToken ct)
    {
        var take = GetArgInt(r, "take", 50);
        var entries = _audit.ReadRecent(take).Select(d => (object)JsonSerializer.SerializeToElement(d.RootElement)).ToList();
        return Task.FromResult(Ok(new { path = _paths.AuditLogPath, count = entries.Count, entries }));
    }

    private Task<CommandResult> QueryRemoteStatusAsync(CommandRequest r, CancellationToken ct)
    {
        using var doc = _store.ReadDocument(ConfigFiles.Remote);
        var data = new Dictionary<string, object?>
        {
            ["config"] = doc is null ? null : JsonSerializer.SerializeToElement(doc.RootElement),
            ["configured"] = _store.Exists(ConfigFiles.Remote),
            ["server"] = ReadString(doc?.RootElement ?? default, "server", "serverUrl", "url")
        };

        // IRemoteSyncService.Status：结构化为 JSON，便于 --json 脚本读取。字段名按服务声明原样输出（大小写由服务定稿决定）
        var svc = TryResolveInterface("ClassExland.Features.Remote.IRemoteSyncService");
        if (svc is not null)
        {
            var prop = svc.GetType().GetProperty("Status", BindingFlags.Public | BindingFlags.Instance);
            var statusObj = prop?.GetValue(svc);
            if (statusObj is not null)
            {
                try
                {
                    // 不套用 camelCase 命名策略，按 Status 声明的字段名原样输出（兼容 PascalCase 或小写定稿）
                    data["serviceStatus"] = JsonSerializer.SerializeToElement(statusObj, statusObj.GetType());
                }
                catch { data["serviceStatus"] = statusObj.ToString(); }
            }
        }
        var web = WebHost;
        data["web"] = web is null
            ? new { running = false, url = "", port = 0, note = "WebHost 未注册" }
            : new { running = web.IsRunning, url = web.Url, port = web.Port };
        return Task.FromResult(Ok(data));
    }

    // ---------- JSON 小工具 ----------

    private static List<JsonElement> ExtractRootArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToList();
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "items", "slots", "Layouts", "layouts", "plans", "activities", "bells" })
                if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    return arr.EnumerateArray().ToList();
        }
        return new List<JsonElement>();
    }

    private static string? ReadString(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static int? ReadInt(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
        return null;
    }

    private static bool? ReadBool(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return v.GetBoolean();
        return null;
    }

    private int CountConfigItems(string file)
    {
        using var doc = _store.ReadDocument(file);
        return doc is null ? 0 : ExtractRootArray(doc.RootElement).Count;
    }

    private string? LatestBackupFor(string file)
    {
        var dir = _paths.BackupDirectory;
        if (!Directory.Exists(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(file);
        return Directory.GetFiles(dir, stem + ".*" + Path.GetExtension(file))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
