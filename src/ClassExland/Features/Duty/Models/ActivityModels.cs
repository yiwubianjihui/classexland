using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassExland.Features.Duty.Models;

/// <summary>
/// 特殊活动（如“试写作业”）的一组名单，按星期配置，语义对齐 TrialHomework 的 TrialGroup。
/// </summary>
public sealed class ActivityGroup : ObservableRecipient
{
    private string _name = "新组";
    private string _monday = "";
    private string _tuesday = "";
    private string _wednesday = "";
    private string _thursday = "";
    private string _friday = "";
    private string _saturday = "";
    private string _sunday = "";

    public string Name
    {
        get => _name;
        set { if (value == _name) return; _name = value; OnPropertyChanged(); }
    }

    public string Monday { get => _monday; set { if (value == _monday) return; _monday = value; OnPropertyChanged(); } }
    public string Tuesday { get => _tuesday; set { if (value == _tuesday) return; _tuesday = value; OnPropertyChanged(); } }
    public string Wednesday { get => _wednesday; set { if (value == _wednesday) return; _wednesday = value; OnPropertyChanged(); } }
    public string Thursday { get => _thursday; set { if (value == _thursday) return; _thursday = value; OnPropertyChanged(); } }
    public string Friday { get => _friday; set { if (value == _friday) return; _friday = value; OnPropertyChanged(); } }
    public string Saturday { get => _saturday; set { if (value == _saturday) return; _saturday = value; OnPropertyChanged(); } }
    public string Sunday { get => _sunday; set { if (value == _sunday) return; _sunday = value; OnPropertyChanged(); } }

    public string this[DayOfWeek day] => day switch
    {
        DayOfWeek.Monday => Monday,
        DayOfWeek.Tuesday => Tuesday,
        DayOfWeek.Wednesday => Wednesday,
        DayOfWeek.Thursday => Thursday,
        DayOfWeek.Friday => Friday,
        DayOfWeek.Saturday => Saturday,
        _ => Sunday
    };
}

