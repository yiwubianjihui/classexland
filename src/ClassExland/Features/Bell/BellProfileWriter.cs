using System.Text.Json;
using System.Text.Json.Nodes;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Bell;

/// <summary>
/// 档案写入器：先预览、再显式确认写入、随时回滚。
/// 只写我方 GUID 的附加设置与宿主内置提醒的两个布尔字段，其他字段一律不动。
/// </summary>
public sealed class BellProfileWriter
{
    private readonly IProfileService _profileService;
    private readonly BellSettings _settings;
    private readonly ILogger<BellProfileWriter> _logger;
    private readonly List<BellProfileChange> _lastPreview = new();
    private readonly List<BellProfileRollbackEntry> _lastRollback = new();
    private bool _applied;

    public BellProfileWriter(IProfileService profileService, BellSettings settings, ILogger<BellProfileWriter> logger)
    {
        _profileService = profileService;
        _settings = settings;
        _logger = logger;
    }

    public bool HasApplied => _applied;

    /// <summary>生成预览，不写任何文件。</summary>
    public IReadOnlyList<BellProfileChange> BuildPreview()
    {
        _lastPreview.Clear();
        try
        {
            var profile = _profileService.Profile;
            if (profile?.TimeLayouts == null) return _lastPreview;

            foreach (var layoutKvp in profile.TimeLayouts)
            {
                if (layoutKvp.Value?.Layouts == null) continue;
                foreach (var slot in layoutKvp.Value.Layouts)
                {
                    if (slot.TimeType != 0) continue; // 只对上课段生效

                    var key = MakeSlotKey(layoutKvp.Key, slot);
                    var our = slot.GetAttachedObject<BellSlotSettings>(BellConstants.SlotAttachedGuid);
                    var oldPrep = our is { IsAttachSettingsEnabled: true }
                        ? our.PrepSeconds
                        : GetFallbackPrep(key);
                    var newPrep = GetFallbackPrep(key);

                    var willDisable = false;
                    if (slot.AttachedObjects.TryGetValue(BellConstants.HostNotificationAttachedGuid, out var host))
                    {
                        willDisable = TryGetBool(host, "IsClassOnPreparingNotificationEnabled", out var a) && a
                                      || TryGetBool(host, "IsClassOnNotificationEnabled", out var b) && b;
                    }

                    _lastPreview.Add(new BellProfileChange(
                        layoutKvp.Key,
                        key,
                        $"{layoutKvp.Value.Name} {slot.StartTime:hh\\:mm}-{slot.EndTime:hh\\:mm}",
                        oldPrep,
                        newPrep,
                        willDisable));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生成预备铃档案预览失败。");
        }
        return _lastPreview;
    }

    /// <summary>
    /// 按预览写入档案。仅写我方字段，其他字段保持原样；写前保存快照以供回滚。
    /// 不再关闭宿主的正式上课/预备提醒开关——正式上课提示交还宿主原生，插件只管上课前。
    /// </summary>
    public bool Apply() => ApplyCore(disableHostNotifications: false);

    /// <summary>
    /// 只把每节课的提前量写进档案（我方附加设置），不改宿主自带的提醒开关。
    /// 设置页"应用每节课设置"与 CLI bell set 走这条路径。
    /// </summary>
    /// <param name="removeSlotKeys">
    /// 需要"恢复默认"的节次：把这些节的附加设置从档案里删掉，而不是写成默认值。
    /// 设置页里取消勾选「单独设置」走这条路径；为 null 时保持原样（CLI 只改一节时用）。
    /// </param>
    public bool ApplySlotPreps(IReadOnlyCollection<string>? removeSlotKeys = null)
        => ApplyCore(disableHostNotifications: false, rebuildPreview: true, removeSlotKeys);

    private bool ApplyCore(bool disableHostNotifications, bool rebuildPreview = false,
        IReadOnlyCollection<string>? removeSlotKeys = null)
    {
        if (rebuildPreview || _lastPreview.Count == 0) BuildPreview();
        _lastRollback.Clear();
        try
        {
            var profile = _profileService.Profile;
            if (profile?.TimeLayouts == null) return false;

            foreach (var change in _lastPreview)
            {
                if (!profile.TimeLayouts.TryGetValue(change.TimeLayoutId, out var layout) || layout?.Layouts == null)
                    continue;

                var slot = layout.Layouts.FirstOrDefault(s => MakeSlotKey(change.TimeLayoutId, s) == change.SlotKey);
                if (slot == null) continue;

                var hadOur = slot.AttachedObjects.TryGetValue(BellConstants.SlotAttachedGuid, out var ourOld);
                var hadHost = slot.AttachedObjects.TryGetValue(BellConstants.HostNotificationAttachedGuid, out var hostOld);

                // 取消勾选 = 恢复默认：直接删掉附加设置，不能写成默认值——
                // 否则表里看着是"未勾选"，档案里却留着一个固定值，以后改默认值它不会跟着变。
                if (removeSlotKeys is not null && removeSlotKeys.Contains(change.SlotKey))
                {
                    if (!hadOur) continue;

                    _lastRollback.Add(new BellProfileRollbackEntry(
                        change.TimeLayoutId, change.SlotKey, hadOur, ourOld, hadHost, hostOld,
                        change.OldPrepSeconds, _settings.HasPrepOverride(change.SlotKey)));

                    slot.AttachedObjects.Remove(BellConstants.SlotAttachedGuid);
                    _settings.RemovePrep(change.SlotKey);
                    continue;
                }

                // 值没变、又不需要处理宿主提醒时跳过，避免把整份档案都写上我方附加设置。
                var needWrite = !(hadOur && ourOld is BellSlotSettings oldValue &&
                                  oldValue.PrepSeconds == change.NewPrepSeconds);
                var needHostFix = disableHostNotifications && hadHost && hostOld != null;
                if (!needWrite && !needHostFix) continue;

                var hadPrepOverride = _settings.HasPrepOverride(change.SlotKey);
                _lastRollback.Add(new BellProfileRollbackEntry(
                    change.TimeLayoutId, change.SlotKey, hadOur, ourOld, hadHost, hostOld,
                    change.OldPrepSeconds, hadPrepOverride));

                if (needWrite)
                {
                    slot.WriteAttachedObject(BellConstants.SlotAttachedGuid,
                        new BellSlotSettings { PrepSeconds = change.NewPrepSeconds });

                    // 同步写入插件自有 bell.json（兜底副本），档案读取失败时仍可工作。
                    _settings.SetPrep(change.SlotKey, change.NewPrepSeconds, change.SlotDisplay);
                }

                if (needHostFix)
                {
                    var updated = DisableBool(hostOld, "IsClassOnPreparingNotificationEnabled");
                    updated = DisableBool(updated, "IsClassOnNotificationEnabled");
                    slot.AttachedObjects[BellConstants.HostNotificationAttachedGuid] = updated;
                }
            }

            _settings.Save();
            _profileService.SaveProfile();
            _applied = true;
            _logger.LogInformation("已将 {Count} 个上课段的预备铃设置写入档案。", _lastRollback.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入预备铃档案失败。");
            return false;
        }
    }

    /// <summary>回滚到写入前的精确状态。</summary>
    public bool Rollback()
    {
        if (!_applied) return false;
        try
        {
            var profile = _profileService.Profile;
            if (profile?.TimeLayouts == null) return false;

            foreach (var entry in _lastRollback)
            {
                if (!profile.TimeLayouts.TryGetValue(entry.TimeLayoutId, out var layout) || layout?.Layouts == null)
                    continue;

                var slot = layout.Layouts.FirstOrDefault(s => MakeSlotKey(entry.TimeLayoutId, s) == entry.SlotKey);
                if (slot == null) continue;

                Restore(slot, BellConstants.SlotAttachedGuid, entry.HadOurAttached, entry.OurAttachedOld);
                Restore(slot, BellConstants.HostNotificationAttachedGuid, entry.HadHostAttached, entry.HostAttachedOld);
                if (entry.HadPrepOverride) _settings.SetPrep(entry.SlotKey, entry.OldPrepSeconds);
                else _settings.RemovePrep(entry.SlotKey);
            }

            _settings.Save();
            _profileService.SaveProfile();
            _applied = false;
            _lastRollback.Clear();
            _logger.LogInformation("已回滚预备铃档案写入。");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "回滚预备铃档案失败。");
            return false;
        }
    }

    /// <summary>生成稳定的槽位键。TimeLayoutItem 本身没有 Id，使用 档案ID+起止时间 作为稳定标识。</summary>
    public static string MakeSlotKey(Guid layoutId, TimeLayoutItem slot) =>
        $"{layoutId:D}|{slot.StartTime:hh\\:mm\\:ss}|{slot.EndTime:hh\\:mm\\:ss}";

    private int GetFallbackPrep(string slotKey) => _settings.GetPrep(slotKey);

    private static void Restore(TimeLayoutItem slot, Guid key, bool had, object? oldValue)
    {
        if (had) slot.AttachedObjects[key] = oldValue;
        else slot.AttachedObjects.Remove(key);
    }

    private static bool TryGetBool(object? obj, string propertyName, out bool value)
    {
        value = false;
        if (obj == null) return false;
        if (obj is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(propertyName, out var prop) &&
                prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = prop.GetBoolean();
                return true;
            }
            return false;
        }
        var pi = obj.GetType().GetProperty(propertyName);
        if (pi != null && pi.PropertyType == typeof(bool))
        {
            value = (bool)(pi.GetValue(obj) ?? false);
            return true;
        }
        return false;
    }

    private static object? DisableBool(object? obj, string propertyName)
    {
        if (obj == null) return null;
        if (obj is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return obj;
            var node = JsonNode.Parse(element.GetRawText()) as JsonObject;
            if (node == null) return obj;
            node[propertyName] = false;
            using var doc = JsonDocument.Parse(node.ToJsonString());
            return doc.RootElement.Clone();
        }
        var pi = obj.GetType().GetProperty(propertyName);
        if (pi != null && pi.CanWrite && pi.PropertyType == typeof(bool))
            pi.SetValue(obj, false);
        return obj;
    }
}
