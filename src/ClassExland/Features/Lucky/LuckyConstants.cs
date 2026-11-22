namespace ClassExland.Features.Lucky;

public static class LuckyConstants
{
    /// <summary>提醒提供方 GUID，需与 [NotificationProviderInfo] 上声明的一致。</summary>
    public const string NotificationProviderGuidText = "3B7D2E14-9A5C-4F68-8D21-6C0E9B4A7F53";
}

/// <summary>LuckyPicker 联动的对外契约（Bridge / CLI 反射调用）。</summary>
public interface ILuckyService
{
    /// <summary>当前状态（只读）。</summary>
    object? Status { get; }

    /// <summary>手动广播一段文本（试用/命令行）。</summary>
    Task<string> AnnounceAsync(string text);
}
