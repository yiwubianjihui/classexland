using System.Globalization;
using System.Text.RegularExpressions;
using ClassExland.Features.Duty.Models;

namespace ClassExland.Features.Duty.Migration;

/// <summary>
/// DutyRoster 纯文本值日表（值日表.txt）解析器，纯逻辑、可单测。
/// 支持：# 注释、[设置]/[第N批] 段、周一~周五 形式的星期行、HH:mm 时间行、中英文冒号与分号。
/// </summary>
public static class DutyRosterTextParser
{
    private static readonly Regex TimeLineRegex = new(
        @"^\s*(?<h>\d{1,2})[:：](?<m>\d{2})\s+(?<rest>.+)$", RegexOptions.Compiled);

    private static readonly Regex WeekdayRangeRegex = new(
        @"周([一二三四五六日天])", RegexOptions.Compiled);

    public static DutyRosterParseResult Parse(string text)
    {
        var rotation = new RotationSettings();
        var plans = new List<DutyPlan>();
        var warnings = new List<string>();
        DutyPlan? currentPlan = null;
        var currentWeekdays = new List<int>();

        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var section = line[1..^1].Trim();
                if (section == "设置")
                {
                    currentPlan = null;
                    currentWeekdays.Clear();
                    continue;
                }

                if (section.StartsWith("第") && section.Contains("批"))
                {
                    currentPlan = new DutyPlan
                    {
                        Name = section,
                        Scope = DutyScope.Custom,
                        Enabled = true
                    };
                    plans.Add(currentPlan);
                    currentWeekdays.Clear();
                    continue;
                }

                warnings.Add($"未识别的段落：[{section}]，已忽略。");
                continue;
            }

            var eq = line.IndexOfAny(new[] { '=', '＝' });
            if (eq > 0)
            {
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (key is "轮换起始" or "轮换开始")
                {
                    if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate))
                        rotation.StartDate = startDate;
                    else
                        warnings.Add($"轮换起始日期无法解析：{value}");
                }
                else if (key is "轮换周期" or "周期")
                {
                    if (int.TryParse(value, out var period) && period > 0)
                        rotation.PeriodDays = period;
                }
                else if (key == "跳过")
                {
                    var days = ParseWeekdays(value);
                    foreach (var d in days)
                    {
                        if (!rotation.SkipWeekdays.Contains(d)) rotation.SkipWeekdays.Add(d);
                    }
                }
                continue;
            }

            var weekdays = ParseWeekdays(line);
            if (weekdays.Count > 0 && TimeLineRegex.IsMatch(line) == false)
            {
                currentWeekdays = weekdays;
                if (currentPlan != null)
                {
                    foreach (var d in weekdays) currentPlan.WeekDays[d] = true;
                }
                continue;
            }

            var match = TimeLineRegex.Match(line);
            if (!match.Success) continue;

            if (currentPlan == null)
            {
                currentPlan = new DutyPlan { Name = "值日表", Scope = DutyScope.Custom };
                plans.Add(currentPlan);
            }

            if (!int.TryParse(match.Groups["h"].Value, out var hour) ||
                !int.TryParse(match.Groups["m"].Value, out var minute) ||
                hour > 23 || minute > 59)
            {
                warnings.Add($"时间无法解析：{match.Value}");
                continue;
            }

            var start = new TimeSpan(hour, minute, 0);
            var slotLabel = $"{hour:00}:{minute:00}";
            if (currentPlan.TimeSlots.All(s => s.Label != slotLabel))
            {
                currentPlan.TimeSlots.Add(new TimeSlot { Label = slotLabel, Start = start, End = start.Add(TimeSpan.FromMinutes(30)) });
            }

            var rest = match.Groups["rest"].Value;
            foreach (var piece in rest.Split(new[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = piece.IndexOfAny(new[] { '：', ':' });
                if (colon <= 0) continue;
                var project = piece[..colon].Trim();
                var persons = piece[(colon + 1)..].Trim();
                if (project.Length == 0) continue;

                var isSweep = project.Contains("扫地") || project.Contains("拖地") || project.Contains("大扫除");
                foreach (var day in currentWeekdays.Count > 0 ? currentWeekdays : Enumerable.Range(0, 7).ToList())
                {
                    currentPlan.Items.Add(new DutyItem
                    {
                        Project = project,
                        Persons = persons,
                        IsSweep = isSweep,
                        Weekday = day,
                        SlotLabel = slotLabel
                    });
                }
            }
        }

        // 未显式写星期行的时间线，默认周一至周五有效。
        foreach (var plan in plans)
        {
            if (plan.WeekDays.Skip(1).Take(5).All(x => !x))
            {
                for (var d = 1; d <= 5; d++) plan.WeekDays[d] = true;
            }
        }

        return new DutyRosterParseResult(rotation, plans, warnings);
    }

    private static List<int> ParseWeekdays(string value)
    {
        var result = new List<int>();
        var raw = (value ?? "").Trim().Replace('：', ':');
        var rangeMatch = Regex.Match(raw, @"周([一二三四五六日天])\s*[~～至-]\s*周([一二三四五六日天])");
        if (rangeMatch.Success)
        {
            var from = ToDayOfWeek(rangeMatch.Groups[1].Value);
            var to = ToDayOfWeek(rangeMatch.Groups[2].Value);
            for (var d = from; ; d = (d + 1) % 7)
            {
                if (!result.Contains(d)) result.Add(d);
                if (d == to) break;
            }
            return result;
        }

        foreach (Match m in WeekdayRangeRegex.Matches(raw))
        {
            var d = ToDayOfWeek(m.Groups[1].Value);
            if (!result.Contains(d)) result.Add(d);
        }
        return result;
    }

    private static int ToDayOfWeek(string token) => token switch
    {
        "一" => 1,
        "二" => 2,
        "三" => 3,
        "四" => 4,
        "五" => 5,
        "六" => 6,
        _ => 0 // 日 / 天
    };
}

/// <summary>DutyRoster 解析结果。</summary>
public sealed record DutyRosterParseResult(
    RotationSettings Rotation,
    List<DutyPlan> Plans,
    List<string> Warnings);
