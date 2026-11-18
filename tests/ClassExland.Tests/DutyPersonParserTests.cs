using ClassExland.Features.Duty.Migration;
using Xunit;

namespace ClassExland.Tests;

/// <summary>值日名单文本解析的纯逻辑测试（迁移旧插件数据时最易出错的一环）。</summary>
public class DutyPersonParserTests
{
    [Fact]
    public void Parse_兼容多种分隔符()
    {
        var names = DutyPersonParser.Parse("张三、李四，王五,赵六；钱七/孙八");
        Assert.Equal(new[] { "张三", "李四", "王五", "赵六", "钱七", "孙八" }, names);
    }

    [Fact]
    public void Parse_兼容换行与制表符()
    {
        var names = DutyPersonParser.Parse("张三\r\n李四\t王五");
        Assert.Equal(new[] { "张三", "李四", "王五" }, names);
    }

    [Fact]
    public void Parse_兼容全角空格()
    {
        var names = DutyPersonParser.Parse("张三　李四");
        Assert.Equal(new[] { "张三", "李四" }, names);
    }

    [Fact]
    public void Parse_去除空白项()
    {
        var names = DutyPersonParser.Parse("  张三   李四  ");
        Assert.Equal(new[] { "张三", "李四" }, names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("、、,")]
    public void Parse_空输入返回空集合(string? text)
    {
        Assert.Empty(DutyPersonParser.Parse(text));
    }

    [Fact]
    public void Join_用顿号连接()
    {
        Assert.Equal("张三、李四", DutyPersonParser.Join(new[] { "张三", "李四" }));
    }

    [Fact]
    public void Parse与Join_往返一致()
    {
        var text = "张三、李四、王五";
        Assert.Equal(text, DutyPersonParser.Join(DutyPersonParser.Parse(text)));
    }
}
