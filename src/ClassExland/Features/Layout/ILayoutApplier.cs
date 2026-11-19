namespace ClassExland.Features.Layout;

public enum LayoutTargetKind
{
    Class,
    Break
}

public sealed record LayoutApplyResult(bool Ok, string Message);

/// <summary>
/// 把布局配置应用到宿主。实现只做内存态设置或调用宿主服务，绝不覆盖 Automations/Default.json。
/// </summary>
public interface ILayoutApplier
{
    Task<LayoutApplyResult> ApplyAsync(LayoutTargetKind target, LayoutConfig config, CancellationToken ct = default);

    /// <summary>列出宿主现有的组件配置名（用于设置页下拉框）；读不到时返回空列表。</summary>
    IReadOnlyList<string> GetComponentConfigNames();
}
