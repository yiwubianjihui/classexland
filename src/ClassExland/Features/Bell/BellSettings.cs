using System.Text.Json;
using System.Text.Json.Serialization;
using ClassExland.Contracts;
using ClassIsland.Shared.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassExland.Features.Bell;

/// <summary>
/// 预告提醒落在前一课间之外时的处理策略。
/// </summary>
public enum PreNoticeOverflowPolicy
{
    /// <summary>裁剪到前一课间中点（默认）。</summary>
    ClipToMidpoint = 0,
    /// <summary>跳过该条预告提醒。</summary>
    Skip = 1,
    /// <summary>保持原时间不变（不推荐，会与课间重叠）。</summary>
    Keep = 2
}

/// <summary>
/// 单节课的预备铃模式。
/// - Fixed：用固定提前量（PrepSeconds）。
/// - ByBreak：按前一节课间时长自动算——10 分钟课间提前 2 分钟、15 分钟提前 3 分钟、无课间不响。
/// - Off：无预备铃（这一节到上课时刻只切界面状态，不响铃、不发预告）。
/// </summary>
public enum BellPrepMode
{
    Fixed = 0,
    ByBreak = 1,
    Off = 2
}

/// <summary>
/// bell.json 中的单条时间片配置。CLI 的 bell list/set/doctor 直接读写此结构：
/// 根为 JSON 数组，每项 id / prepSeconds / enabled，prep 为 CLI 兼容别名（本模块以 prepSeconds 为真值）。
/// </summary>
public sealed class BellSlotConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("prepSeconds")]
    public int PrepSeconds { get; set; } = 120;

    /// <summary>默认关闭：预备铃要逐节显式启用，不再对所有课自动生效。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("slot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Slot { get; set; }

    [JsonPropertyName("subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; set; }

    /// <summary>null = 跟随全局默认模式；有值则这一节覆盖。</summary>
    [JsonPropertyName("prepMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BellPrepMode? PrepMode { get; set; }
}

/// <summary>
/// 写入 <c>TimeLayoutItem.AttachedObjects[我方GUID]</c> 的附加设置。
/// 只有这一个字段属于我们，绝不触碰宿主或其他插件的附加设置。
/// 实现 <see cref="IAttachedSettings"/> 后，宿主档案编辑器的「附加设置」区域
/// 才能通过 <see cref="BellSlotSettingsControl"/> 直接读写它。
/// </summary>
public sealed class BellSlotSettings : IAttachedSettings
{
    [JsonPropertyName("prepSeconds")]
    public int PrepSeconds { get; set; } = 120;

    /// <summary>
    /// 这一节的预备铃模式。null = 跟随全局默认（旧档案没有这个字段，保持原行为）。
    /// ByBreak = 按课间时长自动算；Off = 无预备铃；Fixed = 用上面的固定秒数。
    /// </summary>
    [JsonPropertyName("prepMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BellPrepMode? PrepMode { get; set; }

    /// <summary>
    /// 宿主的附加设置启用开关。默认 **开启**（与历史行为一致，避免旧档案静默失效）；
    /// 新建一节时由 <see cref="BellSlotSettings.CreateDisabled"/> 显式关掉，
    /// 这样"预备铃逐节按需启用"和"旧档案保持原状"两个要求都满足。
    /// </summary>
    public bool IsAttachSettingsEnabled { get; set; } = true;

    /// <summary>新建一节时的默认对象：预备铃默认不启用。</summary>
    public static BellSlotSettings CreateDisabled() => new() { IsAttachSettingsEnabled = false };
}

/// <summary>
/// 档案写入预览条目。
/// </summary>
public sealed record BellProfileChange(
    Guid TimeLayoutId,
    string SlotKey,
    string SlotDisplay,
    int OldPrepSeconds,
    int NewPrepSeconds,
    bool WillDisableHostNotification);

/// <summary>
/// 可回滚的档案写入快照。
/// </summary>
public sealed record BellProfileRollbackEntry(
    Guid TimeLayoutId,
    string SlotKey,
    bool HadOurAttached,
    object? OurAttachedOld,
    bool HadHostAttached,
    object? HostAttachedOld,
    int OldPrepSeconds,
    bool HadPrepOverride);

/// <summary>
/// 插件自有配置。
/// - bell.json（ConfigFiles.Bell）：根数组，存每节课的 prepSeconds / enabled，供 CLI 直接读写。
/// - bell-options.json：全局参数（默认值、延迟、补发窗口、Harmony 开关、铃声），避免污染 CLI 的数组结构。
/// 每节课提前量同时写入档案 TimeLayouts[].Layouts[].AttachedObjects[我方GUID]；此处为兜底副本。
/// </summary>
public sealed partial class BellSettings : ObservableRecipient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>特殊值：这一节显式关闭了预备铃，不响也不预热。</summary>
    public const int NoPrep = -1;

    private readonly string _path;
    private readonly string _optionsPath;
    private bool _loaded;

    private bool _enableBell = true;
    private int _defaultPrepSeconds = 120;
    private int _classEndDelaySeconds = 60;
    private int _preNoticeLeadSeconds = 60;
    private PreNoticeOverflowPolicy _preNoticeOverflow = PreNoticeOverflowPolicy.ClipToMidpoint;
    private int _makeupWindowSeconds = 1800;
    private bool _enableHostStateOverride = true;
    private bool _enableBellSound = true;
    private string _bellSoundPath = "";
    private float _bellVolume = 0.8f;
    private BellPrepMode _defaultPrepMode = BellPrepMode.Fixed;
    private List<BellBreakPrepRule> _breakPrepRules = new(BellBreakPrepRule.Defaults);
    private List<BellSlotConfig> _slots = new();

    public BellSettings(IPluginPaths paths)
    {
        _path = paths.Get(ConfigFiles.Bell);
        _optionsPath = paths.Get("bell-options.json");
        Load();
    }

    public bool EnableBell
    {
        get => _enableBell;
        set => SetAndSave(ref _enableBell, value);
    }

    /// <summary>默认预备铃提前量（秒）。</summary>
    public int DefaultPrepSeconds
    {
        get => _defaultPrepSeconds;
        set => SetAndSave(ref _defaultPrepSeconds, value);
    }

    /// <summary>下课事件延迟 D（秒），范围 0-300。</summary>
    public int ClassEndDelaySeconds
    {
        get => _classEndDelaySeconds;
        set => SetAndSave(ref _classEndDelaySeconds, value);
    }

    /// <summary>预告提醒相对预备铃的提前量（默认 60 秒）。</summary>
    public int PreNoticeLeadSeconds
    {
        get => _preNoticeLeadSeconds;
        set => SetAndSave(ref _preNoticeLeadSeconds, value);
    }

    public PreNoticeOverflowPolicy PreNoticeOverflow
    {
        get => _preNoticeOverflow;
        set => SetAndSave(ref _preNoticeOverflow, value);
    }

    /// <summary>启动错过时点的补发窗口（秒），默认 30 分钟。</summary>
    public int MakeupWindowSeconds
    {
        get => _makeupWindowSeconds;
        set => SetAndSave(ref _makeupWindowSeconds, value);
    }

    /// <summary>
    /// Harmony 宿主状态覆盖。默认 true（用户已明确选择 Harmony 路径）；失败会自动降级为虚拟状态。
    /// </summary>
    public bool EnableHostStateOverride
    {
        get => _enableHostStateOverride;
        set => SetAndSave(ref _enableHostStateOverride, value);
    }

    public bool EnableBellSound
    {
        get => _enableBellSound;
        set => SetAndSave(ref _enableBellSound, value);
    }

    public string BellSoundPath
    {
        get => _bellSoundPath;
        set => SetAndSave(ref _bellSoundPath, value);
    }

    public float BellVolume
    {
        get => _bellVolume;
        set => SetAndSave(ref _bellVolume, value);
    }

    /// <summary>
    /// 全局默认预备铃模式。Fixed=固定提前量（兼容旧行为）；ByBreak=按课间时长；Off=全体无预备铃。
    /// 单节可通过档案附加设置或 bell.json 覆盖。
    /// </summary>
    public BellPrepMode DefaultPrepMode
    {
        get => _defaultPrepMode;
        set => SetAndSave(ref _defaultPrepMode, value);
    }

    /// <summary>按课间时长算提前量的规则表（ByBreak 模式用）。</summary>
    public IReadOnlyList<BellBreakPrepRule> BreakPrepRules => _breakPrepRules;

    /// <summary>每节课配置（bell.json 的 JSON 数组成员）。</summary>
    public IReadOnlyList<BellSlotConfig> Slots => _slots;

    /// <summary>读取某时间片的有效提前量：未配置或 disabled 时回退到默认值。</summary>
    public int GetPrep(string slotId)
    {
        var slot = _slots.FirstOrDefault(s => s.Id == slotId);
        // 显式关闭 → 这一节不响预备铃（用 -1 表示"无预备铃"，而不是回退到默认值）。
        if (slot is { Enabled: false }) return NoPrep;
        if (slot == null) return DefaultPrepSeconds;
        return Math.Clamp(slot.PrepSeconds, 0, 600);
    }

    /// <summary>写入/更新某时间片的提前量（CLI bell set 的等价内部入口）。</summary>
    public void SetPrep(string slotId, int prepSeconds, string? slotLabel = null)
    {
        var slot = _slots.FirstOrDefault(s => s.Id == slotId);
        if (slot == null)
        {
            slot = new BellSlotConfig { Id = slotId, Slot = slotLabel };
            _slots.Add(slot);
        }
        slot.PrepSeconds = Math.Clamp(prepSeconds, 0, 600);
        slot.Enabled = true;
        if (!string.IsNullOrWhiteSpace(slotLabel)) slot.Slot = slotLabel;
        Save();
    }

    /// <summary>移除某时间片的覆盖配置，使其回退到默认值。</summary>
    public void RemovePrep(string slotId)
    {
        var removed = _slots.RemoveAll(s => s.Id == slotId);
        if (removed > 0) Save();
    }

    /// <summary>某时间片是否存在显式覆盖配置。</summary>
    public bool HasPrepOverride(string slotId) => _slots.Any(s => s.Id == slotId);

    /// <summary>
    /// 批量写入每节课提前量。value 为 null 表示删除该节的覆盖、回退到默认值。
    /// 只落盘一次，供设置页"应用每节课设置"与 CLI 使用。
    /// </summary>
    public void ApplyPreps(IReadOnlyDictionary<string, int?> preps, IReadOnlyDictionary<string, string?>? labels = null)
    {
        foreach (var (slotId, prep) in preps)
        {
            if (prep is null)
            {
                _slots.RemoveAll(s => s.Id == slotId);
                continue;
            }

            var slot = _slots.FirstOrDefault(s => s.Id == slotId);
            if (slot is null)
            {
                slot = new BellSlotConfig { Id = slotId };
                _slots.Add(slot);
            }
            slot.PrepSeconds = Math.Clamp(prep.Value, 0, 600);
            slot.Enabled = true;
            if (labels is not null && labels.TryGetValue(slotId, out var label) && !string.IsNullOrWhiteSpace(label))
                slot.Slot = label;
        }
        Save();
    }

    /// <summary>
    /// 从磁盘重新读取。CLI 或远程同步直接改了 bell.json / bell-options.json 后必须调用，
    /// 否则内存里仍是启动时的快照，界面上看不到变化、时间轴也按旧值走。
    /// </summary>
    public void Reload()
    {
        ResetToDefaults();
        try
        {
            LoadSlots();
            LoadOptions();
        }
        catch
        {
            // 配置损坏时保留默认值，不让插件因配置问题崩溃。
        }
        OnPropertyChanged(string.Empty);
    }

    private void ResetToDefaults()
    {
        _slots = new List<BellSlotConfig>();
        _enableBell = true;
        _defaultPrepSeconds = 120;
        _classEndDelaySeconds = 60;
        _preNoticeLeadSeconds = 60;
        _preNoticeOverflow = PreNoticeOverflowPolicy.ClipToMidpoint;
        _makeupWindowSeconds = 1800;
        _enableHostStateOverride = true;
        _enableBellSound = true;
        _bellSoundPath = "";
        _bellVolume = 0.8f;
        _defaultPrepMode = BellPrepMode.Fixed;
        _breakPrepRules = new List<BellBreakPrepRule>(BellBreakPrepRule.Defaults);
    }

    /// <summary>供计算使用的选项快照（纯逻辑层不依赖本类）。</summary>
    public BellTimelineOptions ToOptions() => new()
    {
        PrepSecondsDefault = DefaultPrepSeconds,
        ClassEndDelaySeconds = ClassEndDelaySeconds,
        PreNoticeLeadSeconds = PreNoticeLeadSeconds,
        PreNoticeOverflow = PreNoticeOverflow,
        MakeupWindowSeconds = MakeupWindowSeconds,
        PrepMode = DefaultPrepMode,
        BreakPrepRules = _breakPrepRules
    };

    private void SetAndSave<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
        Save();
    }

    private void Load()
    {
        var hadSlots = File.Exists(_path);
        var hadOptions = File.Exists(_optionsPath);
        var ok = true;

        try
        {
            LoadSlots();
            LoadOptions();
        }
        catch (Exception ex)
        {
            ok = false;
            // 不写日志也不落盘：读失败就保留默认值，下次启动重试，绝不覆盖用户的文件。
        }
        finally
        {
            _loaded = true;
        }

        // 关键：只有「文件本来就不存在」时才落一份模板。
        // 以前这里无条件 Save()，等于每次启动都拿内存里的值回写磁盘——
        // 一旦某次读取失败（或文件被清成 []），就会把真实配置永久冲成默认值，
        // 用户看到的就是"每次更新都重置了配置"。
        if (!ok) return;
        if (!hadSlots || !hadOptions) Save();
    }

    private void LoadSlots()
    {
        if (!File.Exists(_path)) return;
        var json = File.ReadAllText(_path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            _slots = JsonSerializer.Deserialize<List<BellSlotConfig>>(json, JsonOptions) ?? new();
            return;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            // 兼容早期对象格式：根对象上的 slots / bells / items 数组，或 PrepOverrides 字典。
            foreach (var name in new[] { "slots", "bells", "items" })
            {
                if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    _slots = JsonSerializer.Deserialize<List<BellSlotConfig>>(arr.GetRawText(), JsonOptions) ?? new();
                    return;
                }
            }
            if (root.TryGetProperty("PrepOverrides", out var dict) && dict.ValueKind == JsonValueKind.Object)
            {
                _slots = dict.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Number)
                    .Select(p => new BellSlotConfig
                    {
                        Id = p.Name,
                        PrepSeconds = p.Value.GetInt32(),
                        Enabled = true
                    })
                    .ToList();
            }
        }
    }

    private void LoadOptions()
    {
        if (!File.Exists(_optionsPath)) return;
        var loaded = JsonSerializer.Deserialize<BellOptionsDto>(File.ReadAllText(_optionsPath), JsonOptions);
        if (loaded == null) return;
        _enableBell = loaded.EnableBell;
        _defaultPrepSeconds = loaded.DefaultPrepSeconds;
        _classEndDelaySeconds = loaded.ClassEndDelaySeconds;
        _preNoticeLeadSeconds = loaded.PreNoticeLeadSeconds;
        _preNoticeOverflow = loaded.PreNoticeOverflow;
        _makeupWindowSeconds = loaded.MakeupWindowSeconds;
        _enableHostStateOverride = loaded.EnableHostStateOverride;
        _enableBellSound = loaded.EnableBellSound;
        _bellSoundPath = loaded.BellSoundPath ?? "";
        _bellVolume = loaded.BellVolume;
        _defaultPrepMode = loaded.DefaultPrepMode;
        if (loaded.BreakPrepRules is { Count: > 0 }) _breakPrepRules = loaded.BreakPrepRules;
    }

    public void Save()
    {
        if (!_loaded) return;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // bell.json 根数组：真值字段 prepSeconds；不写 prep 别名（由 CLI 自行写入）。
            File.WriteAllText(_path, JsonSerializer.Serialize(_slots, JsonOptions));

            var options = new BellOptionsDto
            {
                EnableBell = _enableBell,
                DefaultPrepSeconds = _defaultPrepSeconds,
                ClassEndDelaySeconds = _classEndDelaySeconds,
                PreNoticeLeadSeconds = _preNoticeLeadSeconds,
                PreNoticeOverflow = _preNoticeOverflow,
                MakeupWindowSeconds = _makeupWindowSeconds,
                EnableHostStateOverride = _enableHostStateOverride,
                EnableBellSound = _enableBellSound,
                BellSoundPath = _bellSoundPath,
                BellVolume = _bellVolume,
                DefaultPrepMode = _defaultPrepMode,
                BreakPrepRules = _breakPrepRules
            };
            File.WriteAllText(_optionsPath, JsonSerializer.Serialize(options, JsonOptions));
        }
        catch
        {
            // 写配置失败不能影响主流程。
        }
    }

    private sealed class BellOptionsDto
    {
        public bool EnableBell { get; set; } = true;
        public int DefaultPrepSeconds { get; set; } = 120;
        public int ClassEndDelaySeconds { get; set; } = 60;
        public int PreNoticeLeadSeconds { get; set; } = 60;
        public PreNoticeOverflowPolicy PreNoticeOverflow { get; set; } = PreNoticeOverflowPolicy.ClipToMidpoint;
        public int MakeupWindowSeconds { get; set; } = 1800;
        public bool EnableHostStateOverride { get; set; } = true;
        public bool EnableBellSound { get; set; } = true;
        public string BellSoundPath { get; set; } = "";
        public float BellVolume { get; set; } = 0.8f;
        public BellPrepMode DefaultPrepMode { get; set; } = BellPrepMode.Fixed;
        public List<BellBreakPrepRule> BreakPrepRules { get; set; } = new(BellBreakPrepRule.Defaults);
    }
}
