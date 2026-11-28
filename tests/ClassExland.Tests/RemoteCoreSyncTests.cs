using System.Text;
using System.Text.Json;
using ClassExland.Features.Remote;
using ClassExland.Infra;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// 远程同步 v2（core 全配置覆盖）的纯逻辑单测：不碰网络也不碰宿主。
/// 临时目录布局：root/config（插件配置）+ root（data 目录：Settings.json / Profiles / Config/ComponentLayouts）。
/// </summary>
public class RemoteCoreSyncTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cx-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        return root;
    }

    private static void Write(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static string NewDataLayout(string root, string settings = "{\"Scale\":1,\"Keep\":7}")
    {
        Write(Path.Combine(root, "Settings.json"), settings);
        Write(Path.Combine(root, "Profiles", "YBH.json"), "{\"Name\":\"YBH\"}");
        Write(Path.Combine(root, "Config", "ComponentLayouts", "Default.json"), "{\"Slots\":1}");
        Write(Path.Combine(root, "Config", "ComponentLayouts", "offclass.json"), "{\"Slots\":2}");
        return Path.Combine(root, "config");
    }

    // ---- C1：哈希命名空间 ----

    [Fact]
    public void C1_HashAll_六插件配置与核心配置各自分区且排除同步自身文件()
    {
        var root = NewRoot();
        try
        {
            var config = NewDataLayout(root);
            Write(Path.Combine(config, "bell.json"), "{\"b\":1}");
            Write(Path.Combine(config, "bell-options.json"), "{\"o\":1}");
            Write(Path.Combine(config, "lucky.json"), "{\"l\":1}");
            Write(Path.Combine(config, "remote.json"), "{\"token\":\"secret\"}");
            Write(Path.Combine(config, "remote-state.json"), "{}");

            var hashes = RemoteLocalSnapshot.HashAll(new PluginPaths(config));

            Assert.Equal(7, hashes.Count); // 3 个插件 + 4 个核心条目
            Assert.True(hashes.ContainsKey("bell.json"));
            Assert.True(hashes.ContainsKey("bell-options.json"));
            Assert.True(hashes.ContainsKey("lucky.json"));
            Assert.True(hashes.ContainsKey(RemoteLocalSnapshot.CoreSettingsKey));
            Assert.True(hashes.ContainsKey("core:profiles/YBH.json"));
            Assert.True(hashes.ContainsKey("core:layouts/Default.json"));
            Assert.True(hashes.ContainsKey("core:layouts/offclass.json"));
            Assert.False(hashes.ContainsKey("remote.json"));
            Assert.False(hashes.ContainsKey("remote-state.json"));
            Assert.False(hashes.ContainsKey("remote.disabled"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void C1_ReadCore_原文透传且哈希等于文件字节哈希()
    {
        var root = NewRoot();
        try
        {
            var config = NewDataLayout(root);
            var settingsPath = Path.Combine(root, "Settings.json");
            var raw = File.ReadAllText(settingsPath);
            var core = RemoteLocalSnapshot.ReadCore(root);

            Assert.Equal(raw, core[RemoteLocalSnapshot.CoreSettingsKey].Text);
            Assert.Equal(SafeConfigWriter.ComputeHash(settingsPath),
                core[RemoteLocalSnapshot.CoreSettingsKey].Hash);
            Assert.StartsWith("core:profiles/", core.Keys.First(k => k.Contains("YBH")));
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- C3：覆盖判定 ----

    [Fact]
    public void C3_空基线视为干净_变化才判脏()
    {
        var clean = new Dictionary<string, string>();
        var current = new Dictionary<string, string> { ["bell.json"] = "hash" };

        Assert.False(RemoteLocalSnapshot.IsLocalDirty(clean, current),
            "首次安装/升级没有基线时不能永远拒绝下发");

        var baseline = new Dictionary<string, string>(current);
        Assert.False(RemoteLocalSnapshot.IsLocalDirty(baseline, current));

        var modified = new Dictionary<string, string>(current) { ["bell.json"] = "changed" };
        Assert.True(RemoteLocalSnapshot.IsLocalDirty(baseline, modified));

        var added = new Dictionary<string, string>(current) { ["lucky.json"] = "new" };
        Assert.True(RemoteLocalSnapshot.IsLocalDirty(baseline, added));
    }

    [Fact]
    public void C3_手工同步在本地脏时拒绝覆盖()
    {
        // SyncOnceAsync 的判定是纯文件比对，这里用同样的断言复现其分支条件。
        var baseline = new Dictionary<string, string> { ["core:Settings.json"] = "base" };
        var current = new Dictionary<string, string> { ["core:Settings.json"] = "local-edited" };
        var needApply = true; // 服务端有新版本

        var shouldApply = needApply && !RemoteLocalSnapshot.IsLocalDirty(baseline, current);
        Assert.False(shouldApply);
    }

    // ---- C4：即时备份与覆盖前 diff ----

    [Fact]
    public void C4_UnifiedDiff_保留变更行且超长降级摘要()
    {
        var oldText = "line1\nline2\nline3\nline4";
        var newText = "line1\nCHANGED\nline3\nline4";
        var diff = UnifiedDiff.Create(oldText, newText, "local/a.json", "remote/a.json");

        Assert.True(diff.Ok);
        Assert.Contains("--- local/a.json", diff.Text);
        Assert.Contains("+++ remote/a.json", diff.Text);
        Assert.Contains("@@", diff.Text);
        Assert.Contains("-line2", diff.Text);
        Assert.Contains("+CHANGED", diff.Text);

        var huge = string.Join('\n', Enumerable.Range(0, 3001).Select(i => "x" + i));
        var summary = UnifiedDiff.Create("", huge, "a", "b");
        Assert.True(summary.Ok);
        Assert.True(summary.IsSummary);
        Assert.Contains("降级摘要", summary.Text);
    }

    [Fact]
    public void C4_备份写入changes与diffs目录()
    {
        var root = NewRoot();
        try
        {
            var config = NewDataLayout(root);
            var paths = new PluginPaths(config);
            var target = Path.Combine(root, "Settings.json");
            var audit = new RemoteAuditLogger(paths);

            var changes = ConfigDiffBackup.BackupChanges(paths.BackupDirectory, new[] { target });
            Assert.Single(changes);
            Assert.Contains("changes", changes[0]);
            Assert.Equal("{\"Scale\":1,\"Keep\":7}", File.ReadAllText(changes[0]));

            var ok = ConfigDiffBackup.WriteBeforeOverwrite(
                paths.BackupDirectory, audit, "20261128-130000-a1b2", target, "{\"Scale\":9,\"Keep\":7}");
            Assert.True(ok);
            var diffPath = Path.Combine(paths.BackupDirectory, "diffs",
                "20261128-130000-a1b2-Settings.json.diff");
            Assert.True(File.Exists(diffPath));
            Assert.Contains("-{\"Scale\":1,\"Keep\":7}", File.ReadAllText(diffPath));
            Assert.Contains("+{\"Scale\":9,\"Keep\":7}", File.ReadAllText(diffPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void C4_diff失败时返回false并写审计()
    {
        var root = NewRoot();
        try
        {
            var config = NewDataLayout(root);
            var paths = new PluginPaths(config);
            var audit = new RemoteAuditLogger(paths);
            var blocker = Path.Combine(root, "not-a-directory");
            Write(blocker, "x");

            var ok = ConfigDiffBackup.WriteBeforeOverwrite(
                blocker, audit, "v1", Path.Combine(root, "Settings.json"), "{\"Scale\":2}");

            Assert.False(ok);
            Assert.Contains("diff-fail", File.ReadAllText(paths.AuditLogPath));
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- C5：白名单 ----

    [Fact]
    public void C5_白名单扩充且移除档案级提示键()
    {
        var keys = CoreConfigPolicy.AllowedSettingKeys;
        foreach (var expected in new[]
        {
            "IsSwapMode", "IsClassChangingNotificationEnabled", "SingleWeekStartTime",
            "MultiWeekRotationOffset", "MultiWeekRotationMaxCycle", "TimeLayoutEditorIndex",
            "DefaultOnClassTimePointMinutes", "DefaultBreakingTimePointMinutes",
            "CountdownSeconds", "IsNotificationEnabled", "ShowExtraInfoOnTimePoint"
        })
            Assert.Contains(expected, keys);

        foreach (var removed in new[]
        {
            "ClassOnPreparingText", "ClassOnMaskText", "ClassOffMaskText", "AttachedObjects",
            "IsClassOnPreparingNotificationEnabled", "IsClassOnNotificationEnabled"
        })
            Assert.DoesNotContain(removed, keys);
    }

    [Fact]
    public void C5_MergeAllowed只合并白名单键且不引入token()
    {
        var local = "{\"Scale\":1,\"Keep\":7}";
        var remote = "{\"Scale\":3,\"Keep\":9,\"Token\":\"secret\"}";

        var merged = CoreConfigPolicy.MergeAllowed(local, remote);
        using var doc = JsonDocument.Parse(merged);
        var root = doc.RootElement;

        Assert.Equal(3, root.GetProperty("Scale").GetInt32());
        Assert.Equal(7, root.GetProperty("Keep").GetInt32());
        Assert.False(root.TryGetProperty("Token", out _));
    }

    [Fact]
    public void C5_上传方向与清单的layouts都做敏感键扫描()
    {
        var outgoing = CoreConfigPolicy.ValidateOutgoingCore(
            "{\"Scale\":1,\"Token\":\"abc\"}", Array.Empty<string>());
        Assert.False(outgoing.Ok);
        Assert.Contains("Token", outgoing.Error);

        var clean = CoreConfigPolicy.ValidateOutgoingCore("{\"Scale\":1}", new[] { "{\"Slots\":1}" });
        Assert.True(clean.Ok);

        var core = JsonSerializer.Deserialize<CoreConfigSnapshot>(
            "{\"settingsJson\":{\"Scale\":1},\"layouts\":{\"Default.json\":{\"json\":{\"Password\":\"x\"},\"baseHash\":\"h\"}}}");
        var manifest = new RemoteManifest { Snapshot = new RemoteSnapshot { Core = core } };
        var incoming = CoreConfigPolicy.Validate(manifest);
        Assert.False(incoming.Ok);
        Assert.Contains("组件布局", incoming.Error);
    }

    // ---- C6：热生效路径冲突检测 ----

    [Fact]
    public void C6_baseHash不符时热生效路径保留本地()
    {
        var root = NewRoot();
        try
        {
            var config = NewDataLayout(root);
            var paths = new PluginPaths(config);
            var settingsPath = Path.Combine(root, "Settings.json");
            var audit = new RemoteAuditLogger(paths);
            var writer = new CoreConfigWriter(new SafeConfigWriter(paths), new CorePaths(paths), audit);

            var snapshot = new CoreConfigSnapshot
            {
                SettingsJson = JsonDocument.Parse("{\"Scale\":99,\"Keep\":7}").RootElement,
                SettingsBaseHash = "WRONG-BASE-HASH"
            };
            var outcome = writer.Apply(snapshot, "v1");

            Assert.True(outcome.Ok, "保留本地不是整体失败");
            Assert.Contains(outcome.Results, r => r.ConflictKeptLocal && r.Path == settingsPath);
            Assert.Equal("{\"Scale\":1,\"Keep\":7}", File.ReadAllText(settingsPath));
            Assert.Contains("热生效路径哈希不符", File.ReadAllText(paths.AuditLogPath));
            Assert.True(Directory.GetFiles(Path.Combine(paths.BackupDirectory, "diffs"), "*.diff").Length > 0,
                "冲突时 diff 备份仍应生成");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void C6_baseHash相符时非冲突路径可正常应用()
    {
        var root = NewRoot();
        try
        {
            var config = NewDataLayout(root);
            var paths = new PluginPaths(config);
            var settingsPath = Path.Combine(root, "Settings.json");
            var baseHash = SafeConfigWriter.ComputeHash(settingsPath);
            var writer = new CoreConfigWriter(
                new SafeConfigWriter(paths), new CorePaths(paths), new RemoteAuditLogger(paths));

            var snapshot = new CoreConfigSnapshot
            {
                SettingsJson = JsonDocument.Parse("{\"Scale\":42,\"Keep\":7}").RootElement,
                SettingsBaseHash = baseHash
            };
            var outcome = writer.Apply(snapshot, "v2");

            Assert.True(outcome.Ok);
            Assert.DoesNotContain(outcome.Results, r => r.ConflictKeptLocal);
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            Assert.Equal(42, doc.RootElement.GetProperty("Scale").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void C6_layouts按命名空间写入ComponentLayouts目录()
    {
        var root = NewRoot();
        try
        {
            var config = NewDataLayout(root);
            var paths = new PluginPaths(config);
            var writer = new CoreConfigWriter(
                new SafeConfigWriter(paths), new CorePaths(paths), new RemoteAuditLogger(paths));

            var snapshot = new CoreConfigSnapshot();
            snapshot.Layouts["Default.json"] = new CoreProfileEntry
            {
                Json = JsonDocument.Parse("{\"Slots\":99}").RootElement,
                BaseHash = SafeConfigWriter.ComputeHash(
                    Path.Combine(root, "Config", "ComponentLayouts", "Default.json"))
            };
            var outcome = writer.Apply(snapshot, "v3");

            Assert.True(outcome.Ok);
            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(root, "Config", "ComponentLayouts", "Default.json")));
            Assert.Equal(99, doc.RootElement.GetProperty("Slots").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }
}
