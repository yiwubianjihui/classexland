namespace ClassExland.Features.Duty.Migration;

/// <summary>
/// 人名分隔文本的纯解析逻辑：兼容顿号、中英文逗号、分号、斜杠、空格、换行、全角空格。
/// </summary>
public static class DutyPersonParser
{
    private static readonly char[] Separators =
    {
        '、', '，', ',', '；', ';', '/', ' ', '\n', '\r', '\t', '　'
    };

    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string Join(IEnumerable<string> names) => string.Join("、", names);
}