/// <summary>
/// 一个轮流特殊活动。触发时间走 BellModule 时间轴，不单独起定时器。
/// </summary>
public sealed class SpecialActivity : ObservableRecipient
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "特殊活动";
    private bool _enabled = true;
    private ObservableCollection<TimeSpan> _reminderTimes = new();
    private RotationMode _rotationMode = RotationMode.Month;
    private DateTime _customStartDate = DateTime.Today;
    private int _activeGroupIndex;
    private ObservableCollection<ActivityGroup> _groups = new();
    private string _messageTemplate = "{names}";
    private bool _weekConfirmed;
    private string _confirmedWeekKey = "";
    private bool _includeDutyNames = true;
    private bool _schoolDaysOnly = true;

    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set { if (value == _id) return; _id = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set { if (value == _name) return; _name = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("enabled")]
    public bool Enabled
    {
        get => _enabled;
        set { if (value == _enabled) return; _enabled = value; OnPropertyChanged(); }
    }

    /// <summary>periods 的兼容别名（旧 CLI 字段）；真值字段是 reminderTimes，故不参与序列化。</summary>
    [JsonIgnore]
    public List<string> Periods
    {
        get => ReminderTimes.Select(t => t.ToString(@"hh\:mm")).ToList();
        set
        {
            var list = (value ?? new List<string>())
                .Select(s => TimeSpan.TryParse((s ?? "").Replace('：', ':'), out var t) ? t : (TimeSpan?)null)
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .ToList();
            ReminderTimes = new ObservableCollection<TimeSpan>(list);
            OnPropertyChanged();
        }
    }

    /// <summary>CLI 契约字段：reminderTimes（TimeSpan 序列化为 HH:mm:ss 字符串数组）。</summary>
    [JsonPropertyName("reminderTimes")]
    public ObservableCollection<TimeSpan> ReminderTimes
    {
        get => _reminderTimes;
        set { if (Equals(value, _reminderTimes)) return; _reminderTimes = value ?? new ObservableCollection<TimeSpan>(); OnPropertyChanged(); }
    }

    [JsonPropertyName("rotationMode")]
    public RotationMode RotationMode
    {
        get => _rotationMode;
        set { if (value == _rotationMode) return; _rotationMode = value; OnPropertyChanged(); }
    }

    public DateTime CustomStartDate
    {
        get => _customStartDate;
        set { if (value == _customStartDate) return; _customStartDate = value; OnPropertyChanged(); }
    }

    public int ActiveGroupIndex
    {
        get => _activeGroupIndex;
        set { if (value == _activeGroupIndex) return; _activeGroupIndex = value; OnPropertyChanged(); }
    }

    public ObservableCollection<ActivityGroup> Groups
    {
        get => _groups;
        set { if (Equals(value, _groups)) return; _groups = value ?? new ObservableCollection<ActivityGroup>(); OnPropertyChanged(); }
    }

    /// <summary>{names} 会替换为当天名单；活动提醒会再附一段当前值日名单。</summary>
    public string MessageTemplate
    {
        get => _messageTemplate;
        set { if (value == _messageTemplate) return; _messageTemplate = value; OnPropertyChanged(); }
    }

    /// <summary>周确认：未确认的周不弹活动提醒。</summary>
    public bool WeekConfirmed
    {
        get => _weekConfirmed;
        set { if (value == _weekConfirmed) return; _weekConfirmed = value; OnPropertyChanged(); }
    }

    public string ConfirmedWeekKey
    {
        get => _confirmedWeekKey;
        set { if (value == _confirmedWeekKey) return; _confirmedWeekKey = value; OnPropertyChanged(); }
    }

    public bool IncludeDutyNames
    {
        get => _includeDutyNames;
        set { if (value == _includeDutyNames) return; _includeDutyNames = value; OnPropertyChanged(); }
    }

    public bool SchoolDaysOnly
    {
        get => _schoolDaysOnly;
        set { if (value == _schoolDaysOnly) return; _schoolDaysOnly = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public string ReminderTimesText
    {
        get => string.Join(", ", ReminderTimes.Select(t => t.ToString(@"hh\:mm")));
        set
        {
            var list = (value ?? "")
                .Split(new[] { ',', '，', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => TimeSpan.TryParse(s.Replace('：', ':'), out var t) ? t : (TimeSpan?)null)
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .ToList();
            ReminderTimes = new ObservableCollection<TimeSpan>(list);
            OnPropertyChanged();
        }
    }
}

/// <summary>
/// 特殊活动全局配置。activities.json 的 CLI 契约是「JSON 数组」，因此本类不直接序列化，
/// 由 LoadFromFile/SaveToFile 对 Activities 数组单独读写；全局选项存到独立的 activity-options.json。
/// </summary>
public sealed class ActivitySettings : ObservableRecipient
{
    private ObservableCollection<SpecialActivity> _activities = new();
    private bool _enableAdvanceNotice = true;
    private int _advanceMinutes = 10;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [JsonIgnore]
    public string? ConfigPath { get; set; }

    [JsonIgnore]
    public string? OptionsPath { get; set; }

    [JsonIgnore]
    public ObservableCollection<SpecialActivity> Activities
    {
        get => _activities;
        set { if (Equals(value, _activities)) return; _activities = value ?? new ObservableCollection<SpecialActivity>(); OnPropertyChanged(); }
    }

    [JsonIgnore]
    public bool EnableAdvanceNotice
    {
        get => _enableAdvanceNotice;
        set { if (value == _enableAdvanceNotice) return; _enableAdvanceNotice = value; OnPropertyChanged(); }
    }

    /// <summary>活动开始前提前提示的分钟数，默认 10。</summary>
    [JsonIgnore]
    public int AdvanceMinutes
    {
        get => _advanceMinutes;
        set { if (value == _advanceMinutes) return; _advanceMinutes = Math.Clamp(value, 0, 120); OnPropertyChanged(); }
    }

    public static ActivitySettings LoadFromFile(string path, string optionsPath)
    {
        var settings = new ActivitySettings { ConfigPath = path, OptionsPath = optionsPath };
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (json.TrimStart().StartsWith('['))
                {
                    var list = JsonSerializer.Deserialize<List<SpecialActivity>>(json, JsonOptions) ?? new List<SpecialActivity>();
                    foreach (var a in list) settings.Activities.Add(a);
                }
                else
                {
                    // 兼容早期对象格式 { "activities": [...] }。
                    var legacy = JsonSerializer.Deserialize<LegacyActivityFile>(json, JsonOptions);
                    if (legacy?.Activities != null)
                        foreach (var a in legacy.Activities) settings.Activities.Add(a);
                }
            }
        }
        catch { /* 损坏文件按空配置处理，避免插件被禁用 */ }

        try
        {
            if (File.Exists(optionsPath))
            {
                var opt = JsonSerializer.Deserialize<ActivityOptions>(File.ReadAllText(optionsPath), JsonOptions);
                if (opt != null)
                {
                    settings.EnableAdvanceNotice = opt.EnableAdvanceNotice;
                    settings.AdvanceMinutes = opt.AdvanceMinutes;
                }
            }
        }
        catch { }
        return settings;
    }

    public void SaveToFile()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ConfigPath)) return;
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Activities.ToList(), JsonOptions), System.Text.Encoding.UTF8);
            File.Move(temp, ConfigPath, true);
        }
        catch { /* 保存失败不影响内存状态 */ }

        try
        {
            if (string.IsNullOrWhiteSpace(OptionsPath)) return;
            var dir = Path.GetDirectoryName(OptionsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var options = new ActivityOptions
            {
                EnableAdvanceNotice = EnableAdvanceNotice,
                AdvanceMinutes = AdvanceMinutes
            };
            File.WriteAllText(OptionsPath, JsonSerializer.Serialize(options, JsonOptions), System.Text.Encoding.UTF8);
        }
        catch { }
    }

    private sealed class ActivityOptions
    {
        public bool EnableAdvanceNotice { get; set; } = true;
        public int AdvanceMinutes { get; set; } = 10;
    }

    private sealed class LegacyActivityFile
    {
        [JsonPropertyName("activities")]
        public List<SpecialActivity>? Activities { get; set; }
    }
}
