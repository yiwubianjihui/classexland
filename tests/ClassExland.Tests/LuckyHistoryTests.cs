using ClassExland.Features.Lucky;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// LuckyPicker 联动的纯逻辑：历史解析、增量判定、人名提取。
///
/// 格式依据 LuckyPicker 源码（desktop/History.cs 与 wpf/Core.cs）：
/// history.json 是 `[{"time","text","classId"}]`，新记录插在数组最前。
/// </summary>
public class LuckyHistoryTests
{
    private static string Json(params (string time, string text)[] rows)
    {
        var items = rows.Select(r =>
            $"{{\"time\":\"{r.time}\",\"text\":\"{r.text}\",\"classId\":\"19\"}}");
        return "[" + string.Join(",", items) + "]";
    }

    [Fact]
    public void 能解析真实格式()
    {
        var list = LuckyHistory.Parse(Json(
            ("2026-11-08 00:45:04", "尹熙彭"),
            ("2026-10-30 21:57:40", "连抽： 丛煜函、王鹤予、魏辛娃")));

        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
        Assert.Equal("尹熙彭", list[0].Text);
        Assert.Equal("19", list[0].ClassId);
    }

    [Fact]
    public void 空内容返回空列表而不是null()
    {
        Assert.Empty(LuckyHistory.Parse("")!);
        Assert.Empty(LuckyHistory.Parse("[]")!);
    }

    [Fact]
    public void 坏JSON返回null_不能当成没有记录()
    {
        // 返回 null 表示"看不懂"，调用方应跳过这一拍而不是清空已见集合。
        Assert.Null(LuckyHistory.Parse("{ not json"));
    }

    [Theory]
    [InlineData("张三", new[] { "张三" })]
    [InlineData("连抽： 张三、李四、王五", new[] { "张三", "李四", "王五" })]
    [InlineData("连抽: 张三,李四", new[] { "张三", "李四" })]
    public void 能拆出人名(string text, string[] expected)
    {
        Assert.Equal(expected, LuckyHistory.ExtractNames(text));
    }

    [Fact]
    public void 提取结果为空的输入返回空集合()
    {
        Assert.Empty(LuckyHistory.ExtractNames(null));
        Assert.Empty(LuckyHistory.ExtractNames("   "));
        Assert.Empty(LuckyHistory.ExtractNames("连抽："));
    }

    [Fact]
    public void 首次启动且有历史时不广播旧记录()
    {
        var list = LuckyHistory.Parse(Json(("2026-11-08 00:45:04", "尹熙彭")))!;
        var known = new HashSet<string>(StringComparer.Ordinal);

        var fresh = LuckyHistory.FindNew(list, known, skipAllWhenEmpty: true);

        Assert.Empty(fresh);
    }

    [Fact]
    public void 新抽选会被识别并按时间正序返回()
    {
        var list = LuckyHistory.Parse(Json(
            ("2026-11-22 21:00:02", "李四"),
            ("2026-11-22 21:00:00", "张三")))!;
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            LuckyHistory.Fingerprint(list[1])
        };

        var fresh = LuckyHistory.FindNew(list, known, skipAllWhenEmpty: true);

        var only = Assert.Single(fresh);
        Assert.Equal("李四", only.Text);
    }

    [Fact]
    public void 两条新记录按时间正序广播()
    {
        // 数组是"新的在前"，广播应当先旧后新，否则听起来顺序颠倒。
        var list = LuckyHistory.Parse(Json(
            ("2026-11-22 21:00:02", "李四"),
            ("2026-11-22 21:00:00", "张三")))!;
        var known = new HashSet<string>(StringComparer.Ordinal);

        var fresh = LuckyHistory.FindNew(list, known, skipAllWhenEmpty: false);

        Assert.Equal(new[] { "张三", "李四" }, fresh.Select(x => x.Text));
    }

    [Fact]
    public void 同时间同内容的记录指纹一致_不会重复广播()
    {
        var a = new LuckyHistoryEntry { Time = "2026-11-22 21:00:00", Text = "张三", ClassId = "19" };
        var b = new LuckyHistoryEntry { Time = "2026-11-22 21:00:00", Text = "张三", ClassId = "19" };
        Assert.Equal(LuckyHistory.Fingerprint(a), LuckyHistory.Fingerprint(b));
    }

    [Fact]
    public void 配置默认关闭并给出一致的默认值()
    {
        var cfg = LuckyConfig.Sanitize(null);
        Assert.False(cfg.Enabled);            // 默认不读别人的文件
        Assert.Equal(2, cfg.PollIntervalSeconds);
        // 遮罩显示人名、正文才是「抽到了……」这句话
        Assert.Equal("抽到了：{names}", cfg.OverlayTemplate);
        Assert.False(string.IsNullOrEmpty(cfg.MaskLeftIcon));
        Assert.True(cfg.SkipExistingOnStart);
    }

    [Fact]
    public void 正文模板可自定义()
    {
        var cfg = LuckyConfig.Sanitize(new LuckyConfig { OverlayTemplate = "恭喜 {names} 被抽中！" });
        Assert.Equal("恭喜 {names} 被抽中！", cfg.OverlayTemplate);
    }

    [Fact]
    public void 正文模板留空时回落到默认()
    {
        var cfg = LuckyConfig.Sanitize(new LuckyConfig { OverlayTemplate = "   " });
        Assert.Equal("抽到了：{names}", cfg.OverlayTemplate);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(9999, 2)]
    [InlineData(5, 5)]
    public void 轮询间隔收敛到合理范围(int raw, int expected)
    {
        var cfg = LuckyConfig.Sanitize(new LuckyConfig { PollIntervalSeconds = raw });
        Assert.Equal(expected, cfg.PollIntervalSeconds);
    }

    [Fact]
    public void 未指定路径时用LuckyPicker的默认位置()
    {
        var cfg = LuckyConfig.Sanitize(new LuckyConfig { HistoryPath = "" });
        var path = cfg.ResolveHistoryPath();

        Assert.Contains("LuckyPicker", path);
        Assert.EndsWith("history.json", path);
    }

    [Fact]
    public void 手工指定的路径优先()
    {
        var cfg = LuckyConfig.Sanitize(new LuckyConfig { HistoryPath = @"D:\tmp\h.json" });
        Assert.Equal(@"D:\tmp\h.json", cfg.ResolveHistoryPath());
    }
    [Fact]
    public void 横幅默认不是完整时长_避免遮挡主界面()
    {
        // 宿主 MainWindowLine.PreProcessNotificationContent 会无条件解引用 MaskContent，
        // 传 null 会直接 NullReferenceException（1.0.0.14 的崩溃事故就是这么来的），
        // 所以横幅无法完全不发。这里锁住"默认只闪现约 1 秒"这一约定。
        var cfg = LuckyConfig.Sanitize(null);
        Assert.False(cfg.EnableMask);
        Assert.True(cfg.MaskDurationSeconds > 0);
    }
}
