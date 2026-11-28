using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassExland.Features.Remote;

/// <summary>
/// 远程修改 ClassIsland 核心配置的白名单/黑名单常量表与校验。
///
/// 白名单（允许远程修改）：界面布局 CurrentComponentConfig / Scale / Opacity、
/// 通知开关、ClassPrepareNotifySeconds、课表 AttachedObjects（预备铃相关）。
/// 黑名单（出现即整份拒绝）：Credentials.json、Management/Policy.json、Debug*、Update*、
/// 插件启用状态、主题私有字段。
///
/// 注意：本文件是常量表的唯一来源；文档说明由 docs-dev 维护（已同步给 team-lead）。
/// </summary>
public static class CoreConfigPolicy
{
    /// <summary>
    /// 允许远程修改的 Settings.json 顶层键（大小写不敏感，精确匹配）。
    /// ClassOnPreparingText / ClassOnMaskText / ClassOffMaskText / AttachedObjects /
    /// IsClassOnPreparingNotificationEnabled / IsClassOnNotificationEnabled 属于档案
    /// （Profile）的 AttachedObjects，不在 Settings.json 白名单内；档案本身是整份下发的，
    /// 这些字段随档案生效，不需要也不允许从 Settings.json 合并。
    /// </summary>
    public static readonly string[] AllowedSettingKeys =
    {
        "CurrentComponentConfig",
        "Scale",
        "Opacity",
        "ComponentConfigs",
        "ClassPrepareNotifySeconds",
        "IsClassPrepareNotificationEnabled",
        "IsClassOffNotificationEnabled",
        "IsSwapMode",
        "IsClassChangingNotificationEnabled",
        "SingleWeekStartTime",
        "MultiWeekRotationOffset",
        "MultiWeekRotationMaxCycle",
        "TimeLayoutEditorIndex",
        "DefaultOnClassTimePointMinutes",
        "DefaultBreakingTimePointMinutes",
        "CountdownSeconds",
        "IsNotificationEnabled",
        "ShowExtraInfoOnTimePoint"
    };

    /// <summary>禁止远程下发的文件名模式（大小写不敏感，结尾 * 表示前缀匹配）。</summary>
    public static readonly string[] ForbiddenFileNamePatterns =
    {
        "Credentials.json",
        "Policy.json",
        "Debug*",
        "Update*",
        "Plugins.json",
        "PluginSettings.json",
        "EnabledPlugins*",
        "*PluginState*",
        "ThemePrivate*"
    };

    /// <summary>禁止远程出现的 JSON 键名片段（大小写不敏感，子串匹配，递归扫描）。</summary>
    public static readonly string[] ForbiddenKeyFragments =
    {
        "credential", "password", "token", "secret", "apikey", "privatekey",
        "policy", "debug", "update",
        "pluginenabled", "enabledplugins", "ispluginenabled",
        "themeprivate", "privatetheme"
    };

    /// <summary>禁止远程下发的命令名片段。</summary>
    public static readonly string[] ForbiddenCommandFragments =
    {
        "credential", "debug", "update", "plugin.enable", "theme.private"
    };

    public sealed record PolicyResult(bool Ok, string Error)
    {
        public static PolicyResult Pass() => new(true, "");
        public static PolicyResult Fail(string error) => new(false, error);
    }

