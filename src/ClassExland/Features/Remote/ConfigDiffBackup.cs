using System.Text;

namespace ClassExland.Features.Remote;

/// <summary>unified diff 生成结果。Ok=false 表示失败，调用方必须中止覆盖。</summary>
public sealed record DiffResult(bool Ok, string Text, string Error, bool IsSummary)
{
    public static DiffResult Success(string text, bool summary = false) => new(true, text, "", summary);
    public static DiffResult Failure(string error) => new(false, "", error, false);
}

/// <summary>
/// 极简 unified diff：逐行 LCS + 3 行上下文。超过 3000 行降级为摘要，
/// 避免大 JSON 单行使 DP 爆内存。只读原文，不重新序列化，不破坏大小写。
/// </summary>
public static class UnifiedDiff
{
    private const int MaxLines = 3000;
    private const int Context = 3;

    private sealed record Op(char Kind, string Line);

    public static DiffResult Create(string oldText, string newText, string oldLabel, string newLabel)
    {
        try
        {
            var oldLines = SplitLines(oldText);
            var newLines = SplitLines(newText);
            var header = $"--- {oldLabel}\n+++ {newLabel}\n";
            if (oldLines.Count + newLines.Count > MaxLines)
            {
                var summary = header +
                    $"[diff 降级摘要] 超过 {MaxLines} 行限制：原文 {oldLines.Count} 行 → 新文 {newLines.Count} 行，未逐行展开。\n";
                return DiffResult.Success(summary, summary: true);
            }

            var ops = BuildOps(oldLines, newLines);
            var sb = new StringBuilder(header);
            var hunks = BuildHunks(ops);
            if (hunks.Count == 0) sb.Append("（无变化）\n");
            else foreach (var hunk in hunks) sb.Append(hunk);
            return DiffResult.Success(sb.ToString());
        }
        catch (Exception ex)
        {
            return DiffResult.Failure($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
    }

    private static List<Op> BuildOps(List<string> oldLines, List<string> newLines)
    {
        var n = oldLines.Count;
        var m = newLines.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = oldLines[i] == newLines[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var ops = new List<Op>();
        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (oldLines[x] == newLines[y])
            {
                ops.Add(new Op(' ', oldLines[x]));
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                ops.Add(new Op('-', oldLines[x++]));
            }
            else
            {
                ops.Add(new Op('+', newLines[y++]));
            }
        }
        while (x < n) ops.Add(new Op('-', oldLines[x++]));
        while (y < m) ops.Add(new Op('+', newLines[y++]));
        return ops;
    }

    private static List<string> BuildHunks(List<Op> ops)
    {
        var changes = new List<int>();
        for (var i = 0; i < ops.Count; i++)
            if (ops[i].Kind != ' ') changes.Add(i);
        if (changes.Count == 0) return new List<string>();

        var hunks = new List<string>();
        var groupStart = 0;
        while (groupStart < changes.Count)
        {
            var first = changes[groupStart];
            var last = first;
            var k = groupStart + 1;
            while (k < changes.Count && changes[k] - last <= 2 * Context + 1)
            {
                last = changes[k];
                k++;
            }

            var start = Math.Max(0, first - Context);
            var end = Math.Min(ops.Count - 1, last + Context);
            var oldStart = 1;
            var newStart = 1;
            for (var i = 0; i < start; i++)
            {
                if (ops[i].Kind != '+') oldStart++;
                if (ops[i].Kind != '-') newStart++;
            }
            var oldCount = 0;
            var newCount = 0;
            for (var i = start; i <= end; i++)
            {
                if (ops[i].Kind != '+') oldCount++;
                if (ops[i].Kind != '-') newCount++;
            }

            var sb = new StringBuilder();
            sb.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n");
            for (var i = start; i <= end; i++)
                sb.Append(ops[i].Kind).Append(ops[i].Line).Append('\n');
            hunks.Add(sb.ToString());
            groupStart = k;
        }
        return hunks;
    }
}

/// <summary>
/// 两类备份：变化检测到时的即时备份 changes/&lt;时间戳&gt;/&lt;文件名&gt;，
/// 以及覆盖前的 diff 备份 diffs/&lt;version&gt;-&lt;文件名&gt;.diff。
/// diff 生成或写入失败时返回 false，调用方必须中止覆盖并记审计。
/// </summary>
public static class ConfigDiffBackup
{
    public static IReadOnlyList<string> BackupChanges(string backupDirectory, IEnumerable<string> paths)
    {
        var copied = new List<string>();
        try
        {
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
            var dir = Path.Combine(backupDirectory, "changes", stamp);
            Directory.CreateDirectory(dir);
            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var dest = Path.Combine(dir, Path.GetFileName(path));
                    File.Copy(path, dest, overwrite: false);
                    copied.Add(dest);
                }
                catch
                {
                    // 单文件备份失败不影响其余文件。
                }
            }
        }
        catch { /* 目录创建失败返回空列表 */ }
        return copied;
    }

    public static bool WriteBeforeOverwrite(
        string backupDirectory, RemoteAuditLogger audit, string version, string path, string newText)
    {
        var name = Path.GetFileName(path);
        string oldText;
        try
        {
            oldText = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
        }
        catch (Exception ex)
        {
            audit.Log("diff-fail", $"{name} 读取本地原文失败：{ex.Message}，已中止覆盖", ok: false);
            return false;
        }

        var diff = UnifiedDiff.Create(oldText, newText, "local/" + name, "remote/" + name);
        if (!diff.Ok)
        {
            audit.Log("diff-fail", $"{name} diff 生成失败：{diff.Error}，已中止覆盖", ok: false);
            return false;
        }

        try
        {
            var dir = Path.Combine(backupDirectory, "diffs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Sanitize(version, "unknown") + "-" + Sanitize(name, "file") + ".diff");
            File.WriteAllText(file, diff.Text, new UTF8Encoding(false));
            audit.Log(diff.IsSummary ? "diff-summary" : "diff-backup",
                $"{file}（{oldText.Length} → {newText.Length} 字符）");
            return true;
        }
        catch (Exception ex)
        {
            audit.Log("diff-fail", $"{name} diff 写入失败：{ex.Message}，已中止覆盖", ok: false);
            return false;
        }
    }

    private static string Sanitize(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
