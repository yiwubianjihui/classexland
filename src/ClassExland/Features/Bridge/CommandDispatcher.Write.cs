using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassExland.Contracts;
using ClassExland.Features.Bell;

namespace ClassExland.Features.Bridge;

public sealed partial class CommandDispatcher
{
    // ---------- Bell ----------

    private Task<CommandResult> WriteBellSetAsync(CommandRequest r, CancellationToken ct)
        => BuildBellSetPreview(r, apply: r.Apply);

    private Task<CommandResult> BuildBellSetPreview(CommandRequest r, bool apply)
    {
        var slot = GetArg(r, "slot") ?? GetArg(r, "id");
        var indexText = GetArg(r, "index");
        BellSlotInfo? target = null;

        // --index N：按 cex bell list 的序号定位，省去手打 "档案ID|开始|结束" 这种长键。
        if (!string.IsNullOrWhiteSpace(indexText))
        {
            if (!int.TryParse(indexText, out var index) || index < 1)
                return Task.FromResult(Fail(ExitCodes.Validation, "--index 必须是 ≥1 的整数（见 bell list 的 index 列）"));

            target = EnumerateBellSlots().FirstOrDefault(s => s.Index == index);
            if (target is null)
                return Task.FromResult(Fail(ExitCodes.Validation,
                    $"没有第 {index} 节课；先运行 cex bell list 查看可用序号"));

            slot = target.SlotKey;
        }

        if (string.IsNullOrWhiteSpace(slot)) return Task.FromResult(Fail(ExitCodes.Validation, "缺少 --slot <id> 或 --index <n>"));
        if (!int.TryParse(GetArg(r, "prep"), out var prep) || prep is < 0 or > 600)
            return Task.FromResult(Fail(ExitCodes.Validation, "--prep 必须是 0–600 的整数"));

        var label = target?.Display;

        if (!apply)
            return Task.FromResult(Preview(new
            {
                file = ConfigFiles.Bell,
                slot,
                slotLabel = label,
                prepSeconds = prep,
                action = "set"
            }));

        try
        {
            var backup = _store.Backup(ConfigFiles.Bell);
            var (bellRoot, array) = LoadRootAndArray(_store.ReadRaw(ConfigFiles.Bell), "items", "slots", "bells");
            var found = false;
            foreach (var item in array)
            {
                if (item is JsonObject obj && MatchSlot(obj, slot))
                {
                    // 真值字段为 prepSeconds（bell-dev 对齐），不写 prep 别名
                    obj["prepSeconds"] = prep;
                    if (label is not null) obj["slot"] = label;
                    found = true;
                }
            }
            if (!found)
                array.Add(new JsonObject { ["id"] = slot, ["prepSeconds"] = prep, ["enabled"] = true, ["slot"] = label });

            _store.Write(ConfigFiles.Bell, bellRoot);
            var appliedToProfile = RefreshBellAfterWrite();
            return Task.FromResult(Ok(new
            {
                slot,
                slotLabel = label,
                prepSeconds = prep,
                created = !found,
                appliedToProfile,
                backup
            }));
        }
        catch (CommandAbortedException ex)
        {
            return Task.FromResult(Fail(ex.ExitCode, ex.Message));
        }
    }

    private Task<CommandResult> WriteBellImportAsync(CommandRequest r, CancellationToken ct)
    {
        var from = GetArg(r, "from");
        if (string.IsNullOrWhiteSpace(from) || !File.Exists(from))
            return Task.FromResult(Fail(ExitCodes.Validation, "--from 指定的文件不存在"));

        try
        {
            var source = JsonNode.Parse(File.ReadAllText(from));
            if (source is null) return Task.FromResult(Fail(ExitCodes.Validation, "源文件 JSON 无效"));
            var incoming = source is JsonArray arr ? arr : new JsonArray(source);
            var preview = new { source = from, incomingCount = incoming.Count, action = "append to bell.json" };
            if (!r.Apply) return Task.FromResult(Preview(preview));

            var backup = _store.Backup(ConfigFiles.Bell);
            var (bellRoot, target) = LoadRootAndArray(_store.ReadRaw(ConfigFiles.Bell), "items", "slots", "bells");
            foreach (var item in incoming) target.Add(item?.DeepClone());
            _store.Write(ConfigFiles.Bell, bellRoot);
            var appliedToProfile = RefreshBellAfterWrite();
            return Task.FromResult(Ok(new { imported = incoming.Count, appliedToProfile, backup }));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(Fail(ExitCodes.Validation, "源文件 JSON 无效：" + ex.Message));
        }
        catch (CommandAbortedException ex)
        {
            return Task.FromResult(Fail(ex.ExitCode, ex.Message));
        }
    }

