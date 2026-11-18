namespace ClassExland.Features.Bell;

/// <summary>
/// 预备铃模块固定标识。所有 GUID 一经发布不得更改，否则用户已写入档案的设置会失联。
/// </summary>
public static class BellConstants
{
    /// <summary>
    /// 我方写入 TimeLayouts[].Layouts[].AttachedObjects 的附加设置 GUID。
    /// 由本项目固定生成，用于保存每节课的预备铃提前量（<see cref="BellSlotSettings.PrepSeconds"/>）。
    /// </summary>
    public const string SlotAttachedGuidText = "A7E3C1B4-6D52-4F08-9C31-8B0E5A2D7F64";
    public static readonly Guid SlotAttachedGuid = Guid.Parse(SlotAttachedGuidText);

    /// <summary>
    /// 宿主内置的「上下课提醒」附加设置 GUID（来自勘察 data/Profiles/*.json）。
    /// 一键配置会把其下 IsClassOnPreparingNotificationEnabled / IsClassOnNotificationEnabled 置 false，
    /// 以避免与 ClassExland 自有提醒重复响铃；ClassOff 保留给宿主。
    /// </summary>
    public const string HostNotificationAttachedGuidText = "08f0d9c3-c770-4093-a3d0-02f3d90c24bc";
    public static readonly Guid HostNotificationAttachedGuid = Guid.Parse(HostNotificationAttachedGuidText);

    /// <summary>提醒提供方 GUID，需与 [NotificationProviderInfo] 上声明的一致。</summary>
    public const string NotificationProviderGuidText = "C1D4E8F2-9A06-4B73-8E51-3F2A7D9C6B10";

    /// <summary>Harmony patch 的唯一 id，便于幂等与精确回滚。</summary>
    public const string HarmonyId = "yibianhui.classexland.bell.hoststate";

    /// <summary>设置页 ID。</summary>
    public const string SettingsPageId = "yibianhui.classexland.bell";
}
