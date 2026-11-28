using System.Text;
using System.Text.Json;
using ClassExland.Features.Remote;
using ClassExland.Infra;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// 远程同步 v2（全配置覆盖）客户端侧纯逻辑单测：C1/C3/C4/C5/C6。
/// 全部使用临时目录，不碰网络与宿主服务。
/// </summary>
public class RemoteCoreSyncC1C8Tests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cx-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string name, string text)
        => File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));

    /// <summary>构造 data 目录（含 Settings.json / Profiles / Config/ComponentLayouts）并给出插件配置目录。</summary>
    private static (string Data, string Config) NewDataTree(string root)
    {
        var data = Path.Combine(root, "data");
        var config = Path.Combine(data, "Config");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(Path.Combine(data, "Profiles"));
        Directory.CreateDirectory(Path.Combine(data, "Config", "ComponentLayouts"));
        return (data, config);
    }

    // ---- C1 ----

    [Fact]
    public void C1_插件白名单扩到六个且排除同步自身文件()
    {
        Assert.Equal(6, RemoteLocalSnapshot.FileNames.Length);
        Assert.Contains("bell-options.json", RemoteLocalSnapshot.FileNames);
        Assert.Contains("lucky.json", RemoteLocalSnapshot.FileNames);
        Assert.DoesNotContain("remote.json", RemoteLocalSnapshot.FileNames);
        Assert.DoesNotContain("remote-state.json", RemoteLocalSnapshot.FileNames);
        Assert.DoesNotContain("remote.disabled", RemoteLocalSnapshot.FileNames);
    }

    [Fact]
    public void C1_HashAll_核心配置带命名空间前缀且插件裸名()
    {
        var root = NewTempDir();
        try
        {
            var (data, config) = NewDataTree(root);
            Write(config, "bell.json", "{\"a\":1}");
            Write(config, "bell-options.json", "{\"b\":2}");
            Write(config, "lucky.json", "{\"c\":3}");
            Write(config, "remote.json", "{\"token\":\"绝不能上传\"}");
            Write(config, "remote-state.json", "{}");
            Write(data, "Settings.json", "{\"d\":4}");
            Write(Path.Combine(data, "Profiles"), "YBH.json", "{\"e\":5}");
            var layouts = Path.Combine(data, "Config", "ComponentLayouts");
            Write(layouts, "Default.json", "{\"f\":6}");
            Write(layouts, "offclass.json", "{\"g\":7}");

            var hashes = RemoteLocalSnapshot.HashAll(new PluginPaths(config));

            Assert.Contains("bell.json", hashes.Keys);
            Assert.Contains("bell-options.json", hashes.Keys);
            Assert.Contains("lucky.json", hashes.Keys);
            Assert.Contains(RemoteLocalSnapshot.CoreSettingsKey, hashes.Keys);
            Assert.Contains(RemoteLocalSnapshot.CoreProfilesPrefix + "YBH.json", hashes.Keys);
            Assert.Contains(RemoteLocalSnapshot.CoreLayoutsPrefix + "Default.json", hashes.Keys);
            Assert.Contains(RemoteLocalSnapshot.CoreLayoutsPrefix + "offclass.json", hashes.Keys);
            Assert.DoesNotContain("remote.json", hashes.Keys);
            Assert.DoesNotContain("remote-state.json", hashes.Keys);
            Assert.DoesNotContain("remote.disabled", hashes.Keys);
            Assert.Equal(RemoteLocalSnapshot.Hash("{\"d\":4}"), hashes[RemoteLocalSnapshot.CoreSettingsKey]);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- C3 ----

    [Fact]
    public void C3_只有需要应用且本地干净时才覆盖()
    {
        Assert.True(RemoteSyncModule.ShouldApplySnapshot(needApply: true, localDirty: false));
        Assert.False(RemoteSyncModule.ShouldApplySnapshot(needApply: true, localDirty: true));
        Assert.False(RemoteSyncModule.ShouldApplySnapshot(needApply: false, localDirty: false));

        var baseline = new Dictionary<string, string> { ["bell.json"] = "a" };
        Assert.False(RemoteLocalSnapshot.IsLocalDirty(baseline,
            new Dictionary<string, string> { ["bell.json"] = "a" }));
        Assert.True(RemoteLocalSnapshot.IsLocalDirty(baseline,
            new Dictionary<string, string> { ["bell.json"] = "b" }));
        // 首次安装/升级无基线：视为干净，否则永远拿不到第一份快照。
        Assert.False(RemoteLocalSnapshot.IsLocalDirty(new Dictionary<string, string>(),
            new Dictionary<string, string> { ["bell.json"] = "b" }));
    }

    // ---- C4 ----

    [Fact]
    public void C4_覆盖前写入diff备份且保留原文()
    {
        var root = NewTempDir();
        try
        {
            var paths = new PluginPaths(root);
            var file = paths.Get("bell.json");
            Write(root, "bell.json", "{\"a\":1}\n{\"b\":2}");
            var audit = new RemoteAuditLogger(paths);

            var ok = ConfigDiffBackup.WriteBeforeOverwrite(
                paths.BackupDirectory, audit, "20261128-130000-x1", file,
                "{\"a\":9}\n{\"b\":2}");

            Assert.True(ok);
            // 原文未被覆盖：diff 备份是只读的。
            Assert.Equal("{\"a\":1}\n{\"b\":2}", File.ReadAllText(file));
            var diff = Path.Combine(paths.BackupDirectory, "diffs", "20261128-130000-x1-bell.json.diff");
            Assert.True(File.Exists(diff));
            var text = File.ReadAllText(diff);
            Assert.Contains("--- local/bell.json", text);
            Assert.Contains("+++ remote/bell.json", text);
            Assert.Contains("@@ -1,2 +1,2 @@", text);
            Assert.Contains("-{\"a\":1}", text);
            Assert.Contains("+{\"a\":9}", text);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void C4_变化检测时即时备份到changes时间戳目录()
    {
        var root = NewTempDir();
        try
        {
            var paths = new PluginPaths(root);
            var file = paths.Get("bell.json");
            Write(root, "bell.json", "{\"a\":1}");

            var backed = ConfigDiffBackup.BackupChanges(paths.BackupDirectory, new[] { file });

            Assert.Single(backed);
            var path = backed[0];
            Assert.Contains(Path.Combine("Backups", "changes"), path);
            Assert.Equal("bell.json", Path.GetFileName(path));
            Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void C4_超过三千行降级为摘要而不是巨型diff()
    {
        var oldText = string.Join("\n", Enumerable.Range(0, 2000).Select(i => "line" + i));
        var newText = string.Join("\n", Enumerable.Range(0, 2000).Select(i => i == 0 ? "changed" : "line" + i));

        var result = UnifiedDiff.Create(oldText, newText, "local/x", "remote/x");

        Assert.True(result.Ok);
        Assert.True(result.IsSummary);
        Assert.Contains("降级摘要", result.Text);
    }

    // ---- C5 ----

    [Fact]
    public void C5_白名单扩充安全键并移除错位提示键()
    {
        var keys = CoreConfigPolicy.AllowedSettingKeys;
        foreach (var key in new[]
                 {
                     "IsSwapMode", "IsClassChangingNotificationEnabled", "SingleWeekStartTime",
                     "MultiWeekRotationOffset", "MultiWeekRotationMaxCycle", "TimeLayoutEditorIndex",
                     "DefaultOnClassTimePointMinutes", "DefaultBreakingTimePointMinutes",
                     "CountdownSeconds", "IsNotificationEnabled", "ShowExtraInfoOnTimePoint"
                 })
            Assert.Contains(key, keys);

        // 这些字段属于 Profile 的 AttachedObjects，不能从 Settings.json 合并。
        foreach (var key in new[]
                 {
                     "ClassOnPreparingText", "ClassOnMaskText", "ClassOffMaskText",
                     "AttachedObjects", "IsClassOnPreparingNotificationEnabled",
                     "IsClassOnNotificationEnabled"
                 })
            Assert.DoesNotContain(key, keys);
    }

    [Fact]
    public void C5_MergeAllowed_不把错位键从远端合进Settings()
    {
        var local = "{\"Scale\":1,\"AttachedObjects\":{\"local\":true}}";
        var remote = "{\"Scale\":2,\"IsSwapMode\":true,\"AttachedObjects\":{\"remote\":true}}";

        var merged = CoreConfigPolicy.MergeAllowed(local, remote);

        using var doc = JsonDocument.Parse(merged);
        Assert.Equal(2, doc.RootElement.GetProperty("Scale").GetInt32());
        Assert.True(doc.RootElement.GetProperty("IsSwapMode").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("AttachedObjects").GetProperty("local").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("AttachedObjects").TryGetProperty("remote", out _));
    }

    [Fact]
    public void C2_上传方向命中敏感键整份拒绝()
    {
        Assert.False(CoreConfigPolicy.ValidateOutgoingCore("{\"ApiToken\":\"abc\"}", Array.Empty<string>()).Ok);
        Assert.False(CoreConfigPolicy.ValidateOutgoingCore("{\"Scale\":1}", new[] { "{\"Password\":\"x\"}" }).Ok);
        Assert.True(CoreConfigPolicy.ValidateOutgoingCore("{\"Scale\":1}", new[] { "{\"Scale\":2}" }).Ok);
    }

    // ---- C6 ----

    [Fact]
    public void C6_热生效路径baseHash不符时保留本地文件()
    {
        var root = NewTempDir();
        try
        {
            var (data, _) = NewDataTree(root);
            var original = "{\"Scale\":1,\"IsSwapMode\":false}";
            Write(data, "Settings.json", original);
            var paths = new PluginPaths(data);
            var writer = new CoreConfigWriter(
                new SafeConfigWriter(paths), new CorePaths(paths), new RemoteAuditLogger(paths));

            using var remote = JsonDocument.Parse("{\"Scale\":2,\"IsSwapMode\":true}");
            var outcome = writer.Apply(new CoreConfigSnapshot
            {
                SettingsJson = remote.RootElement.Clone(),
                SettingsBaseHash = "故意写错的哈希"
            }, "v1");

            Assert.Equal(original, File.ReadAllText(Path.Combine(data, "Settings.json")));
            Assert.Contains(outcome.Results,
                r => r.ConflictKeptLocal && r.Path == Path.Combine(data, "Settings.json"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void C6_baseHash一致时允许合并白名单键()
    {
        var root = NewTempDir();
        try
        {
            var (data, _) = NewDataTree(root);
            var original = "{\"Scale\":1,\"IsSwapMode\":false}";
            var path = Path.Combine(data, "Settings.json");
            Write(data, "Settings.json", original);
            var paths = new PluginPaths(data);
            var writer = new CoreConfigWriter(
                new SafeConfigWriter(paths), new CorePaths(paths), new RemoteAuditLogger(paths));
            var hash = RemoteLocalSnapshot.Hash(original);

            using var remote = JsonDocument.Parse("{\"Scale\":2,\"IsSwapMode\":true}");
            var outcome = writer.Apply(new CoreConfigSnapshot
            {
                SettingsJson = remote.RootElement.Clone(),
                SettingsBaseHash = hash
            }, "v2");

            Assert.DoesNotContain(outcome.Results, r => r.ConflictKeptLocal);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(2, doc.RootElement.GetProperty("Scale").GetInt32());
            Assert.True(doc.RootElement.GetProperty("IsSwapMode").GetBoolean());
        }
        finally { Directory.Delete(root, true); }
    }
}
