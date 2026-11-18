using System.Text;
using ClassExland.Contracts;

namespace ClassExland.Features.Remote;

/// <summary>审计日志：记录每次远程同步的来源、版本、动作、结果与回滚情况。</summary>
public sealed class RemoteAuditLogger
{
    private readonly object _gate = new();
    private readonly string _path;

    public RemoteAuditLogger(IPluginPaths paths)
    {
        _path = paths.AuditLogPath;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { /* 忽略 */ }
    }

    public void Log(string action, string detail, bool ok = true)
    {
        try
        {
            lock (_gate)
            {
                var sb = new StringBuilder();
                sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(ok ? " [OK] " : " [FAIL] ");
                sb.Append("remote ");
                sb.Append(action);
                sb.Append(" :: ");
                sb.Append(detail.Replace('\r', ' ').Replace('\n', ' '));
                sb.AppendLine();
                File.AppendAllText(_path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 审计日志失败不影响同步主流程。
        }
    }
}
