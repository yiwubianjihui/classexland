using System.Text;
using ClassExland.Features.Remote;
using ClassExland.Infra;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// 双向同步的纯逻辑：本地指纹、变更判定、配置收敛。
/// 不碰网络也不碰宿主，直接用临时目录。
/// </summary>
public class RemoteDiffTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cx-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string name, string text)
        => File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));

    [Fact]
    public void HashDirectory_只看白名单文件()
    {
        var dir = NewTempDir();
        try
        {
            Write(dir, "bell.json", "{\"a\":1}");
            Write(dir, "layout.json", "{\"b\":2}");
            Write(dir, "remote.json", "{\"token\":\"secret\"}");
            Write(dir, "remote-state.json", "{}");

            var hashes = RemoteLocalSnapshot.HashDirectory(dir);

            Assert.Equal(2, hashes.Count);
            Assert.True(hashes.ContainsKey("bell.json"));
            Assert.True(hashes.ContainsKey("layout.json"));
            Assert.False(hashes.ContainsKey("remote.json"));
            Assert.False(hashes.ContainsKey("remote-state.json"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Hash_同样内容同样哈希_改一个字符就变()
    {
        var a = RemoteLocalSnapshot.Hash("{\"x\":1}");
        var b = RemoteLocalSnapshot.Hash("{\"x\":1}");
        var c = RemoteLocalSnapshot.Hash("{\"x\":2}");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void HasChanges_识别新增修改与删除()
    {
        var old = new Dictionary<string, string>
        {
            ["bell.json"] = "a",
            ["duty.json"] = "b"
        };
        var same = new Dictionary<string, string>(old);
        Assert.False(RemoteLocalSnapshot.HasChanges(old, same));

        var modified = new Dictionary<string, string>(old) { ["bell.json"] = "changed" };
        Assert.True(RemoteLocalSnapshot.HasChanges(old, modified));

        var added = new Dictionary<string, string>(old) { ["layout.json"] = "new" };
        Assert.True(RemoteLocalSnapshot.HasChanges(old, added));

        var removed = new Dictionary<string, string> { ["duty.json"] = "b" };
        Assert.True(RemoteLocalSnapshot.HasChanges(old, removed));
    }

    [Fact]
    public void DescribeChanges_说清哪个文件变了()
    {
        var old = new Dictionary<string, string> { ["bell.json"] = "a", ["duty.json"] = "b" };
        var now = new Dictionary<string, string> { ["bell.json"] = "x", ["layout.json"] = "y" };

        var text = RemoteLocalSnapshot.DescribeChanges(old, now);

        Assert.Contains("bell.json 修改", text);
        Assert.Contains("layout.json 新增", text);
        Assert.Contains("duty.json 删除", text);
    }

    [Fact]
    public void Sanitize_按清单路径推导上传端点()
    {
        var cfg = RemoteConfig.Sanitize(new RemoteConfig
        {
            ServerUrl = "https://classisland.yibianhui.cn/",
            ManifestPath = "/remote/manifest.php"
        });

        Assert.Equal("/remote/upload.php", cfg.UploadPath);
        Assert.True(cfg.AutoUpload);
        Assert.Equal(60, cfg.PollIntervalSeconds);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(5, 60)]
    [InlineData(9999, 60)]
    [InlineData(120, 120)]
    public void Sanitize_轮询间隔限制在15到3600(int raw, int expected)
    {
        var cfg = RemoteConfig.Sanitize(new RemoteConfig { PollIntervalSeconds = raw });
        Assert.Equal(expected, cfg.PollIntervalSeconds);
    }

    [Fact]
    public void PluginPaths_Get_不泄露上级目录()
    {
        var dir = NewTempDir();
        try
        {
            var paths = new PluginPaths(dir);
            Assert.Equal(Path.Combine(dir, "bell.json"), paths.Get("bell.json"));
            Assert.True(paths.Get("deep/x.json").EndsWith("deep/x.json"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(2, 10)]
    [InlineData(9999, 10)]
    [InlineData(25, 25)]
    public void Sanitize_命令轮询间隔限制在5到3600(int raw, int expected)
    {
        var cfg = RemoteConfig.Sanitize(new RemoteConfig { CommandPollSeconds = raw });
        Assert.Equal(expected, cfg.CommandPollSeconds);
    }

    [Fact]
    public void RemoteCommand_能读出服务端带的id()
    {
        var cmd = System.Text.Json.JsonSerializer.Deserialize<RemoteCommand>(
            "{\"id\":\"abc123\",\"command\":\"layout.switch\",\"args\":{\"to\":\"class\"},\"apply\":true}");

        Assert.NotNull(cmd);
        Assert.Equal("abc123", cmd!.Id);
        Assert.Equal("layout.switch", cmd.Command);
        Assert.Equal("class", cmd.Args["to"]);
    }

    [Fact]
    public void StateStore_指令id去重且保留最近200条()
    {
        var dir = NewTempDir();
        try
        {
            var store = new RemoteStateStore(new PluginPaths(dir));
            store.MarkCommandApplied("a");
            store.MarkCommandApplied("b");
            store.MarkCommandApplied("a");

            var ids = store.LoadAppliedCommandIds();
            Assert.Equal(new[] { "a", "b" }, ids.OrderBy(x => x).ToArray());

            for (var i = 0; i < 205; i++) store.MarkCommandApplied("id" + i);
            Assert.Equal(200, store.LoadAppliedCommandIds().Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
