using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassExland.Features.Duty.Models;

/// <summary>
/// 一个时段，例如 07:00–11:50 的“上午”。
/// </summary>
public sealed class TimeSlot : ObservableRecipient
{
    private string _label = "上午";
    private TimeSpan _start = new(7, 0, 0);
    private TimeSpan _end = new(11, 50, 0);

    public string Label
    {
        get => _label;
        set
        {
            if (value == _label) return;
            _label = value;
            OnPropertyChanged();
        }
    }

    public TimeSpan Start
    {
        get => _start;
        set
        {
            if (value == _start) return;
            _start = value;
            OnPropertyChanged();
        }
    }

    public TimeSpan End
    {
        get => _end;
        set
        {
            if (value == _end) return;
            _end = value;
            OnPropertyChanged();
        }
    }

    /// <summary>仅供界面显示的 HH:mm 文本，避免 DataGrid 直接编辑 TimeSpan 的格式化问题。</summary>
    [JsonIgnore]
    public string StartText
    {
        get => Start.ToString(@"hh\:mm");
        set
        {
            if (TimeSpan.TryParse(value, out var t)) Start = t;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string EndText
    {
        get => End.ToString(@"hh\:mm");
        set
        {
            if (TimeSpan.TryParse(value, out var t)) End = t;
            OnPropertyChanged();
        }
    }
}

/// <summary>
/// DataGrid 的一行：一个值日项目。人员以分隔文本保存，方便表格内直接编辑与迁移。
/// </summary>
public sealed class DutyItem : ObservableRecipient
{
    private string _project = "";
    private string _persons = "";
    private string? _color;
    private bool _isSweep;
    private bool _remindEnabled = true;
    private string _slotLabel = "";
    private int _weekday = -1;

    public string Project
    {
        get => _project;
        set
        {
            if (value == _project) return;
            _project = value;
            OnPropertyChanged();
        }
    }

    /// <summary>人员名单，使用顿号/逗号/空格等分隔。</summary>
    public string Persons
    {
        get => _persons;
        set
        {
            if (value == _persons) return;
            _persons = value;
            OnPropertyChanged();
        }
    }

    public string? Color
    {
        get => _color;
        set
        {
            if (value == _color) return;
            _color = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是否大扫除（可用整屏提醒样式）。</summary>
    public bool IsSweep
    {
        get => _isSweep;
        set
        {
            if (value == _isSweep) return;
            _isSweep = value;
            OnPropertyChanged();
        }
    }

    public bool RemindEnabled
    {
        get => _remindEnabled;
        set
        {
            if (value == _remindEnabled) return;
            _remindEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>所属时段标签；为空表示适用于当天所有时段。</summary>
    public string SlotLabel
    {
        get => _slotLabel;
        set
        {
            if (value == _slotLabel) return;
            _slotLabel = value;
            OnPropertyChanged();
        }
    }

    /// <summary>-1 表示每天；0=周日 … 6=周六（与 .NET DayOfWeek 一致）。</summary>
    public int Weekday
    {
        get => _weekday;
        set
        {
            if (value == _weekday) return;
            _weekday = value;
            OnPropertyChanged();
        }
    }
}

/// <summary>
/// 轮换设置：起始日期、周期、跳过星期。
/// </summary>
public sealed class RotationSettings : ObservableRecipient
{
    private DateTime _startDate = DateTime.Today;
    private int _periodDays = 7;
    private RotationMode _mode = RotationMode.Day;
    private List<int> _skipWeekdays = new();

    [JsonPropertyName("startDate")]
    public DateTime StartDate
    {
        get => _startDate;
        set
        {
            if (value == _startDate) return;
            _startDate = value;
            OnPropertyChanged();
        }
    }

    [JsonPropertyName("periodDays")]
    public int PeriodDays
    {
        get => _periodDays;
        set
        {
            if (value == _periodDays) return;
            _periodDays = value;
            OnPropertyChanged();
        }
    }

    [JsonPropertyName("mode")]
    public RotationMode Mode
    {
        get => _mode;
        set
        {
            if (value == _mode) return;
            _mode = value;
            OnPropertyChanged();
        }
    }

    /// <summary>跳过的星期（0=周日 … 6=周六）。跳过日不消耗“第 N 天”计数。</summary>
    [JsonPropertyName("skipWeekdays")]
    public List<int> SkipWeekdays
    {
        get => _skipWeekdays;
        set
        {
            if (Equals(value, _skipWeekdays)) return;
            _skipWeekdays = value ?? new List<int>();
            OnPropertyChanged();
        }
    }
}

/// <summary>
/// 一张值日表（通常对应旧插件的一个“批次/组”）。
/// </summary>
public sealed class DutyPlan : ObservableRecipient
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "值日表";
    private bool _enabled = true;
    private string? _color;
    private DutyScope _scope = DutyScope.FullDay;
    private bool[] _weekDays = { true, true, true, true, true, false, false };
    private ObservableCollection<TimeSlot> _timeSlots = new();
    private ObservableCollection<DutyItem> _items = new();

    public string Id
    {
        get => _id;
        set
        {
            if (value == _id) return;
            _id = value;
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

    public string? Color
    {
        get => _color;
        set
        {
            if (value == _color) return;
            _color = value;
            OnPropertyChanged();
        }
    }

    public DutyScope Scope
    {
        get => _scope;
        set
        {
            if (value == _scope) return;
            _scope = value;
            OnPropertyChanged();
        }
    }

    /// <summary>按星期启用（索引与 DayOfWeek 一致：0=周日 … 6=周六）。</summary>
    public bool[] WeekDays
    {
        get => _weekDays;
        set
        {
            if (Equals(value, _weekDays)) return;
            _weekDays = value ?? new bool[7];
            OnPropertyChanged();
        }
    }

    public ObservableCollection<TimeSlot> TimeSlots
    {
        get => _timeSlots;
        set
        {
            if (Equals(value, _timeSlots)) return;
            _timeSlots = value ?? new ObservableCollection<TimeSlot>();
            OnPropertyChanged();
        }
    }

    public ObservableCollection<DutyItem> Items
    {
        get => _items;
        set
        {
            if (Equals(value, _items)) return;
            _items = value ?? new ObservableCollection<DutyItem>();
            OnPropertyChanged();
        }
    }
}
