using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Documents;

/// <summary>
/// JSON path 求值器，供 document collection 与关系表 JSON 列共用。
/// </summary>
public static class JsonPathEvaluator
{
    /// <summary>
    /// 从 JSON 文本中读取指定 path 的值。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <param name="path">JSON path 文本。</param>
    /// <returns>找到时返回稳定 SQL 标量或 JSON 字符串；缺失时返回 null。</returns>
    public static object? Evaluate(string? json, string path)
    {
        if (json is null)
            return null;

        var parsedPath = JsonPath.Parse(path);
        return Evaluate(json, parsedPath);
    }

    /// <summary>
    /// 从 JSON 文本中读取指定 path 的值。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <param name="path">已解析 JSON path。</param>
    /// <returns>找到时返回稳定 SQL 标量或 JSON 字符串；缺失时返回 null。</returns>
    public static object? Evaluate(string? json, JsonPath path)
    {
        if (json is null)
            return null;

        ArgumentNullException.ThrowIfNull(path);
        using var document = JsonDocument.Parse(json);
        if (!TryResolve(document.RootElement, path, out var element))
            return null;

        return ConvertElement(element);
    }

    /// <summary>
    /// 尝试从 JSON 文本中读取指定 path 的值，并区分 JSON null 与 path 缺失。
    /// </summary>
    /// <param name="json">JSON 文本文档。</param>
    /// <param name="path">JSON path 文本。</param>
    /// <param name="value">path 存在时返回稳定 SQL 标量或 JSON 文本。</param>
    /// <returns>path 存在返回 true；path 缺失或 JSON 文本为 null 返回 false。</returns>
    public static bool TryEvaluate(string? json, string path, out object? value)
    {
        value = null;
        if (json is null)
            return false;

        var parsedPath = JsonPath.Parse(path);
        return TryEvaluate(json, parsedPath, out value);
    }

    /// <summary>
    /// 尝试从 JSON 文本中读取指定 path 的值，并区分 JSON null 与 path 缺失。
    /// </summary>
    /// <param name="json">JSON 文本文档。</param>
    /// <param name="path">已解析 JSON path。</param>
    /// <param name="value">path 存在时返回稳定 SQL 标量或 JSON 文本。</param>
    /// <returns>path 存在返回 true；path 缺失或 JSON 文本为 null 返回 false。</returns>
    public static bool TryEvaluate(string? json, JsonPath path, out object? value)
    {
        value = null;
        if (json is null)
            return false;

        ArgumentNullException.ThrowIfNull(path);
        using var document = JsonDocument.Parse(json);
        if (!TryResolve(document.RootElement, path, out var element))
            return false;

        value = ConvertElement(element);
        return true;
    }

    /// <summary>
    /// 将 JSON 文本规范化为紧凑 UTF-8 JSON 字符串。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>规范化后的 JSON 文本。</returns>
    public static string NormalizeJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            document.RootElement.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 将任意值转换为文档 path 索引键使用的稳定文本。
    /// </summary>
    /// <param name="value">path 求值结果。</param>
    /// <returns>可比较的稳定文本；null 保持为 null。</returns>
    public static string? ToIndexScalar(object? value)
        => value switch
        {
            null => null,
            bool b => b ? "true" : "false",
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                Convert.ToString(value, CultureInfo.InvariantCulture),
            DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

    /// <summary>
    /// 尝试从 JSON DOM 中按已解析 path 解析元素。
    /// </summary>
    /// <param name="root">JSON 根元素。</param>
    /// <param name="path">已解析 JSON path。</param>
    /// <param name="value">解析成功时的元素。</param>
    /// <returns>解析成功返回 true，否则返回 false。</returns>
    public static bool TryResolve(JsonElement root, JsonPath path, out JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(path);
        value = root;
        foreach (var segment in path.Segments)
        {
            if (segment.Kind == JsonPathSegmentKind.Property)
            {
                if (value.ValueKind != JsonValueKind.Object
                    || !value.TryGetProperty(segment.PropertyName!, out value))
                {
                    value = default;
                    return false;
                }
            }
            else
            {
                if (value.ValueKind != JsonValueKind.Array || segment.ArrayIndex >= value.GetArrayLength())
                {
                    value = default;
                    return false;
                }

                value = value[segment.ArrayIndex];
            }
        }

        return true;
    }

    internal static object? ConvertElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out long longValue)
                ? longValue
                : element.GetDouble(),
            JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
            JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
    }
}
