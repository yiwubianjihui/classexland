using ClassExland.Contracts;
using ClassExland.Features.Bell;

namespace ClassExland.Features.Bridge;

/// <summary>
/// 预备铃相关的桥接辅助：课表节次枚举与"改完立即生效"。
/// CLI 与远程指令队列都走这里，避免出现"文件改了但内存/档案还是旧值"。
/// </summary>
public sealed partial class CommandDispatcher
{
    /// <summary>按稳定顺序列出档案里的全部上课段；Bell 模块未加载时返回空。</summary>
    private IReadOnlyList<BellSlotInfo> EnumerateBellSlots()
    {
        try
        {
            var settings = TryService<BellSettings>();
            if (settings is null) return Array.Empty<BellSlotInfo>();

            var profileService =
                TryService<ClassIsland.Core.Abstractions.Services.IProfileService>();
            return BellSlotCatalog.Enumerate(profileService?.Profile, settings);
        }
        catch
        {
            return Array.Empty<BellSlotInfo>();
        }
    }

    /// <summary>该文件名是否属于预备铃配置。</summary>
    private static bool IsBellConfigFile(string file) =>
        file.Equals(ConfigFiles.Bell, StringComparison.OrdinalIgnoreCase) ||
        file.Equals("bell-options.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 写完 bell.json 后：重载内存配置 → 把每节课的值同步进档案附加设置 → 让时间轴重算。
    /// 档案里的附加设置优先级高于 bell.json，不同步就会出现"CLI 改了不生效"。
    /// </summary>
    private bool RefreshBellAfterWrite()
    {
        try
        {
            var settings = TryService<BellSettings>();
            settings?.Reload();

            var applied = TryService<BellProfileWriter>()?.ApplySlotPreps() ?? false;
            TryService<BellTimelineService>()?.InvalidateSchedule();
            return applied;
        }
        catch
        {
            return false;
        }
    }
}
