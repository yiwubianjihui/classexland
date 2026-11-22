using ClassExland.Features.Lucky;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// 用本机真实的 LuckyPicker 历史文件做一次端到端校验（文件不存在时自动跳过）。
/// 只读，不修改用户的任何数据。
/// </summary>
public class LuckyHistoryRealFileTests
{
    private static string? RealHistoryPath
    {
        get
        {
            var p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LuckyPicker", "history.json");
            return File.Exists(p) ? p : null;
        }
    }

    [Fact]
    public void 真实历史文件能被解析且人名可提取()
    {
        var path = RealHistoryPath;
        if (path is null) return; // 本机未装 LuckyPicker

        var entries = LuckyHistory.Parse(File.ReadAllText(path));
        Assert.NotNull(entries);
        Assert.NotEmpty(entries!);

        // 每条记录都应能提取出至少一个人名（连抽/单抽两种格式）
        var withNames = entries.Where(e => LuckyHistory.ExtractNames(e.Text).Count > 0).ToList();
        Assert.NotEmpty(withNames);

        // 指纹两两不同（同一条记录不会重复入"已见集合"）
        var fingerprints = entries.Select(LuckyHistory.Fingerprint).ToList();
        Assert.Equal(fingerprints.Count, fingerprints.Distinct().Count());
    }

    [Fact]
    public void 模拟新抽选只广播新增的那条()
    {
        var path = RealHistoryPath;
        if (path is null) return;

        var entries = LuckyHistory.Parse(File.ReadAllText(path))!;
        var known = entries.Select(LuckyHistory.Fingerprint).ToHashSet(StringComparer.Ordinal);

        // 模拟 LuckyPicker 抽到新人：新记录插在数组最前
        var appended = new List<LuckyHistoryEntry>
        {
            new() { Time = "2099-01-01 00:00:00", Text = "测试同学A", ClassId = "19" }
        };
        appended.AddRange(entries);

        var fresh = LuckyHistory.FindNew(appended, known, skipAllWhenEmpty: true);

        var only = Assert.Single(fresh);
        Assert.Equal("测试同学A", only.Text);
        Assert.Equal(new[] { "测试同学A" }, LuckyHistory.ExtractNames(only.Text));
    }
}
