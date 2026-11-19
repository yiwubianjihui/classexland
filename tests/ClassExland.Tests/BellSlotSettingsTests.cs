using System.Text.Json;
using ClassExland.Features.Bell;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// 每节课提前量写在档案 <c>AttachedObjects</c> 里，格式一旦变形，
/// 老用户档案里的值就会读不出来（静默回退到 120 秒）。这里固定住线上格式。
/// </summary>
public class BellSlotSettingsTests
{
    [Fact]
    public void 旧档案只有提前量也能读出并默认启用()
    {
        // 1.0.0.x 写进档案的形状：只有 prepSeconds。
        var settings = JsonSerializer.Deserialize<BellSlotSettings>("{\"prepSeconds\":90}");

        Assert.NotNull(settings);
        Assert.Equal(90, settings!.PrepSeconds);
        Assert.True(settings.IsAttachSettingsEnabled);
    }

    [Fact]
    public void 回写时保留小写提前量并带上宿主约定的启用字段()
    {
        var json = JsonSerializer.Serialize(new BellSlotSettings { PrepSeconds = 75 });
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(75, doc.RootElement.GetProperty("prepSeconds").GetInt32());
        Assert.True(doc.RootElement.GetProperty("IsAttachSettingsEnabled").GetBoolean());
    }

    [Fact]
    public void 关掉启用开关后再读回仍是关闭状态()
    {
        var text = JsonSerializer.Serialize(new BellSlotSettings { PrepSeconds = 30, IsAttachSettingsEnabled = false });
        var back = JsonSerializer.Deserialize<BellSlotSettings>(text);

        Assert.NotNull(back);
        Assert.False(back!.IsAttachSettingsEnabled);
        Assert.Equal(30, back.PrepSeconds);
    }
}
