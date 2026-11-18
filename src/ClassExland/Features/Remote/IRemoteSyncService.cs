namespace ClassExland.Features.Remote;

/// <summary>
/// 远程同步服务契约，供 Bridge 的 remote.status/sync/poll 命令复用。
///
/// 重要：Bridge 按接口全名反射解析，SyncNowAsync/PollOnceAsync 均为【零参数】，
/// 返回值为可直接展示/解析的文本摘要。不要加 CancellationToken 参数。
/// </summary>
public interface IRemoteSyncService
{
    /// <summary>当前状态快照（可被 CLI 序列化）。</summary>
    object? Status { get; }

    /// <summary>立即执行一次完整同步（下载 + 校验 + 应用 + 回滚保护）。</summary>
    Task<string> SyncNowAsync();

    /// <summary>只拉取清单并检查版本/etag，不应用任何配置。</summary>
    Task<string> PollOnceAsync();
}
