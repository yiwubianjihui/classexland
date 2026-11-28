using System.Text.Json;
using ClassExland.Contracts;

namespace ClassExland.Features.Remote;

public sealed class CoreApplyOutcome
{
    public bool Ok { get; set; }
    /// <summary>Settings.json 经宿主服务热生效（不产生 ApplyFileResult），据此决定是否通知重载。</summary>
    public bool HotApplied { get; set; }
    public List<ApplyFileResult> Results { get; } = new();
    public List<string> RolledBackFiles { get; } = new();
    public string Message { get; set; } = "";
}

/// <summary>
/// 应用 ClassIsland 核心配置快照（Settings.json + Profiles + ComponentLayouts）。
/// 严格走 备份 → diff → 校验 → 原子写；文件在快照生成后被改动（哈希不符）则保留本地并记录，
/// 任何写入失败都整批回滚。diff 生成失败中止本项覆盖并写审计。
/// </summary>
public sealed class CoreConfigWriter
{
    private readonly SafeConfigWriter _writer;
    private readonly CorePaths _paths;
    private readonly RemoteAuditLogger _audit;
    private string _version = "";

    public CoreConfigWriter(SafeConfigWriter writer, CorePaths paths, RemoteAuditLogger audit)
    {
        _writer = writer;
        _paths = paths;
        _audit = audit;
    }

    public CoreApplyOutcome Apply(CoreConfigSnapshot? snapshot, string version = "", CancellationToken ct = default)
    {
        _version = version;
        var outcome = new CoreApplyOutcome();
        if (snapshot is null)
        {
            outcome.Message = "快照不含核心配置";
            outcome.Ok = true;
            return outcome;
        }

        if (!_paths.IsValid)
        {
            outcome.Message = "未找到 ClassIsland data 目录，跳过核心配置同步";
            _audit.Log("core-skip", outcome.Message, ok: false);
            outcome.Ok = true; // 跳过不是失败，保持整体流程可继续。
            return outcome;
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            // Settings.json：优先宿主 SettingsService（内存 + SaveSettings）；
            // 走不通时才回退到文件原子写，并记录为特殊审计事件。
            if (snapshot.SettingsJson.ValueKind != JsonValueKind.Undefined &&
                snapshot.SettingsJson.ValueKind != JsonValueKind.Null)
            {
                var remoteSettings = snapshot.SettingsJson.GetRawText();
                var settingsPath = _paths.SettingsPath;
                var baseHash = snapshot.SettingsBaseHash;
                var localHash = File.Exists(settingsPath) ? SafeConfigWriter.ComputeHash(settingsPath) : "";

                // C4：覆盖前 diff 备份。热生效路径同样先做，失败必须中止覆盖。
                if (!ConfigDiffBackup.WriteBeforeOverwrite(
                        _writer.BackupDirectory, _audit, _version, settingsPath, remoteSettings))
                {
                    outcome.Results.Add(new ApplyFileResult
                    {
                        Ok = false,
                        Path = settingsPath,
                        Message = "Settings.json diff 备份失败，已中止覆盖"
                    });
                }
                // C6：热生效路径此前绕过 ApplyOne 的冲突检测，这里显式补上：
                // 本地哈希不符时保留本地文件，绝不走反射写内存。
                else if (!string.IsNullOrEmpty(baseHash) && File.Exists(settingsPath) &&
                         !string.Equals(localHash, baseHash, StringComparison.OrdinalIgnoreCase))
                {
                    outcome.Results.Add(new ApplyFileResult
                    {
                        Ok = false,
                        ConflictKeptLocal = true,
                        Path = settingsPath,
                        Message = "Settings.json 本地已变更，保留本地（热生效路径跳过）"
                    });
                    _audit.Log("core-conflict",
                        $"Settings.json 热生效路径哈希不符，保留本地（base={baseHash} local={localHash}）",
                        ok: false);
                }
                else if (SettingsServiceBridge.TryApply(remoteSettings, _audit, out var serviceMessage))
                {
                    outcome.HotApplied = true;
                    _audit.Log("settings-via-service", serviceMessage);
                    outcome.Message = serviceMessage;
                }
                else
                {
                    _audit.Log("settings-via-file-fallback", serviceMessage, ok: false);
                    var local = File.Exists(settingsPath)
                        ? File.ReadAllText(settingsPath)
                        : "{}";
                    // 仅合并白名单顶层键，其余本地键保持原样。diff 已在本分支之前写过。
                    var merged = CoreConfigPolicy.MergeAllowed(local, remoteSettings);
                    ApplyOne(outcome, settingsPath, merged, baseHash, "Settings.json", diffAlreadyWritten: true);
                }
            }

            // Profiles
            foreach (var pair in snapshot.Profiles)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = pair.Key;
                if (!IsSafeFileName(fileName))
                {
                    _audit.Log("core-conflict", $"拒绝非法档案名：{fileName}", ok: false);
                    continue;
                }
                if (pair.Value is null) continue;
                var profileKind = pair.Value.Json.ValueKind;
                if (profileKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;

                ApplyOne(
                    outcome,
                    Path.Combine(_paths.ProfilesDirectory, fileName),
                    pair.Value.Json.GetRawText(),
                    pair.Value.BaseHash,
                    fileName);
            }

            // ComponentLayouts（与 profiles 同结构：json + baseHash）
            foreach (var pair in snapshot.Layouts)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = pair.Key;
                if (!IsSafeFileName(fileName))
                {
                    _audit.Log("core-conflict", $"拒绝非法布局名：{fileName}", ok: false);
                    continue;
                }
                if (pair.Value is null) continue;
                var layoutKind = pair.Value.Json.ValueKind;
                if (layoutKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;

                ApplyOne(
                    outcome,
                    Path.Combine(_paths.ComponentLayoutsDirectory, fileName),
                    pair.Value.Json.GetRawText(),
                    pair.Value.BaseHash,
                    $"ComponentLayouts/{fileName}");
            }

            var failed = outcome.Results.FirstOrDefault(r => !r.Ok && !r.ConflictKeptLocal);
            if (failed is not null)
            {
                outcome.RolledBackFiles.AddRange(Rollback(outcome.Results));
                outcome.Ok = false;
                outcome.Message = $"核心配置写入失败：{failed.Message}";
                _audit.Log("core-rollback", outcome.Message, ok: false);
                return outcome;
            }

            outcome.Ok = true;
            outcome.Message = $"核心配置已应用 {outcome.Results.Count(r => r.Ok)} 项，保留本地 {outcome.Results.Count(r => r.ConflictKeptLocal)} 项";
            _audit.Log("core-apply", outcome.Message);
            return outcome;
        }
        catch (Exception ex)
        {
            outcome.RolledBackFiles.AddRange(Rollback(outcome.Results));
            outcome.Ok = false;
            outcome.Message = $"核心配置同步异常：{ex.GetType().Name}: {ex.Message}";
            _audit.Log("core-exception", outcome.Message, ok: false);
            return outcome;
        }
    }