    private Task<CommandResult> WriteBellExportAsync(CommandRequest r, CancellationToken ct)
    {
        var outPath = GetArg(r, "out");
        if (string.IsNullOrWhiteSpace(outPath)) return Task.FromResult(Fail(ExitCodes.Validation, "缺少 --out <path>"));
        var raw = _store.ReadRaw(ConfigFiles.Bell);
        if (raw is null) return Task.FromResult(Fail(ExitCodes.Validation, "bell.json 不存在"));
        if (!r.Apply) return Task.FromResult(Preview(new { outPath, bytes = raw.Length, action = "export bell.json" }));
        try
        {
            File.WriteAllText(outPath, raw);
            return Task.FromResult(Ok(new { outPath, bytes = raw.Length }));
        }
        catch (CommandAbortedException ex) { return Task.FromResult(Fail(ex.ExitCode, ex.Message)); }
        catch (Exception ex) { return Task.FromResult(Fail(ExitCodes.Validation, "导出失败：" + ex.Message)); }
    }

    // ---------- Duty ----------

    private Task<CommandResult> WriteDutyEditAsync(CommandRequest r, CancellationToken ct)
        => ApplyJsonPathEdit(r, ConfigFiles.Duty, ct);

    private Task<CommandResult> WriteDutyImportAsync(CommandRequest r, CancellationToken ct)
    {
        var from = GetArg(r, "from");
        var plan = GetArg(r, "plan") ?? "Imported";
        if (string.IsNullOrWhiteSpace(from) || !File.Exists(from))
            return Task.FromResult(Fail(ExitCodes.Validation, "--from 指定的文件不存在"));

        try
        {
            var source = JsonNode.Parse(File.ReadAllText(from));
            if (source is null) return Task.FromResult(Fail(ExitCodes.Validation, "源文件 JSON 无效"));
            var node = source is JsonObject o ? o : new JsonObject { ["raw"] = source };
            node["name"] ??= plan;

            var preview = new { source = from, plan, action = "append to duty.json plans" };
            if (!r.Apply) return Task.FromResult(Preview(preview));

            var backup = _store.Backup(ConfigFiles.Duty);
            var root = _store.ReadRaw(ConfigFiles.Duty) is null
                ? new JsonObject { ["plans"] = new JsonArray() }
                : JsonNode.Parse(_store.ReadRaw(ConfigFiles.Duty)!) as JsonObject ?? new JsonObject { ["plans"] = new JsonArray() };
            var plans = root["plans"] as JsonArray ?? new JsonArray();
            plans.Add(node.DeepClone());
            root["plans"] = plans;
            _store.Write(ConfigFiles.Duty, root);
            return Task.FromResult(Ok(new { plan, imported = true, backup }));
        }
        catch (JsonException ex) { return Task.FromResult(Fail(ExitCodes.Validation, "源文件 JSON 无效：" + ex.Message)); }
        catch (CommandAbortedException ex) { return Task.FromResult(Fail(ex.ExitCode, ex.Message)); }
    }

    private Task<CommandResult> WriteDutyExportAsync(CommandRequest r, CancellationToken ct)
    {
        var outPath = GetArg(r, "out");
        if (string.IsNullOrWhiteSpace(outPath)) return Task.FromResult(Fail(ExitCodes.Validation, "缺少 --out <path>"));
        var raw = _store.ReadRaw(ConfigFiles.Duty);
        if (raw is null) return Task.FromResult(Fail(ExitCodes.Validation, "duty.json 不存在"));
        if (!r.Apply) return Task.FromResult(Preview(new { outPath, action = "export duty.json" }));
        try
        {
            File.WriteAllText(outPath, raw);
            return Task.FromResult(Ok(new { outPath }));
        }
        catch (CommandAbortedException ex) { return Task.FromResult(Fail(ex.ExitCode, ex.Message)); }
        catch (Exception ex) { return Task.FromResult(Fail(ExitCodes.Validation, "导出失败：" + ex.Message)); }
    }

