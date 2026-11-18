using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassExland.Features.Layout;

/// <summary>
/// 生成「推荐自动化片段」的纯函数：无 IO、无副作用、相同输入恒定输出。
/// 当宿主 SDK 没有提供可直接设置布局的注册扩展时，设置页可展示本片段，
/// 由用户自行粘贴进现有自动化动作集——绝不自动覆盖 Automations/Default.json。
/// </summary>
public static class RecommendedAutomation
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>生成可直接粘贴的自动化动作集 JSON 数组。</summary>
    public static string Generate(LayoutConfig config)
    {
        var c = LayoutConfig.Sanitize(config);
        var workflows = new object[]
        {
            BuildWorkflow(
                name: "上课（ClassExland 推荐）",
                guid: "0ee32c17-d3f3-491b-9347-83eb911c936c",
                triggerId: "classisland.lessons.onClass",
                timeSeconds: null,
                targetState: null,
                c.ClassOpacity, c.ClassComponentConfig, c.ClassScale),
            BuildWorkflow(
                name: "下课（ClassExland 推荐）",
                guid: "73106049-6758-45d8-8fcf-2ccb9f56eec4",
                triggerId: "classisland.lessons.onBreakingTime",
                timeSeconds: -c.ClassEndDelaySeconds,
                targetState: 3,
                c.BreakOpacity, c.BreakComponentConfig, c.BreakScale)
        };

        return JsonSerializer.Serialize(workflows, JsonOptions);
    }

    /// <summary>生成给设置页展示的说明文字。</summary>
    public static string Description(LayoutConfig config)
    {
        var c = LayoutConfig.Sanitize(config);
        return $"上课：{c.ClassComponentConfig} / Scale {c.ClassScale:0.##} / Opacity {c.ClassOpacity:0.##}；" +
               $"下课：{c.BreakComponentConfig} / Scale {c.BreakScale:0.##} / Opacity {c.BreakOpacity:0.##}；" +
               $"下课延迟 {c.ClassEndDelaySeconds}s（拖堂顺延 {c.PostponeStepSeconds}s × {c.MaxPostpones}）。";
    }

    private static object BuildWorkflow(
        string name, string guid, string triggerId, int? timeSeconds, int? targetState,
        double opacity, string componentConfig, double scale)
    {
        var triggerSettings = new Dictionary<string, object?>();
        if (targetState.HasValue) triggerSettings["TargetState"] = targetState.Value;
        if (timeSeconds.HasValue) triggerSettings["TimeSeconds"] = timeSeconds.Value;
        object? settings = triggerSettings.Count == 0 ? null : triggerSettings;

        var actions = new object[]
        {
            SettingAction("Opacity", opacity),
            SettingAction("CurrentComponentConfig", componentConfig),
            SettingAction("Scale", scale)
        };

        return new
        {
            Triggers = new object[] { new { Id = triggerId, Settings = settings, IsActive = false } },
            IsConditionEnabled = false,
            Ruleset = new
            {
                Mode = 0,
                IsReversed = false,
                Groups = new object[]
                {
                    new
                    {
                        Rules = new object[] { new { IsReversed = false, Id = "", Settings = (object?)null, IsActive = false } },
                        Mode = 1,
                        IsReversed = false,
                        IsEnabled = true,
                        IsActive = false
                    }
                },
                IsActive = false
            },
            ActionSet = new
            {
                IsWorking = false,
                Guid = guid,
                Name = name,
                Actions = actions,
                IsEnabled = true,
                IsRevertEnabled = true,
                Status = 0,
                IsActive = false
            },
            IsActive = false
        };
    }

    private static object SettingAction(string name, object value) => new
    {
        IsCompleted = true,
        Id = "classisland.settings",
        Settings = new { Name = name, Value = value, IsActive = false },
        IsActive = false
    };
}