    private void ApplyOne(CoreApplyOutcome outcome, string path, string content, string baseHash,
                          string label, bool diffAlreadyWritten = false)
    {
        try
        {
            // C4：覆盖前的 diff 备份；失败即中止，不触碰目标文件。
            if (!diffAlreadyWritten &&
                !ConfigDiffBackup.WriteBeforeOverwrite(_writer.BackupDirectory, _audit, _version, path, content))
            {
                outcome.Results.Add(new ApplyFileResult
                {
                    Ok = false,
                    Path = path,
                    Message = $"{label} diff 备份失败，已中止覆盖"
                });
                return;
            }

            if (!string.IsNullOrEmpty(baseHash) && File.Exists(path))
            {
                var localHash = SafeConfigWriter.ComputeHash(path);
                if (!string.Equals(localHash, baseHash, StringComparison.OrdinalIgnoreCase))
                {
                    // 冲突：本地已被改动，保留本地并记录，不覆盖。
                    outcome.Results.Add(new ApplyFileResult
                    {
                        Ok = false,
                        ConflictKeptLocal = true,
                        Path = path,
                        Message = $"{label} 本地已变更，保留本地"
                    });
                    _audit.Log("core-conflict", $"{label} 哈希不符，保留本地（base={baseHash} local={localHash}）", ok: false);
                    return;
                }
            }

            var result = _writer.ApplyFile(path, content, validateJson: true);
            outcome.Results.Add(result);
            _audit.Log("core-file", $"{label}: {result.Message}", ok: result.Ok);
        }
        catch (Exception ex)
        {
            outcome.Results.Add(new ApplyFileResult
            {
                Path = path,
                Message = $"{ex.GetType().Name}: {ex.Message}"
            });
        }
    }

    private List<string> Rollback(IEnumerable<ApplyFileResult> applied)
    {
        var list = applied.ToList();
        var rolled = list.Where(r => r.Ok && !string.IsNullOrEmpty(r.BackupPath)).Select(r => r.Path).ToList();
        _writer.RollbackAll(list.Where(r => r.Ok));
        return rolled;
    }

    private static bool IsSafeFileName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name == Path.GetFileName(name)
           && name.IndexOf("..", StringComparison.Ordinal) < 0
           && Path.GetExtension(name).Equals(".json", StringComparison.OrdinalIgnoreCase);
}