    private Task<CommandResult> WriteDutyRotateAsync(CommandRequest r, CancellationToken ct)
    {
        var to = GetArg(r, "to");
        if (!int.TryParse(to, out var target) || target < 0)
            return Task.FromResult(Fail(ExitCodes.Validation, "--to 必须是非负整数（第 N 批）"));
        if (!r.Apply)
            return Task.FromResult(Preview(new { current = CurrentRotationIndex(), target, action = "rotate duty" }));
        try
        {
            var backup = _store.Backup(ConfigFiles.Duty);
            // 真实模型：批次数组在根级 plans，当前索引是根级 activePlanIndex（不是 rotation.currentIndex）
            var root = JsonNode.Parse(_store.ReadRaw(ConfigFiles.Duty) ?? "{}") as JsonObject ?? new JsonObject();
            var previous = root["activePlanIndex"]?.GetValue<int>() ?? 0;
            root["activePlanIndex"] = target;
            // rotation 内 startDate/periodDays/mode/skipWeekdays 保持原样，不写 currentIndex
            _store.Write(ConfigFiles.Duty, root);
            return Task.FromResult(Ok(new { previous, current = target, backup }));
        }
        catch (CommandAbortedException ex) { return Task.FromResult(Fail(ex.ExitCode, ex.Message)); }
        catch (JsonException ex) { return Task.FromResult(Fail(ExitCodes.Validation, "duty.json 无效：" + ex.Message)); }
    }

    private Task<CommandResult> WriteDutyCompleteAsync(CommandRequest r, CancellationToken ct)
    {
        var item = GetArg(r, "item");
        if (string.IsNullOrWhiteSpace(item)) return Task.FromResult(Fail(ExitCodes.Validation, "缺少 --item <项目>"));
        var date = (ParseDate(GetArg(r, "date")) ?? DateOnly.FromDateTime(DateTime.Today)).ToString("yyyy-MM-dd");
        const string file = "duty-completions.json";
        if (!r.Apply) return Task.FromResult(Preview(new { date, item, action = "mark duty complete" }));
        try
        {
            var root = JsonNode.Parse(_store.ReadRaw(file) ?? "[]") as JsonArray ?? new JsonArray();
            root.Add(new JsonObject { ["date"] = date, ["item"] = item, ["at"] = DateTimeOffset.UtcNow.ToString("O") });
            _store.Write(file, root);
            return Task.FromResult(Ok(new { date, item, recorded = true }));
        }
        catch (CommandAbortedException ex) { return Task.FromResult(Fail(ex.ExitCode, ex.Message)); }
        catch (JsonException ex) { return Task.FromResult(Fail(ExitCodes.Validation, "完成记录文件无效：" + ex.Message)); }
    }

    // ---------- Activity ----------

