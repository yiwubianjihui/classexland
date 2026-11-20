using ClassExland.Features.Duty;
using ClassExland.Features.Duty.Models;
using ClassExland.Features.Remote;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// 时段名字与设备名的纯逻辑测试。
///
/// 时段：值日表里该说「上午 / 下午」，不该把 07:00 这种边界时间当名字显示。
/// 设备名：同一个远程站要按名字区分多台教室电脑，名字会进 URL，边界必须收敛。
/// </summary>
public class DutySlotLabelsTests
{
    private static DutyPlan Plan(params (string label, string start, string end)[] slots)
    {
        var plan = new DutyPlan { Name = "测试" };
        foreach (var (label, start, end) in slots)
        {
            plan.TimeSlots.Add(new TimeSlot
            {
                Label = label,
                Start = TimeSpan.Parse(start),
                End = TimeSpan.Parse(end)
            });
        }
        return plan;
    }

    [Fact]
    public void TimeLikeLabelsBecomeCoarseNames()
    {
        var plan = Plan(
            ("07:00", "07:00", "11:50"),
            ("13:30", "13:30", "22:15"),
            ("16:25", "16:25", "17:05"));

        var changed = DutySlotLabels.Normalize(plan);

        Assert.True(changed);
        Assert.Equal(new[] { "上午", "下午", "傍晚" }, plan.TimeSlots.Select(s => s.Label));
    }

    [Fact]
    public void ItemsFollowTheirSlot()
    {
        var plan = Plan(("07:00", "07:00", "11:50"), ("16:25", "16:25", "17:05"));
        plan.Items.Add(new DutyItem { Project = "擦黑板（上）", SlotLabel = "07:00" });
        plan.Items.Add(new DutyItem { Project = "作业试写", SlotLabel = "16:25" });

        DutySlotLabels.Normalize(plan);

        Assert.Equal("上午", plan.Items[0].SlotLabel);
        Assert.Equal("傍晚", plan.Items[1].SlotLabel);
    }

    [Fact]
    public void SingleAllDaySlotIsCalledAllDay()
    {
        var plan = Plan(("08:00", "07:30", "17:30"));

        DutySlotLabels.Normalize(plan);

        Assert.Equal("全天", plan.TimeSlots[0].Label);
    }

    [Fact]
    public void AlreadyNamedSlotsAreLeftAlone()
    {
        var plan = Plan(("上午", "07:00", "11:50"));
        plan.Items.Add(new DutyItem { Project = "扫地", SlotLabel = "上午" });

        var changed = DutySlotLabels.Normalize(plan);

        Assert.False(changed);
        Assert.Equal("上午", plan.TimeSlots[0].Label);
        Assert.Equal("上午", plan.Items[0].SlotLabel);
    }

    [Fact]
    public void CollidingNamesGetASuffix()
    {
        // 两个都是下午开始的时段：第二个必须另取名，否则值日项会串台。
        var plan = Plan(("13:30", "13:30", "15:00"), ("14:10", "14:10", "15:10"));

        DutySlotLabels.Normalize(plan);

        Assert.Equal("下午", plan.TimeSlots[0].Label);
        Assert.Equal("下午2", plan.TimeSlots[1].Label);
    }

    [Theory]
    [InlineData("07:00", true)]
    [InlineData("7:00", true)]
    [InlineData("07：00", true)]
    [InlineData("上午", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeTimeDetectsClockStrings(string? label, bool expected)
    {
        Assert.Equal(expected, DutySlotLabels.LooksLikeTime(label));
    }
}

public class RemoteDeviceNameTests
{
    [Fact]
    public void ControlCharactersAndOverflowAreTrimmed()
    {
        var raw = "初一2班\t\n" + new string('X', 60);

        var name = RemoteConfig.SanitizeDeviceName(raw);

        Assert.DoesNotContain('\t', name);
        Assert.DoesNotContain('\n', name);
        Assert.True(name.Length <= 40);
        Assert.StartsWith("初一2班", name);
    }

    [Fact]
    public void BlankBecomesEmpty()
    {
        Assert.Equal("", RemoteConfig.SanitizeDeviceName("   "));
        Assert.Equal("", RemoteConfig.SanitizeDeviceName(null));
    }

    [Fact]
    public void DeviceNameSurvivesSanitize()
    {
        var cfg = RemoteConfig.Sanitize(new RemoteConfig
        {
            ServerUrl = "https://classisland.yibianhui.cn/",
            ManifestPath = "manifest.php",
            DeviceName = "  录播教室  "
        });

        Assert.Equal("https://classisland.yibianhui.cn", cfg.ServerUrl);
        Assert.Equal("/manifest.php", cfg.ManifestPath);
        Assert.Equal("录播教室", cfg.DeviceName);
    }
}
