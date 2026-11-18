using System.Text.Json;
using ClassExland.Contracts;

namespace ClassExland.Features.Layout;

/// <summary>
/// LayoutModule 的可配置参数。默认值完全沿用用户现有 Automations/Default.json 的行为：
/// 上课紧凑布局 Default/0.8/0.5，下课完整布局 offclass/1/0.7。
/// </summary>
public sealed class LayoutConfig
{
    /// <summary>是否启用界面自动切换。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>上课（预备铃起）使用的组件配置名。</summary>
    public string ClassComponentConfig { get; set; } = "Default";

    public double ClassScale { get; set; } = 0.8;
    public double ClassOpacity { get; set; } = 0.5;

    /// <summary>下课使用的组件配置名。</summary>
    public string BreakComponentConfig { get; set; } = "offclass";

    public double BreakScale { get; set; } = 1.0;
    public double BreakOpacity { get; set; } = 0.7;

    /// <summary>下课延迟 D（秒），范围 0-300，默认 60。</summary>
    public int ClassEndDelaySeconds { get; set; } = 60;

    /// <summary>拖堂时每次顺延的秒数，默认 30。</summary>
    public int PostponeStepSeconds { get; set; } = 30;

    /// <summary>拖堂最多顺延次数，默认 3。超过后放弃本次下课并记录日志。</summary>
    public int MaxPostpones { get; set; } = 3;

    public static LayoutConfig Sanitize(LayoutConfig? raw)
    {
        var c = raw ?? new LayoutConfig();
        c.ClassComponentConfig = string.IsNullOrWhiteSpace(c.ClassComponentConfig) ? "Default" : c.ClassComponentConfig.Trim();
        c.BreakComponentConfig = string.IsNullOrWhiteSpace(c.BreakComponentConfig) ? "offclass" : c.BreakComponentConfig.Trim();
        c.ClassScale = Clamp(c.ClassScale, 0.1, 3.0, 0.8);
        c.BreakScale = Clamp(c.BreakScale, 0.1, 3.0, 1.0);
        c.ClassOpacity = Clamp(c.ClassOpacity, 0.05, 1.0, 0.5);
        c.BreakOpacity = Clamp(c.BreakOpacity, 0.05, 1.0, 0.7);
        c.ClassEndDelaySeconds = Clamp(c.ClassEndDelaySeconds, 0, 300, 60);
        c.PostponeStepSeconds = Clamp(c.PostponeStepSeconds, 1, 120, 30);
        c.MaxPostpones = Clamp(c.MaxPostpones, 0, 10, 3);
        return c;
    }

    private static int Clamp(int value, int min, int max, int fallback)
        => value < min || value > max ? fallback : value;

    private static double Clamp(double value, double min, double max, double fallback)
        => double.IsNaN(value) || value < min || value > max ? fallback : value;
}

/// <summary>layout.json 的读写。使用独立文件，不触碰 Automations/Default.json。</summary>
public sealed class LayoutConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public LayoutConfigStore(IPluginPaths paths)
    {
        _path = paths.Get(ConfigFiles.Layout);
    }

    public LayoutConfig Load()
    {
        try
        {
            if (!File.Exists(_path)) return LayoutConfig.Sanitize(new LayoutConfig());
            var json = File.ReadAllText(_path);
            var cfg = JsonSerializer.Deserialize<LayoutConfig>(json, JsonOptions);
            return LayoutConfig.Sanitize(cfg);
        }
        catch
        {
            // 配置损坏时退回默认值，绝不因配置问题导致插件被禁用。
            return LayoutConfig.Sanitize(new LayoutConfig());
        }
    }

    public bool TrySave(LayoutConfig config)
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(LayoutConfig.Sanitize(config), JsonOptions));
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
