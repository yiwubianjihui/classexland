using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassExland.Features.Duty.Models;

/// <summary>
/// 值日表全局配置（ConfigFiles.Duty）。
/// </summary>
public sealed class DutySettings : ObservableRecipient
{
    private ObservableCollection<DutyPlan> _plans = new();
    private RotationSettings _rotation = new();
    private int _activePlanIndex;
    private bool _enabled = true;

    // 显示与提醒
    private int _classEndDelaySeconds = 60;
    private int _maskDurationSeconds = 5;
    private int _overlayDurationSeconds = 15;
    private bool _enableClassEndReminder = true;
    private bool _enableTtsSpeech;
    private string _reminderTitle = "值日提醒";
    private string _reminderTemplate = "{items}";
    private bool _showSlotBadge = true;

    // 创意功能
    private bool _enableChecklist;
    private bool _enableHolidaySkip;
    private ObservableCollection<HolidayRange> _holidays = new();
    private bool _enableLuckyPicker;
    private string _luckyPickerNamesFile = "";
    private ObservableCollection<string> _checks = new();

    [JsonIgnore]
    public string? ConfigPath { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            _enabled = value;
            OnPropertyChanged();
        }
    }

    [JsonPropertyName("plans")]
    public ObservableCollection<DutyPlan> Plans
    {
        get => _plans;
        set
        {
            if (Equals(value, _plans)) return;
            _plans = value ?? new ObservableCollection<DutyPlan>();
            OnPropertyChanged();
        }
    }

    /// <summary>批次轮换（跨多个 DutyPlan）。</summary>
    [JsonPropertyName("rotation")]
    public RotationSettings Rotation
    {
        get => _rotation;
        set
        {
            if (Equals(value, _rotation)) return;
            _rotation = value ?? new RotationSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>CLI 契约：duty.json 根级 activePlanIndex，由 `cex duty.rotate` 直接写入。</summary>
    [JsonPropertyName("activePlanIndex")]
    public int ActivePlanIndex
    {
        get => _activePlanIndex;
        set
        {
            if (value == _activePlanIndex) return;
            _activePlanIndex = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 兼容旧配置字段。下课延迟现由 BellModule 统一控制（BellSettings.ClassEndDelaySeconds），
    /// 值日提醒直接使用 BellMoment.ClassEnd.At，不再叠加本字段。保留仅为读取旧配置不报错。
    /// </summary>
    public int ClassEndDelaySeconds
    {
        get => _classEndDelaySeconds;
        set
        {
            if (value == _classEndDelaySeconds) return;
            _classEndDelaySeconds = Math.Clamp(value, 0, 300);
            OnPropertyChanged();
        }
    }

    public int MaskDurationSeconds
    {
        get => _maskDurationSeconds;
        set
        {
            if (value == _maskDurationSeconds) return;
            _maskDurationSeconds = Math.Clamp(value, 1, 60);
            OnPropertyChanged();
        }
    }

    public int OverlayDurationSeconds
    {
        get => _overlayDurationSeconds;
        set
        {
            if (value == _overlayDurationSeconds) return;
            _overlayDurationSeconds = Math.Clamp(value, 1, 120);
            OnPropertyChanged();
        }
    }

    public bool EnableClassEndReminder
    {
        get => _enableClassEndReminder;
        set
        {
            if (value == _enableClassEndReminder) return;
            _enableClassEndReminder = value;
            OnPropertyChanged();
        }
    }

    /// <summary>人名朗读：通过宿主语音通道（NotificationContent.IsSpeechEnabled），默认关。</summary>
    public bool EnableTtsSpeech
    {
        get => _enableTtsSpeech;
        set
        {
            if (value == _enableTtsSpeech) return;
            _enableTtsSpeech = value;
            OnPropertyChanged();
        }
    }

    public string ReminderTitle
    {
        get => _reminderTitle;
        set
        {
            if (value == _reminderTitle) return;
            _reminderTitle = value;
            OnPropertyChanged();
        }
    }

    /// <summary>{items} 会替换为项目与名单文本。</summary>
    public string ReminderTemplate
    {
        get => _reminderTemplate;
        set
        {
            if (value == _reminderTemplate) return;
            _reminderTemplate = value;
            OnPropertyChanged();
        }
    }

    public bool ShowSlotBadge
    {
        get => _showSlotBadge;
        set
        {
            if (value == _showSlotBadge) return;
            _showSlotBadge = value;
            OnPropertyChanged();
        }
    }

    public bool EnableChecklist
    {
        get => _enableChecklist;
        set
        {
            if (value == _enableChecklist) return;
            _enableChecklist = value;
            OnPropertyChanged();
        }
    }

    public bool EnableHolidaySkip
    {
        get => _enableHolidaySkip;
        set
        {
            if (value == _enableHolidaySkip) return;
            _enableHolidaySkip = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<HolidayRange> Holidays
    {
        get => _holidays;
        set
        {
            if (Equals(value, _holidays)) return;
            _holidays = value ?? new ObservableCollection<HolidayRange>();
            OnPropertyChanged();
        }
    }

    public bool EnableLuckyPicker
    {
        get => _enableLuckyPicker;
        set
        {
            if (value == _enableLuckyPicker) return;
            _enableLuckyPicker = value;
            OnPropertyChanged();
        }
    }

    /// <summary>本地名单文件路径；仅读取本地文件，绝不上传。</summary>
    public string LuckyPickerNamesFile
    {
        get => _luckyPickerNamesFile;
        set
        {
            if (value == _luckyPickerNamesFile) return;
            _luckyPickerNamesFile = value;
            OnPropertyChanged();
        }
    }

    /// <summary>值日完成勾选记录：yyyy-MM-dd|项目|姓名。</summary>
    public ObservableCollection<string> Checks
    {
        get => _checks;
        set
        {
            if (Equals(value, _checks)) return;
            _checks = value ?? new ObservableCollection<string>();
            OnPropertyChanged();
        }
    }
}

/// <summary>本地节假日区间，仅用于离线跳过判断。</summary>
public sealed class HolidayRange : ObservableRecipient
{
    private DateTime _start;
    private DateTime _end;
    private string _name = "假期";

    public DateTime Start
    {
        get => _start;
        set
        {
            if (value == _start) return;
            _start = value;
            OnPropertyChanged();
        }
    }

    public DateTime End
    {
        get => _end;
        set
        {
            if (value == _end) return;
            _end = value;
            OnPropertyChanged();
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (value == _name) return;
            _name = value;
            OnPropertyChanged();
        }
    }
}

/// <summary>主界面组件设置。</summary>
public sealed class DutyComponentSettings : ObservableRecipient
{
    private string _title = "今日值日";
    private bool _showOnlyOnBreak = true;
    private bool _showOnAfterSchool;
    private int _maxItems = 3;

    public string Title
    {
        get => _title;
        set
        {
            if (value == _title) return;
            _title = value;
            OnPropertyChanged();
        }
    }

    public bool ShowOnlyOnBreak
    {
        get => _showOnlyOnBreak;
        set
        {
            if (value == _showOnlyOnBreak) return;
            _showOnlyOnBreak = value;
            OnPropertyChanged();
        }
    }

    public bool ShowOnAfterSchool
    {
        get => _showOnAfterSchool;
        set
        {
            if (value == _showOnAfterSchool) return;
            _showOnAfterSchool = value;
            OnPropertyChanged();
        }
    }

    /// <summary>单行最多显示的项目数，其余省略。</summary>
    public int MaxItems
    {
        get => _maxItems;
        set
        {
            if (value == _maxItems) return;
            _maxItems = Math.Clamp(value, 1, 10);
            OnPropertyChanged();
        }
    }
}
