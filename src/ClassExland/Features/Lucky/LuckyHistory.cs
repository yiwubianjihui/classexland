using System.Text.Json;

namespace ClassExland.Features.Lucky;

/// <summary>
/// LuckyPicker 抽选记录的解析与「哪条是新的」判定。纯逻辑，方便单测。
///
/// LuckyPicker 每次抽选都会把新记录**插到数组最前面**（desktop/History.cs 的 SaveLocked），
/// 所以增量判断不能只看长度：用户可能清空历史，也可能两条记录时间戳相同（连抽）。
/// 这里用「文件哈希 + 已见记录指纹」双保险。
/// </summary>
public static class LuckyHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>解析 history.json；格式不认识时返回 null（不要当成"没有记录"）。</summary>
    public static List<LuckyHistoryEntry>? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<LuckyHistoryEntry>();
        try
        {
            var list = JsonSerializer.Deserialize<List<LuckyHistoryEntry>>(raw, JsonOptions);
            return list?.Where(e => e is not null).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>一条记录的去重指纹：时间 + 内容 + 班级。</summary>
    public static string Fingerprint(LuckyHistoryEntry entry)
        => $"{entry.Time}\u0001{entry.Text}\u0001{entry.ClassId}";

    /// <summary>连抽记录形如「连抽： A、B、C」，把其中的名字拆出来。</summary>
    public static IReadOnlyList<string> ExtractNames(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var body = text.Trim();
        var colon = body.IndexOfAny(new[] { '：', ':' });
        if (colon >= 0) body = body[(colon + 1)..];

        return body
            .Split(new[] { '、', ',', '，', '/', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();
    }

    /// <summary>把人名列表拼成广播文本。</summary>
    public static string FormatNames(IReadOnlyList<string> names)
        => names.Count == 0 ? "" : string.Join("、", names);

    /// <summary>
    /// 找出比 <paramref name="known"/> 更新的记录（按原顺序，越靠前越新）。
    /// 返回空表示没有新记录；<paramref name="known"/> 为空时不返回任何历史（首次启动不刷屏）。
    /// </summary>
    public static IReadOnlyList<LuckyHistoryEntry> FindNew(
        IReadOnlyList<LuckyHistoryEntry> current,
        ISet<string> known,
        bool skipAllWhenEmpty)
    {
        if (skipAllWhenEmpty && known.Count == 0) return Array.Empty<LuckyHistoryEntry>();

        var result = new List<LuckyHistoryEntry>();
        foreach (var entry in current)
        {
            var fp = Fingerprint(entry);
            if (!known.Contains(fp)) result.Add(entry);
        }
        // 按时间正序广播，连抽时不会出现"先新后旧"的播报顺序。
        result.Reverse();
        return result;
    }
}
