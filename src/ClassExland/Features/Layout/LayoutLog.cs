using ClassExland.Contracts;

namespace ClassExland.Features.Layout;

/// <summary>
/// 极简文件日志。刻意不依赖 ILogger 的注册状态，避免 DI 构造失败导致插件被禁用；
/// 所有写入都 try/catch，日志失败绝不影响主流程。
/// </summary>
public sealed class LayoutLog
{
    private readonly object _gate = new();
    private readonly string _path;

    public LayoutLog(IPluginPaths paths)
    {
        _path = paths.Get("layout.log");
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_path, line);
            }
        }
        catch
        {
            // 忽略：日志不是关键路径。
        }
    }
}