    /// <summary>整份校验：任一禁止项命中即拒绝，绝不部分应用。</summary>
    public static PolicyResult Validate(RemoteManifest manifest)
    {
        foreach (var name in manifest.Snapshot?.PluginConfigs.Keys ?? Enumerable.Empty<string>())
        {
            if (MatchesForbiddenFileName(name))
                return PolicyResult.Fail($"快照包含禁止下发的文件：{name}");
        }

        var core = manifest.Snapshot?.Core;
        if (core is not null)
        {
            if (core.SettingsJson.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                if (FindForbiddenKey(core.SettingsJson) is { } key)
                    return PolicyResult.Fail($"Settings.json 命中禁止键：{key}");
            }
            foreach (var pair in core.Profiles)
            {
                if (pair.Value is null) continue;
                var profileKind = pair.Value.Json.ValueKind;
                if (profileKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
                if (FindForbiddenKey(pair.Value.Json) is { } key)
                    return PolicyResult.Fail($"档案 {pair.Key} 命中禁止键：{key}");
            }
            foreach (var pair in core.Layouts)
            {
                if (pair.Value is null) continue;
                var layoutKind = pair.Value.Json.ValueKind;
                if (layoutKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
                if (FindForbiddenKey(pair.Value.Json) is { } key)
                    return PolicyResult.Fail($"组件布局 {pair.Key} 命中禁止键：{key}");
            }
        }

        foreach (var command in manifest.Commands)
        {
            if (ContainsAny(command.Command, ForbiddenCommandFragments))
                return PolicyResult.Fail($"指令命中禁止项：{command.Command}");
            foreach (var arg in command.Args.Keys)
            {
                if (ContainsAny(arg, ForbiddenKeyFragments))
                    return PolicyResult.Fail($"指令 {command.Command} 的参数命中禁止键：{arg}");
            }
        }

        return PolicyResult.Pass();
    }

    /// <summary>
    /// 上传方向的敏感键过滤：core 的原文（Settings / Profiles / Layouts）命中禁止键时
    /// 整份拒绝上传，绝不把 token/secret 等字段发到服务器。只读扫描，不重新序列化原文。
    /// </summary>
    public static PolicyResult ValidateOutgoingCore(string settingsJson, IEnumerable<string> otherCoreTexts)
    {
        if (!string.IsNullOrWhiteSpace(settingsJson) &&
            FindForbiddenKeyInText(settingsJson) is { } settingsKey)
            return PolicyResult.Fail($"Settings.json 命中禁止键，拒绝上传：{settingsKey}");

        foreach (var text in otherCoreTexts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (FindForbiddenKeyInText(text) is { } key)
                return PolicyResult.Fail($"核心配置命中禁止键，拒绝上传：{key}");
        }
        return PolicyResult.Pass();
    }

    private static string? FindForbiddenKeyInText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FindForbiddenKey(doc.RootElement);
        }
        catch
        {
            // 解析失败时不做猜测；服务端会按原文拒收，这里不伪装成"干净"。
            return "（JSON 解析失败）";
        }
    }

    /// <summary>
    /// 仅把白名单内的顶层键从 remote 合并进 local，其余键保持本地值不变。
    /// 解析失败时原样返回本地内容，绝不产生半截配置。
    /// </summary>
    public static string MergeAllowed(string localJson, string remoteJson)
    {
        try
        {
            if (!IsObject(localJson) || !IsObject(remoteJson)) return localJson;
            var local = JsonNode.Parse(localJson) as JsonObject;
            var remote = JsonNode.Parse(remoteJson) as JsonObject;
            if (local is null || remote is null) return localJson;

            foreach (var key in AllowedSettingKeys)
            {
                if (!remote.TryGetPropertyValue(key, out var node) || node is null) continue;
                local[key] = node.DeepClone();
            }
            return local.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return localJson;
        }
    }

    private static bool IsObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    private static string? FindForbiddenKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (ContainsAny(prop.Name, ForbiddenKeyFragments)) return prop.Name;
                    if (FindForbiddenKey(prop.Value) is { } found) return found;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (FindForbiddenKey(item) is { } found) return found;
                break;
        }
        return null;
    }

    private static bool MatchesForbiddenFileName(string name)
    {
        foreach (var pattern in ForbiddenFileNamePatterns)
        {
            if (pattern.EndsWith('*'))
            {
                if (name.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (pattern.StartsWith('*'))
            {
                if (name.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (name.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool ContainsAny(string text, IEnumerable<string> fragments)
        => fragments.Any(f => text.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
}
