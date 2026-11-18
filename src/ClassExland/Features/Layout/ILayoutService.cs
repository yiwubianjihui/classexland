namespace ClassExland.Features.Layout;

/// <summary>
/// 界面布局服务，供 Bridge 的 layout.switch / layout.current 命令复用。
///
/// 重要：Bridge 按接口全名反射解析，且只传 1 个 target 参数调用 SwitchToAsync。
/// 因此本接口方法【只带 target 一个参数】，不要加 CancellationToken（含可选参数），
/// 否则 MethodInfo.Invoke 会因参数个数不匹配报错。
/// target 取 "class"（上课紧凑）或 "break"（下课完整）。
/// </summary>
public interface ILayoutService
{
    /// <summary>当前布局目标（"class" / "break"，未切换前为空字符串）。</summary>
    string CurrentLayout { get; }

    /// <summary>切换到指定布局。未知 target 返回但不生效。</summary>
    Task SwitchToAsync(string target);
}
