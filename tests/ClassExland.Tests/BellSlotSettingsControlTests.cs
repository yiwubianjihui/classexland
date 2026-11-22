using System.Text.Json;
using ClassExland.Features.Bell;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// 附加设置控件的回归测试。
///
/// 背景（1.0.0.5–1.0.0.9 的实际 bug）：控件在渲染时把 IsAttachSettingsEnabled 写成 false，
/// 想让课间时段"不写回档案"。但宿主把这个字段直接绑在启用开关上
/// （Presenter.UpdateSourceSettings 读写 IAttachedSettings.IsAttachSettingsEnabled），
/// 于是用户一打开开关就被渲染改回关闭，表现为"再点该时间条目又关掉了"。
///
/// 这些测试锁住两件事：
///   1. 设置对象的默认启用状态是 true，且序列化/反序列化不会丢；
///   2. 提前量在 0–600 之间可自由读写（不再被 UI 禁用逻辑限制）。
/// </summary>
public class BellSlotSettingsControlTests
{
    [Fact]
    public void 默认启用状态为真_避免开关自己弹回关闭()
    {
        var settings = new BellSlotSettings();
        Assert.True(settings.IsAttachSettingsEnabled);
        Assert.Equal(120, settings.PrepSeconds);
    }

    [Fact]
    public void 显式开启后序列化往返仍是开启()
    {
        var json = JsonSerializer.Serialize(new BellSlotSettings
        {
            PrepSeconds = 90,
            IsAttachSettingsEnabled = true
        });

        using var doc = JsonDocument.Parse(json);
        // 提前量保持宿主约定的小写键名
        Assert.Equal(90, doc.RootElement.GetProperty("prepSeconds").GetInt32());
        // 启用状态可被读出；渲染时不允许把它改成 false
        Assert.True(doc.RootElement.GetProperty("IsAttachSettingsEnabled").GetBoolean());

        var back = JsonSerializer.Deserialize<BellSlotSettings>(json);
        Assert.NotNull(back);
        Assert.True(back!.IsAttachSettingsEnabled);
        Assert.Equal(90, back.PrepSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(600)]
    public void 提前量在允许区间内可自由设置(int seconds)
    {
        var settings = new BellSlotSettings { PrepSeconds = seconds };
        Assert.Equal(seconds, settings.PrepSeconds);
    }

    [Fact]
    public void 旧档案只有提前量时默认启用()
    {
        var settings = JsonSerializer.Deserialize<BellSlotSettings>("{\"prepSeconds\":75}");
        Assert.NotNull(settings);
        Assert.Equal(75, settings!.PrepSeconds);
        Assert.True(settings.IsAttachSettingsEnabled);
    }
    [Fact]
    public void 新建一节时默认不启用预备铃()
    {
        // 用户要求：预备铃按节启用，不是全局自动生效。
        // 但档案里"本来就没写这个字段"的旧数据要按启用处理（否则升级会静默停用），
        // 所以默认值留在 true，新建对象走 CreateDisabled()。
        var fresh = BellSlotSettings.CreateDisabled();
        Assert.False(fresh.IsAttachSettingsEnabled);

        // 反序列化旧形状（只有 prepSeconds）时沿用启用，保证历史配置不失效。
        var legacy = System.Text.Json.JsonSerializer.Deserialize<BellSlotSettings>("{\"prepSeconds\":90}");
        Assert.NotNull(legacy);
        Assert.True(legacy!.IsAttachSettingsEnabled);
    }

    [Fact]
    public void 显式关闭的节次用NoPrep表示()
    {
        Assert.Equal(-1, BellSettings.NoPrep);
    }
}
