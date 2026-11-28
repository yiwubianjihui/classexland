using System.Text.Json.Serialization;

namespace ClassExland.Features.Bell;

/// <summary>
/// 「按课间时长自动算提前量」的一条规则：课间至少 <see cref="BreakMinutes"/> 分钟时，
/// 预备铃在上课前 <see cref="PrepSeconds"/> 秒响。
/// </summary>
public sealed class BellBreakPrepRule
{
    [JsonPropertyName("breakMinutes")]
    public int BreakMinutes { get; set; }

    [JsonPropertyName("prepSeconds")]
    public int PrepSeconds { get; set; }

    /// <summary>默认规则：10 分钟课间提前 2 分钟，15 分钟课间提前 3 分钟。</summary>
    public static IReadOnlyList<BellBreakPrepRule> Defaults { get; } = new[]
    {
        new BellBreakPrepRule { BreakMinutes = 10, PrepSeconds = 120 },
        new BellBreakPrepRule { BreakMinutes = 15, PrepSeconds = 180 }
    };
}

/// <summary>
/// 动态提前量的纯逻辑解析（不依赖宿主类型，可单测）。
/// </summary>
public static class BellBreakPrepRules
{
    /// <summary>
    /// 按课间时长匹配规则：取「课间分钟数 ≥ 阈值」中阈值最大的一条的提前量。
    /// 没有规则、课间过短（低于最小阈值）或时长未知时返回 <see cref="BellSettings.NoPrep"/>——
    /// 对应「没有课间就不触发预备铃」。
    /// </summary>
    public static int Resolve(
        TimeSpan? breakLength,
        IReadOnlyList<BellBreakPrepRule>? rules)
    {
        if (breakLength is null) return BellSettings.NoPrep;
        var minutes = breakLength.Value.TotalMinutes;
        if (rules is null || rules.Count == 0) return BellSettings.NoPrep;

        BellBreakPrepRule? best = null;
        foreach (var rule in rules)
        {
            if (rule.BreakMinutes <= 0 || rule.PrepSeconds < 0) continue;
            if (minutes + 0.001 < rule.BreakMinutes) continue; // 容差，避免 599999ms 被当成 9.99 分钟
            if (best is null || rule.BreakMinutes > best.BreakMinutes) best = rule;
        }
        return best?.PrepSeconds ?? BellSettings.NoPrep;
    }
}
