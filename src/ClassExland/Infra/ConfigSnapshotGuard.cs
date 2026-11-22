using System.Text;
using System.Text.Json;
using ClassExland.Contracts;

namespace ClassExland.Infra;

/// <summary>
/// 配置持久化保障。
///
/// 起因：1.0.0.x 期间出现过"每次更新插件就丢配置"——真正的根因是
/// <c>BellSettings.Load()</c> 末尾无条件 <c>Save()</c>，读取稍有失败就用默认值把磁盘冲掉。
/// 那个 bug 已在 1.0.0.14 修掉；这里再加一层"文件级"保险，让**任何**模块的误写都能回滚：
///
///   · 每次插件启动，给配置目录里的每个 json 存一份快照（同一天只留一份，最多 14 份）；
///   · 快照按内容哈希去重，内容没变就不重复写；
///   · <see cref="RestoreIfDamaged"/> 在启动早期调用：文件缺失或被写成空/无内容时，
///     用最近的快照补回，而不是让插件用默认值把用户配置覆盖掉。
///
/// 全部 try/catch：备份失败绝不影响插件启动。
/// </summary>
public sealed class ConfigSnapshotGuard
{
    /// <summary>受保护的配置文件（插件自有配置；不含日志与状态文件）。</summary>
    private static readonly string[] Protected =
    {
        ConfigFiles.Bell, "bell-options.json",
        ConfigFiles.Duty, "activity-options.json",
        ConfigFiles.Activity,
        ConfigFiles.Layout,
        ConfigFiles.Remote,
        ConfigFiles.Lucky,
        ConfigFiles.Bridge
    };

    private const int MaxSnapshots = 14;

    private readonly IPluginPaths _paths;
    private readonly object _gate = new();

    public ConfigSnapshotGuard(IPluginPaths paths) => _paths = paths;

    private string SnapshotRoot => Path.Combine(_paths.ConfigDirectory, "Backups", "snapshots");

    /// <summary>启动时调用：先尝试修复损坏文件，再给当前内容存快照。</summary>
    public void EnsureHealthy()
    {
        try
        {
            RestoreIfDamaged();
        }
        catch
        {
            // 修复失败不影响启动。
        }

        try
        {
            TakeSnapshot();
        }
        catch
        {
            // 快照失败不影响启动。
        }
    }

    /// <summary>
    /// 文件缺失、为空、或是解析不出内容的 JSON 时，用最近一份快照补回来。
    /// 返回修复过的文件名（供诊断）。
    /// </summary>
    public IReadOnlyList<string> RestoreIfDamaged()
    {
        var repaired = new List<string>();
        lock (_gate)
        {
            foreach (var name in Protected)
            {
                var path = _paths.Get(name);
                if (IsHealthy(path)) continue;

                var snapshot = LatestSnapshotFor(name);
                if (snapshot is null) continue;

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.Copy(snapshot, path, overwrite: true);
                    repaired.Add(name);
                }
                catch
                {
                    // 单个文件修复失败就跳过，别让整个流程垮掉。
                }
            }
        }
        return repaired;
    }

    /// <summary>给当前所有配置存一份快照（同一天一份，内容未变则跳过）。</summary>
    public void TakeSnapshot()
    {
        lock (_gate)
        {
            var day = DateTime.Now.ToString("yyyyMMdd");
            var dir = Path.Combine(SnapshotRoot, day);
            Directory.CreateDirectory(dir);

            foreach (var name in Protected)
            {
                var path = _paths.Get(name);
                if (!File.Exists(path)) continue;

                var target = Path.Combine(dir, name);
                try
                {
                    var content = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    // 同一天内容没变就不重复写，避免把快照目录刷爆。
                    if (File.Exists(target) &&
                        string.Equals(File.ReadAllText(target, Encoding.UTF8), content, StringComparison.Ordinal))
                        continue;

                    File.WriteAllText(target, content, new UTF8Encoding(false));
                }
                catch
                {
                    // 单文件快照失败忽略。
                }
            }

            PruneOld();
        }
    }

    /// <summary>最近一份该文件的快照路径；没有则返回 null。</summary>
    private string? LatestSnapshotFor(string name)
    {
        try
        {
            if (!Directory.Exists(SnapshotRoot)) return null;
            return Directory.GetDirectories(SnapshotRoot)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Select(d => Path.Combine(d, name))
                .FirstOrDefault(IsHealthy);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>健康 = 存在、非空、且（json 文件）能解析。</summary>
    private static bool IsHealthy(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return false;

            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var _ = JsonDocument.Parse(text);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void PruneOld()
    {
        try
        {
            if (!Directory.Exists(SnapshotRoot)) return;
            var dirs = Directory.GetDirectories(SnapshotRoot)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Skip(MaxSnapshots)
                .ToList();

            foreach (var d in dirs)
            {
                try { Directory.Delete(d, recursive: true); }
                catch { /* 删不掉就算了 */ }
            }
        }
        catch
        {
            // 清理失败无妨。
        }
    }
}
