namespace ClassExland.Features.Remote;

/// <summary>
/// 多班级档案切换服务。Bridge 的 profile list|switch|current 命令按接口全名反射复用。
///
/// 重要：SwitchProfileAsync 【只带 profileName 一个参数】（Bridge 会先试 2 参数再回退 1 参数）。
/// 切换必须走宿主官方 IPublicProfileService.CurrentProfilePath，禁止自行改 Settings.json。
/// </summary>
public interface IProfileService
{
    /// <summary>当前加载的档案文件名（如 YBH.json）。</summary>
    string CurrentProfile { get; }

    /// <summary>列出 data/Profiles 下可用档案文件名。</summary>
    IReadOnlyList<string> ListProfiles();

    /// <summary>切换档案。成功返回 true。</summary>
    Task<bool> SwitchProfileAsync(string profileName);
}
