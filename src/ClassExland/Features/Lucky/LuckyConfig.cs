using System.Text.Json;
using System.Text.Json.Serialization;
using ClassExland.Contracts;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Lucky;

/// <summary>
/// LuckyPicker 联动配置（lucky.json）。
///
/// 两个真实文件位置来自 LuckyPicker 源码（desktop/History.cs、desktop/LuckyPicker.cs）：
///   · 抽选记录：%LOCALAPPDATA%\LuckyPicker\history.json
///   · 名单：    %ProgramData%\LuckyPicker\students.json
/// 用户也可以手工指定路径（便携版放在程序目录时会不一样）。
/// </summary>
public sealed class LuckyConfig
{
    /// <summary>总开关。关掉后不读文件、不广播。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>LuckyPicker 抽选记录文件；留空时用 %LOCALAPPDATA%\LuckyPicker\history.json。</summary>
    public string HistoryPath { get; set; } = "";

    /// <summary>轮询间隔（秒）。LuckyPicker 是独立程序，没有 IPC，只能靠看文件变化。</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>广播遮罩时长（秒）。</summary>
    public int MaskDurationSeconds { get; set; } = 5;

    /// <summary>广播正文时长（秒）。</summary>
    public int OverlayDurationSeconds { get; set; } = 12;

    /// <summary>正文文案模板，{names} 会替换成抽到的名字（遮罩上直接显示人名）。</summary>
    public string OverlayTemplate { get; set; } = "抽到了：{names}";

    /// <summary>是否也发顶部横幅遮罩（默认关：只发正文，不挡主界面）。</summary>
    public bool EnableMask { get; set; } = false;

    /// <summary>遮罩左侧图标（Fluent 图标字形）。</summary>
    public string MaskLeftIcon { get; set; } = "\uE823";

    /// <summary>遮罩右侧图标；留空则不显示右侧图标。</summary>
    public string MaskRightIcon { get; set; } = "";

    /// <summary>是否朗读（走宿主已装的语音引擎）。</summary>
    public bool EnableSpeech { get; set; } = false;

    /// <summary>启动时忽略已有的历史，只广播之后新产生的记录。</summary>
    public bool SkipExistingOnStart { get; set; } = true;

    public static LuckyConfig Sanitize(LuckyConfig? raw)
    {
        var c = raw ?? new LuckyConfig();
        c.HistoryPath = (c.HistoryPath ?? "").Trim();
        c.PollIntervalSeconds = c.PollIntervalSeconds is < 1 or > 600 ? 2 : c.PollIntervalSeconds;
        c.MaskDurationSeconds = c.MaskDurationSeconds is < 1 or > 120 ? 5 : c.MaskDurationSeconds;
        c.OverlayDurationSeconds = c.OverlayDurationSeconds is < 1 or > 300 ? 12 : c.OverlayDurationSeconds;
        c.OverlayTemplate = string.IsNullOrWhiteSpace(c.OverlayTemplate) ? "抽到了：{names}" : c.OverlayTemplate;
        c.MaskLeftIcon = c.MaskLeftIcon ?? "\uE823";
        c.MaskRightIcon = c.MaskRightIcon ?? "";
        return c;
    }

    /// <summary>实际使用的历史文件路径。</summary>
    public string ResolveHistoryPath()
    {
        if (!string.IsNullOrWhiteSpace(HistoryPath)) return HistoryPath;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LuckyPicker", "history.json");
    }
}

/// <summary>LuckyPicker history.json 里的一条记录（两版 LuckyPicker 格式一致）。</summary>
public sealed class LuckyHistoryEntry
{
    [JsonPropertyName("time")]
    public string Time { get; set; } = "";

    /// <summary>单人时是名字；连抽时是「连抽： A、B、C」这样的整串。</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("classId")]
    public string ClassId { get; set; } = "";
}

public sealed class LuckyConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public LuckyConfigStore(IPluginPaths paths)
    {
        _path = paths.Get(ConfigFiles.Lucky);
    }

    public string ConfigPath => _path;

    public LuckyConfig Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                var defaults = LuckyConfig.Sanitize(new LuckyConfig());
                Save(defaults);
                return defaults;
            }
            return LuckyConfig.Sanitize(
                JsonSerializer.Deserialize<LuckyConfig>(File.ReadAllText(_path), JsonOptions));
        }
        catch
        {
            // 配置坏了也不能让插件崩，退回默认（关闭状态）。
            return LuckyConfig.Sanitize(new LuckyConfig());
        }
    }

    public bool Save(LuckyConfig config)
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(LuckyConfig.Sanitize(config), JsonOptions));
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
