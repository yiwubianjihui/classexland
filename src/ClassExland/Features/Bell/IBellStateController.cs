using ClassExland.Contracts;

namespace ClassExland.Features.Bell;

/// <summary>
/// 供 CLI / 远程指令强制覆盖虚拟状态的可选控制器。
/// 不加入共享 Contracts，避免影响其他模块；Bridge 通过反射弱依赖此接口。
/// </summary>
public interface IBellStateController
{
    Task SetVirtualStateAsync(VirtualTimeState state, CancellationToken ct = default);
}