    private Task<CommandResult> WriteActivityAddAsync(CommandRequest r, CancellationToken ct)
    {
        var name = GetArg(r, "name");
        if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(Fail(ExitCodes.Validation, "缺少 --name <名称>"));
        var id = GetArg(r, "id") ?? Guid.NewGuid().ToString("N");
        // CLI 入参名保留 --periods，但写入 JSON 用真实模型字段 reminderTimes（HH:mm 字符串数组）
        var reminderTimes = ParseReminderTimes(GetArg(r, "periods"));
        var node = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["enabled"] = true,
            ["rotationMode"] = GetArg(r, "mode") ?? "Month",
            ["reminderTimes"] = reminderTimes
        };
        if (!r.Apply) return Task.FromResult(Preview(new { id, name, action = "add activity" }));
        try
        {
            var backup = _store.Backup(ConfigFiles.Activity);
            var (activityRoot, array) = LoadRootAndArray(_store.ReadRaw(ConfigFiles.Activity), "activities");
            array.Add(node);
            _store.Write(ConfigFiles.Activity, activityRoot);
            return Task.FromResult(Ok(new { id, name, backup }));
        }
        catch (CommandAbortedException ex) { return Task.FromResult(Fail(ex.ExitCode, ex.Message)); }
    }

    private Task<CommandResult> WriteActivityEditAsync(CommandRequest r, CancellationToken ct)
    {
        var id = GetArg(r, "id");
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(Fail(ExitCodes.Validation, "缺少 --id"));
        var path = GetArg(r, "path");
        var value = GetArg(r, "value");
        if (string.IsNullOrWhiteSpace(path) || value is null) return Task.FromResult(Fail(ExitCodes.Validation, "需要 --path <路径> 与 --value <值>"));
        if (!r.Apply) return Task.FromResult(Preview(new { id, path, value, action = "edit activity" }));
        try
        {
            var backup = _store.Backup(ConfigFiles.Activity);
            var (activityRoot, array) = LoadRootAndArray(_store.ReadRaw(ConfigFiles.Activity), "activities");
            var found = false;
            foreach (var item in array)
            {
                if (item is JsonObject obj && MatchSlot(obj, id))
                {
                    SetJsonPath(obj, path, value);
                    found = true;
                    break;
                }
            }
            if (!found) return Task.FromResult(Fail(ExitCodes.Validation, $"未找到活动 id={id}"));
            _store.Write(ConfigFiles.Activity, activityRoot);
            return Task.FromResult(Ok(new { id, path, value, backup }));
        }
        catch (CommandAbortedException ex) { return Task.FromResult(Fail(ex.ExitCode, ex.Message)); }
    }

    private async Task<CommandResult> WriteActivityTestAsync(CommandRequest r, CancellationToken ct)
    {
        var id = GetArg(r, "id") ?? "all";
        if (!r.Apply) return Preview(new { id, action = "fire activity notice once" });

        var svc = TryResolveInterface("ClassExland.Features.Activity.IActivityNotifier");
        var ok = await TryInvokeAsync(svc, "TestAsync", new object?[] { id }, ct).ConfigureAwait(false);
        if (ok is null)
            return Fail(ExitCodes.IpcUnavailable,
                "未找到活动通知服务（ActivityModule 未提供 IActivityNotifier），未发送测试提醒");
        return Ok(new { id, fired = true });
    }

    // ---------- Layout ----------

    private async Task<CommandResult> WriteLayoutSwitchAsync(CommandRequest r, CancellationToken ct)
    {
        var to = GetArg(r, "to");
        if (to is not ("class" or "break"))
            return Fail(ExitCodes.Validation, "--to 必须是 class 或 break");
        var target = to == "class" ? "class" : "break";
        if (!r.Apply) return Preview(new { from = TryService<IBellTimeline>()?.VirtualState.ToString(), to = target, action = "switch layout" });

        var svc = TryResolveInterface("ClassExland.Features.Layout.ILayoutService");
        var invoked = await TryInvokeAsync(svc, "SwitchToAsync", new object?[] { target }, ct).ConfigureAwait(false);
        if (invoked is null)
        {
            // 回退：写 layout.json 当前布局字段（在线服务不可用时按文件方式切换）
            try
            {
                var backup = _store.Backup(ConfigFiles.Layout);
                var root = JsonNode.Parse(_store.ReadRaw(ConfigFiles.Layout) ?? "{}") as JsonObject ?? new JsonObject();
                root["current"] = target;
                _store.Write(ConfigFiles.Layout, root);
                return Ok(new { to = target, via = "file", backup });
            }
            catch (CommandAbortedException ex) { return Fail(ex.ExitCode, ex.Message); }
        }
        return Ok(new { to = target, via = "LayoutModule" });
    }

    // ---------- Config ----------

    private Task<CommandResult> WriteConfigSetAsync(CommandRequest r, CancellationToken ct)
        => ApplyJsonPathEdit(r, GetArg(r, "file") ?? ConfigFiles.Bell, ct);

    private Task<CommandResult> ApplyJsonPathEdit(CommandRequest r, string file, CancellationToken ct)
    {
        var path = GetArg(r, "path");
        var value = GetArg(r, "value");
        if (string.IsNullOrWhiteSpace(path) || value is null)
            return Task.FromResult(Fail(ExitCodes.Validation, "需要 --path <点分路径> 与 --value <值>"));
        if (!r.Apply)
            return Task.FromResult(Preview(new { file, path, value, action = "set json path" }));
        try
        {
            var backup = _store.Backup(file);
            var raw = _store.ReadRaw(file) ?? "{}";
            var root = JsonNode.Parse(raw) ?? new JsonObject();
            SetJsonPath(root, path, value);
            _store.Write(file, root);
            var bellRefreshed = IsBellConfigFile(file) && RefreshBellAfterWrite();
            return Task.FromResult(Ok(new { file, path, value, bellRefreshed, backup }));
        }
        catch (CommandAbortedException ex) { return Task.FromResult(Fail(ex.ExitCode, ex.Message)); }
        catch (JsonException ex) { return Task.FromResult(Fail(ExitCodes.Validation, "JSON 无效：" + ex.Message)); }
    }

    // ---------- Remote ----------

    private async Task<CommandResult> WriteRemoteSyncAsync(CommandRequest r, CancellationToken ct)
    {
        if (!r.Apply) return Preview(new { action = "remote sync now" });
        var svc = TryResolveInterface("ClassExland.Features.Remote.IRemoteSyncService");
        // 定稿签名：Task<string> SyncNowAsync()（零参数），返回 JSON 摘要 {ok,version,etag,message}
        var result = await TryInvokeAsync(svc, "SyncNowAsync", Array.Empty<object?>(), ct).ConfigureAwait(false);
        if (result is null)
            return Fail(ExitCodes.IpcUnavailable, "远程同步服务不可用（RemoteModule 未提供 IRemoteSyncService）");

        var detail = TryParseJsonSummary(result);
        var ok = ReadBoolKey(detail, "ok") ?? true;
        return ok
            ? Ok(new { synced = true, detail })
            : Fail(ExitCodes.Validation, $"同步失败：{ReadStringKey(detail, "message")}", detail);
    }

    private async Task<CommandResult> WriteRemotePollAsync(CommandRequest r, CancellationToken ct)
    {
        if (!r.Apply) return Preview(new { action = "remote poll once" });
        var svc = TryResolveInterface("ClassExland.Features.Remote.IRemoteSyncService");
        // 定稿签名：Task<string> PollOnceAsync()（零参数）
        var result = await TryInvokeAsync(svc, "PollOnceAsync", Array.Empty<object?>(), ct).ConfigureAwait(false);
        if (result is null)
            return Fail(ExitCodes.IpcUnavailable, "远程同步服务不可用");

        var detail = TryParseJsonSummary(result);
        var ok = ReadBoolKey(detail, "ok") ?? true;
        return ok
            ? Ok(new { polled = true, detail })
            : Fail(ExitCodes.Validation, $"轮询失败：{ReadStringKey(detail, "message")}", detail);
    }

    private async Task<CommandResult> WriteRemoteUploadAsync(CommandRequest r, CancellationToken ct)
    {
        if (!r.Apply) return Preview(new { action = "remote upload (上传本机现有配置)" });
        var svc = TryResolveInterface("ClassExland.Features.Remote.IRemoteSyncService");
        // 零参数反射契约：Task<string> UploadNowAsync()
        var result = await TryInvokeAsync(svc, "UploadNowAsync", Array.Empty<object?>(), ct).ConfigureAwait(false);
        if (result is null)
            return Fail(ExitCodes.IpcUnavailable, "远程同步服务不可用");

        var detail = TryParseJsonSummary(result);
        var ok = ReadBoolKey(detail, "ok") ?? true;
        var conflict = ReadBoolKey(detail, "conflict") ?? false;
        return ok
            ? Ok(new { uploaded = true, detail })
            : Fail(ExitCodes.Validation,
                (conflict ? "上传冲突（已进入服务器待审队列）：" : "上传失败：") + ReadStringKey(detail, "message"), detail);
    }

    /// <summary>把服务返回的 JSON 摘要字符串解析为 JsonElement，失败则回退为原始字符串。</summary>
    private static object TryParseJsonSummary(object? value)
    {
        var text = value?.ToString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }
            catch { /* 非 JSON 摘要按纯文本处理 */ }
        }
        return text ?? "";
    }

    private static bool? ReadBoolKey(object detail, string key)
    {
        if (detail is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object &&
            je.TryGetProperty(key, out var v))
            return v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => null
            };
        return null;
    }

    private static string? ReadStringKey(object detail, string key)
    {
        if (detail is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object &&
            je.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private async Task<CommandResult> WriteRemoteToggleAsync(CommandRequest r, CancellationToken ct, bool enable)
    {
        if (!r.Apply) return Preview(new { enable, action = "toggle remote sync" });

        // 优先调用 RemoteSyncModule 的 Enable()/Disable()（同步生效），失败再回退写 remote.json
        var module = TryResolveInterface("ClassExland.Features.Remote.RemoteSyncModule");
        if (module is not null)
        {
            var method = enable ? "Enable" : "Disable";
            var invoked = await TryInvokeAsync(module, method, Array.Empty<object?>(), ct).ConfigureAwait(false);
            if (invoked is not null)
                return Ok(new { enabled = enable, via = "RemoteSyncModule" });
        }

        try
        {
            var backup = _store.Backup(ConfigFiles.Remote);
            var root = JsonNode.Parse(_store.ReadRaw(ConfigFiles.Remote) ?? "{}") as JsonObject ?? new JsonObject();
            root["enabled"] = enable;
            _store.Write(ConfigFiles.Remote, root);
            return Ok(new { enabled = enable, via = "file", backup });
        }
        catch (CommandAbortedException ex) { return Fail(ex.ExitCode, ex.Message); }
        catch (JsonException ex) { return Fail(ExitCodes.Validation, "remote.json 无效：" + ex.Message); }
    }

    private Task<CommandResult> WriteRemoteServerAsync(CommandRequest r, CancellationToken ct)
    {
        var clear = GetArgBool(r, "clear");
        var server = GetArg(r, "set") ?? GetArg(r, "url") ?? GetArg(r, "server");
        if (!clear && string.IsNullOrWhiteSpace(server))
            return Task.FromResult(Fail(ExitCodes.Validation, "需要 --set <服务器地址> 或 --clear"));
        if (!clear && (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            return Task.FromResult(Fail(ExitCodes.Validation, "服务器地址必须是合法 http/https URL"));
        if (!r.Apply) return Task.FromResult(Preview(new { server, clear, action = "set remote server" }));
        try
        {
            var backup = _store.Backup(ConfigFiles.Remote);
            var root = JsonNode.Parse(_store.ReadRaw(ConfigFiles.Remote) ?? "{}") as JsonObject ?? new JsonObject();
            if (clear) root.Remove("serverUrl");
            else root["serverUrl"] = server;
            _store.Write(ConfigFiles.Remote, root);
            return Task.FromResult(Ok(new { server, clear, backup, note = "地址保存在外部配置文件，未硬编码" }));
        }
        catch (CommandAbortedException ex) { return Task.FromResult(Fail(ex.ExitCode, ex.Message)); }
        catch (JsonException ex) { return Task.FromResult(Fail(ExitCodes.Validation, "remote.json 无效：" + ex.Message)); }
    }

    // ---------- Backup ----------

    private Task<CommandResult> WriteBackupCreateAsync(CommandRequest r, CancellationToken ct)
    {
        if (!r.Apply) return Task.FromResult(Preview(new { action = "create full config backup" }));
        try
        {
            var path = _store.BackupAll(ConfigFileNames());
            return Task.FromResult(Ok(new { path, size = new FileInfo(path).Length }));
        }
        catch (Exception ex) { return Task.FromResult(Fail(ExitCodes.Validation, "备份失败：" + ex.Message)); }
    }

    private async Task<CommandResult> WriteBackupRestoreAsync(CommandRequest r, CancellationToken ct)
    {
        var from = GetArg(r, "from");
        if (string.IsNullOrWhiteSpace(from) || !File.Exists(from))
            return Fail(ExitCodes.Validation, "--from 指定的备份不存在");
        var full = Path.GetFullPath(from);
        var rootDir = Path.GetFullPath(_paths.BackupDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
            return Fail(ExitCodes.Security, "备份文件必须位于插件备份目录内");

        if (!r.Apply) return Preview(new { from = full, action = "restore config from backup" });
        try
        {
            var safety = _store.BackupAll(ConfigFileNames());
            using var zip = ZipFile.OpenRead(full);
            var restored = new List<string>();
            foreach (var name in ConfigFileNames())
            {
                var entry = zip.GetEntry(name);
                if (entry is null) continue;
                using var src = entry.Open();
                using var dst = File.Create(_paths.Get(name));
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                restored.Add(name);
            }
            return Ok(new { restored, safetyBackup = safety });
        }
        catch (InvalidDataException) { return Fail(ExitCodes.Validation, "备份文件不是合法 zip"); }
        catch (CommandAbortedException ex) { return Fail(ex.ExitCode, ex.Message); }
        catch (Exception ex) { return Fail(ExitCodes.Validation, "恢复失败：" + ex.Message); }
    }

    private Task<CommandResult> WriteAuditExportAsync(CommandRequest r, CancellationToken ct)
    {
        var outPath = GetArg(r, "out");
        if (string.IsNullOrWhiteSpace(outPath)) return Task.FromResult(Fail(ExitCodes.Validation, "缺少 --out <path>"));
        if (!r.Apply) return Task.FromResult(Preview(new { outPath, action = "export audit log" }));
        try
        {
            File.Copy(_paths.AuditLogPath, outPath, overwrite: true);
            return Task.FromResult(Ok(new { outPath }));
        }
        catch (Exception ex) { return Task.FromResult(Fail(ExitCodes.Validation, "导出审计失败：" + ex.Message)); }
    }

    // ---------- Migrate / Web / Class ----------

    private async Task<CommandResult> WriteMigrateAsync(CommandRequest r, CancellationToken ct)
    {
        var from = GetArg(r, "from");
        var plan = GetArg(r, "plan") ?? "Migrated";
        if (string.IsNullOrWhiteSpace(from))
            return Fail(ExitCodes.Validation, "缺少 --from <旧插件配置目录或文件>");
        if (!r.Apply) return Preview(new { from, plan, action = "migrate legacy config" });

        var svc = TryResolveInterface("ClassExland.Features.Duty.Migration.IDutyMigrationService");
        var result = await TryInvokeAsync(svc, "MigrateAsync", new object?[] { from, plan }, ct).ConfigureAwait(false);
        return result is null
            ? Fail(ExitCodes.IpcUnavailable, "迁移服务不可用（Migration 模块未提供 IDutyMigrationService），未做更改")
            : Ok(new { from, plan, detail = result?.ToString() });
    }

    private async Task<CommandResult> WriteWebStartAsync(CommandRequest r, CancellationToken ct)
    {
        var port = GetArgInt(r, "port", 18765);
        if (!r.Apply) return Preview(new { port, action = "start loopback web page" });
        var web = WebHost;
        if (web is null) return Fail(ExitCodes.IpcUnavailable, "WebHost 未注册，无法启动管理页");
        var url = await web.StartAsync(port, ct).ConfigureAwait(false);
        return Ok(new { url, port = web.Port, note = "仅 127.0.0.1，需令牌访问" });
    }

    private Task<CommandResult> WriteWebStopAsync(CommandRequest r, CancellationToken ct)
    {
        if (!r.Apply) return Task.FromResult(Preview(new { action = "stop loopback web page" }));
        var web = WebHost;
        if (web is null) return Task.FromResult(Fail(ExitCodes.IpcUnavailable, "WebHost 未注册"));
        web.Stop();
        return Task.FromResult(Ok(new { stopped = true }));
    }

    private async Task<CommandResult> WriteClassSetAsync(CommandRequest r, CancellationToken ct)
    {
        var to = GetArg(r, "to");
        if (!Enum.TryParse<VirtualTimeState>(to, ignoreCase: true, out var state))
            return Fail(ExitCodes.Validation, "--to 必须是 None/PrepareOnClass/OnClass/Breaking/AfterSchool");
        if (!r.Apply) return Preview(new { to = state.ToString(), action = "force class state (dangerous)" });

        // 查找 BellModule 暴露的状态设置入口（反射弱依赖）
        var bell = TryService<IBellTimeline>();
        object? target = bell;
        if (target is null) target = TryResolveInterface("ClassExland.Features.Bell.IBellStateController");
        if (target is null)
            return Fail(ExitCodes.IpcUnavailable, "找不到状态控制器（BellModule 未提供可写接口），未更改状态");

        var invoked = await TryInvokeAsync(target, "SetVirtualStateAsync", new object?[] { state }, ct).ConfigureAwait(false);
        if (invoked is null)
            invoked = await TryInvokeAsync(target, "SetVirtualState", new object?[] { state }, ct).ConfigureAwait(false);
        return invoked is null
            ? Fail(ExitCodes.IpcUnavailable, "状态控制器无可用设置方法，未更改状态")
            : Ok(new { state = state.ToString(), forced = true });
    }

    // ---------- 反射 / JSON 工具 ----------

    /// <summary>
    /// 反射调用服务方法。容忍尾部的 CancellationToken / 可选参数（签名如 Foo(string, CancellationToken = default)），
    /// 非泛型 Task 成功完成返回哨兵 true，避免调用方把成功误判为「服务不可用」。
    /// 返回 null 仅表示「找不到匹配方法或调用异常」。
    /// </summary>
    private static async Task<object?> TryInvokeAsync(object? service, string methodName, object?[] args, CancellationToken ct)
    {
        if (service is null) return null;
        try
        {
            var methods = service.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));
            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                // 参数个数：实际参数须能被方法容纳（多余形参必须是 CancellationToken 或可选参数）
                if (ps.Length < args.Length) continue;
                var callArgs = new object?[ps.Length];
                var ok = true;
                for (var i = 0; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType == typeof(CancellationToken)) callArgs[i] = ct;
                    else if (i < args.Length)
                    {
                        if (args[i] is null || ps[i].ParameterType.IsInstanceOfType(args[i])) callArgs[i] = args[i];
                        else { ok = false; break; }
                    }
                    else if (ps[i].HasDefaultValue) callArgs[i] = ps[i].DefaultValue;
                    else { ok = false; break; }
                }
                if (!ok) continue;

                var invoked = m.Invoke(service, callArgs);
                if (invoked is Task t)
                {
                    await t.ConfigureAwait(false);
                    // Task<T> 有 Result；非泛型 Task 没有，成功完成用哨兵 true 表示
                    var rp = t.GetType().GetProperty("Result");
                    return rp is null ? true : rp.GetValue(t);
                }
                return invoked ?? true; // void 方法（Invoke 返回 null）视为成功
            }
        }
        catch { /* 反射调用失败按不可用处理 */ }
        return null;
    }

    /// <summary>
    /// 定位配置文件中的数组，但保留完整根节点，写回时必须写 Root 而非 Array，
    /// 否则根对象上的其它字段（如 activities.json 的 enableAdvanceNotice）会丢失。
    /// </summary>
    private static (JsonNode Root, JsonArray Array) LoadRootAndArray(string? raw, params string[] names)
    {
        JsonNode root;
        try { root = string.IsNullOrWhiteSpace(raw) ? new JsonObject() : (JsonNode.Parse(raw) ?? new JsonObject()); }
        catch { root = new JsonObject(); }

        if (root is JsonArray topArray) return (root, topArray);

        var obj = root as JsonObject ?? new JsonObject();
        var candidates = names.Length > 0 ? names : new[] { "items", "slots", "bells", "plans", "activities" };
        foreach (var name in candidates)
            if (obj[name] is JsonArray found) return (root, found);

        var created = new JsonArray();
        obj[candidates[0]] = created;
        return (root, created);
    }

    private int CurrentRotationIndex()
    {
        try
        {
            var root = JsonNode.Parse(_store.ReadRaw(ConfigFiles.Duty) ?? "{}") as JsonObject;
            // 根级 activePlanIndex，与 DutySettings 模型一致
            return root?["activePlanIndex"]?.GetValue<int>() ?? 0;
        }
        catch { return 0; }
    }

    private static bool MatchSlot(JsonObject obj, string slot)
    {
        foreach (var key in new[] { "id", "slotId", "timeLayoutItemId", "slot", "label", "name" })
            if (obj.TryGetPropertyValue(key, out var v) && string.Equals(v?.ToString(), slot, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static JsonNode? ParseJsonValue(string text)
    {
        try { return JsonNode.Parse(text); } catch { return JsonValue.Create(text); }
    }

    /// <summary>将 --periods 入参（JSON 数组或逗号分隔）解析为 HH:mm 字符串数组，对齐 SpecialActivity.ReminderTimes。</summary>
    private static JsonArray ParseReminderTimes(string? input)
    {
        var result = new JsonArray();
        if (string.IsNullOrWhiteSpace(input)) return result;

        var text = input.Trim();
        List<string?> values = new();
        if (text.StartsWith('['))
        {
            try
            {
                var node = JsonNode.Parse(text);
                if (node is JsonArray arr)
                {
                    values = arr.Select(x => x?.ToString()).ToList();
                }
            }
            catch { /* 非法 JSON 则按逗号分隔处理 */ }
        }
        if (values.Count == 0)
            values = text.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => (string?)v).ToList();

        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v))
                result.Add(NormalizeHhMm(v));
        return result;
    }

    private static string NormalizeHhMm(string value)
    {
        var s = value.Trim().Replace('：', ':');
        var parts = s.Split(':');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
            return $"{Math.Clamp(h, 0, 23):00}:{Math.Clamp(m, 0, 59):00}";
        return s;
    }

    private static void SetJsonPath(JsonNode root, string path, string value)
    {
        var segments = path.Split('.');
        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (current is JsonObject o)
            {
                if (o[seg] is null)
                {
                    JsonNode placeholder = int.TryParse(segments[i + 1], out _) ? new JsonArray() : new JsonObject();
                    o[seg] = placeholder;
                }
                current = o[seg]!;
            }
            else if (current is JsonArray a && int.TryParse(seg, out var idx))
            {
                while (a.Count <= idx) a.Add(new JsonObject());
                current = a[idx]!;
            }
            else throw new CommandAbortedException(ExitCodes.Validation, $"路径段无法导航：{seg}");
        }

        var last = segments[^1];
        var val = ParseJsonValue(value);
        if (current is JsonObject obj) obj[last] = val;
        else if (current is JsonArray arr && int.TryParse(last, out var li))
        {
            while (arr.Count <= li) arr.Add(null);
            arr[li] = val;
        }
        else throw new CommandAbortedException(ExitCodes.Validation, "路径终点无法写入");
    }
}
