using System.Text;

namespace SonnetDB.Documents;

/// <summary>
/// SonnetDB 支持的轻量 JSON path 表达式。
/// </summary>
public sealed class JsonPath
{
    private JsonPath(string text, IReadOnlyList<JsonPathSegment> segments)
    {
        Text = text;
        Segments = segments;
    }

    /// <summary>规范化后的 JSON path 文本。</summary>
    public string Text { get; }

    /// <summary>按顺序排列的 path 片段。</summary>
    public IReadOnlyList<JsonPathSegment> Segments { get; }

    /// <summary>
    /// 解析 JSON path。当前支持 <c>$</c>、<c>$.name</c>、<c>$['name']</c> 和 <c>$[0]</c> 组合。
    /// </summary>
    /// <param name="text">JSON path 文本。</param>
    /// <returns>解析后的 JSON path。</returns>
    public static JsonPath Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text[0] != '$')
            throw new ArgumentException("JSON path 必须以 '$' 开头。", nameof(text));

        var segments = new List<JsonPathSegment>();
        int index = 1;
        while (index < text.Length)
        {
            char ch = text[index];
            if (ch == '.')
            {
                index++;
                int start = index;
                if (start >= text.Length || !IsNameStart(text[start]))
                    throw new ArgumentException($"JSON path 在位置 {start} 期望属性名。", nameof(text));

                index++;
                while (index < text.Length && IsNameContinue(text[index]))
                    index++;

                segments.Add(JsonPathSegment.Property(text[start..index]));
                continue;
            }

            if (ch == '[')
            {
                index++;
                if (index >= text.Length)
                    throw new ArgumentException("JSON path bracket 未闭合。", nameof(text));

                if (text[index] == '\'')
                {
                    index++;
                    var name = new StringBuilder();
                    while (index < text.Length)
                    {
                        char current = text[index];
                        if (current == '\'')
                        {
                            if (index + 1 < text.Length && text[index + 1] == '\'')
                            {
                                name.Append('\'');
                                index += 2;
                                continue;
                            }

                            index++;
                            break;
                        }

                        name.Append(current);
                        index++;
                    }

                    if (index >= text.Length || text[index] != ']')
                        throw new ArgumentException("JSON path 字符串属性 bracket 未闭合。", nameof(text));
                    if (name.Length == 0)
                        throw new ArgumentException("JSON path 属性名不能为空。", nameof(text));

                    index++;
                    segments.Add(JsonPathSegment.Property(name.ToString()));
                    continue;
                }

                int numberStart = index;
                while (index < text.Length && char.IsAsciiDigit(text[index]))
                    index++;
                if (numberStart == index || index >= text.Length || text[index] != ']')
                    throw new ArgumentException("JSON path 数组下标必须是非负整数。", nameof(text));

                if (!int.TryParse(text.AsSpan(numberStart, index - numberStart), out int arrayIndex))
                    throw new ArgumentException("JSON path 数组下标过大。", nameof(text));

                index++;
                segments.Add(JsonPathSegment.ForArrayIndex(arrayIndex));
                continue;
            }

            throw new ArgumentException($"JSON path 在位置 {index} 包含不支持的字符 '{ch}'。", nameof(text));
        }

        return new JsonPath(Normalize(segments), segments.AsReadOnly());
    }

    private static string Normalize(IReadOnlyList<JsonPathSegment> segments)
    {
        var sb = new StringBuilder("$");
        foreach (var segment in segments)
        {
            if (segment.Kind == JsonPathSegmentKind.Property)
            {
                if (CanUseDotProperty(segment.PropertyName!))
                {
                    sb.Append('.');
                    sb.Append(segment.PropertyName);
                }
                else
                {
                    sb.Append("['");
                    sb.Append(segment.PropertyName!.Replace("'", "''", StringComparison.Ordinal));
                    sb.Append("']");
                }
            }
            else
            {
                sb.Append('[');
                sb.Append(segment.ArrayIndex);
                sb.Append(']');
            }
        }

        return sb.ToString();
    }

    private static bool CanUseDotProperty(string value)
    {
        if (value.Length == 0 || !IsNameStart(value[0]))
            return false;
        for (int i = 1; i < value.Length; i++)
        {
            if (!IsNameContinue(value[i]))
                return false;
        }

        return true;
    }

    private static bool IsNameStart(char ch)
        => ch is >= 'A' and <= 'Z'
            || ch is >= 'a' and <= 'z'
            || ch == '_'
            || (ch > '\u007f' && char.IsLetter(ch));

    private static bool IsNameContinue(char ch)
        => IsNameStart(ch)
            || ch is >= '0' and <= '9';
}

/// <summary>JSON path 片段类别。</summary>
public enum JsonPathSegmentKind
{
    /// <summary>对象属性。</summary>
    Property,
    /// <summary>数组下标。</summary>
    ArrayIndex,
}

/// <summary>
/// JSON path 中的一个属性或数组下标片段。
/// </summary>
/// <param name="Kind">片段类别。</param>
/// <param name="PropertyName">属性名。</param>
/// <param name="ArrayIndex">数组下标。</param>
public sealed record JsonPathSegment(
    JsonPathSegmentKind Kind,
    string? PropertyName,
    int ArrayIndex)
{
    /// <summary>
    /// 创建属性片段。
    /// </summary>
    /// <param name="name">属性名。</param>
    public static JsonPathSegment Property(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new JsonPathSegment(JsonPathSegmentKind.Property, name, -1);
    }

    /// <summary>
    /// 创建数组下标片段。
    /// </summary>
    /// <param name="index">非负数组下标。</param>
    public static JsonPathSegment ForArrayIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return new JsonPathSegment(JsonPathSegmentKind.ArrayIndex, null, index);
    }
}
