using ClassExland.Contracts;
using ClassExland.Infra;
using Xunit;

namespace ClassExland.Tests;

/// <summary>
/// 配置持久化保障：快照与损坏恢复。
///
/// 真实事故（1.0.0.5–1.0.0.13）：BellSettings.Load() 末尾无条件 Save()，
/// 读到空/坏文件时用默认值回写，用户配置被永久冲掉，表现为"每次更新都重置配置"。
/// 这些测试锁住兜底能力：坏文件能从快照恢复，快照不会重复堆叠。
/// </summary>
public class ConfigSnapshotGuardTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cx-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 坏文件会从快照恢复()
    {
        var dir = NewDir();
        try
        {
            var paths = new PluginPaths(dir);
            var guard = new ConfigSnapshotGuard(paths);

            // 好配置 → 存快照
            File.WriteAllText(paths.Get(ConfigFiles.Bell), "[{\"prepSeconds\":90}]");
            guard.TakeSnapshot();

            // 模拟被写坏：空文件
            File.WriteAllText(paths.Get(ConfigFiles.Bell), "");

            var repaired = guard.RestoreIfDamaged();

            Assert.Contains(ConfigFiles.Bell, repaired);
            Assert.Contains("90", File.ReadAllText(paths.Get(ConfigFiles.Bell)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 非法JSON也会被恢复()
    {
        var dir = NewDir();
        try
        {
            var paths = new PluginPaths(dir);
            var guard = new ConfigSnapshotGuard(paths);

            File.WriteAllText(paths.Get(ConfigFiles.Duty), "{\"enabled\":true}");
            guard.TakeSnapshot();

            File.WriteAllText(paths.Get(ConfigFiles.Duty), "{ 坏掉的 json");

            var repaired = guard.RestoreIfDamaged();

            Assert.Contains(ConfigFiles.Duty, repaired);
            Assert.Contains("enabled", File.ReadAllText(paths.Get(ConfigFiles.Duty)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 健康文件不会被改动()
    {
        var dir = NewDir();
        try
        {
            var paths = new PluginPaths(dir);
            var guard = new ConfigSnapshotGuard(paths);

            var content = "{\"enabled\":true,\"plans\":[]}";
            File.WriteAllText(paths.Get(ConfigFiles.Duty), content);
            guard.TakeSnapshot();

            var repaired = guard.RestoreIfDamaged();

            Assert.Empty(repaired);
            Assert.Equal(content, File.ReadAllText(paths.Get(ConfigFiles.Duty)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 没有快照时不会凭空造文件()
    {
        var dir = NewDir();
        try
        {
            var paths = new PluginPaths(dir);
            var guard = new ConfigSnapshotGuard(paths);

            var repaired = guard.RestoreIfDamaged();

            Assert.Empty(repaired);
            Assert.False(File.Exists(paths.Get(ConfigFiles.Bell)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 快照按天归档且内容未变不重复写()
    {
        var dir = NewDir();
        try
        {
            var paths = new PluginPaths(dir);
            var guard = new ConfigSnapshotGuard(paths);

            File.WriteAllText(paths.Get(ConfigFiles.Bell), "[]");
            guard.TakeSnapshot();
            var root = Path.Combine(dir, "Backups", "snapshots");
            var firstDay = Directory.GetDirectories(root).Single();

            guard.TakeSnapshot(); // 同内容再来一次

            Assert.Single(Directory.GetDirectories(root));
            Assert.Equal(firstDay, Directory.GetDirectories(root).Single());
            Assert.True(File.Exists(Path.Combine(firstDay, ConfigFiles.Bell)));
        }
        finally { Directory.Delete(dir, true); }
    }
}
