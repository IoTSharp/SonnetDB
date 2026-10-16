using System.Buffers;
using System.Text;
using System.Text.Json;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Documents;

/// <summary>为动态 JSON path 和类型安全对象构造提供有界、无反射的 SQL 标量实现。</summary>
internal static class JsonDynamicSqlFunctions
{
    private const int MaxJsonTextLength = 8 * 1024 * 1024;
    private const int MaxPathLength = 1024;
    private const int MaxPathSegments = 64;
    private const int MaxObjectArguments = 4096;
    private const int MaxObjectEncodingBytes = 8 * 1024 * 1024;

    /// <summary>使用本行求值后的 path 读取 JSON，沿用静态路径的 SQL 标量类型合同。</summary>
    internal static object? Value(IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 2)
            throw new InvalidOperationException("json_value 需要两个参数：JSON 文本和 JSON path。");
        SqlExecutor.ThrowIfCancellationRequested();
        string? json = RequireString(arguments[0], "json_value", "json");
        string? pathText = RequireString(arguments[1], "json_value", "path");
        if (json is null || pathText is null)
            return null;
        if (json.Length > MaxJsonTextLength)
            throw new InvalidOperationException($"json_value 的 JSON 文本超过 {MaxJsonTextLength} 个字符上限。");
        if (pathText.Length > MaxPathLength)
            throw new InvalidOperationException($"json_value 的 JSON path 超过 {MaxPathLength} 个字符上限。");

        using var reservation = ReserveMemory(checked((long)json.Length * 8 + pathText.Length * 32L + 1024), "json_value");
        JsonPath path;
        try
        {
            path = JsonPath.Parse(pathText);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("json_value 的 JSON path 无效。", exception);
        }
        if (path.Segments.Count > MaxPathSegments)
            throw new InvalidOperationException($"json_value 的 JSON path 片段数超过 {MaxPathSegments} 个上限。");

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxPathSegments });
            SqlExecutor.ThrowIfCancellationRequested();
            return JsonPathEvaluator.TryResolve(document.RootElement, path, out JsonElement value)
                ? JsonPathEvaluator.ConvertElement(value) : null;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"json_value 的 JSON 无效或嵌套深度超过 {MaxPathSegments}。", exception);
        }
    }

    /// <summary>从交替的字段名和 SQL 标量构造对象；字符串保持字符串，decimal 不经过 double。</summary>
    internal static string CreateObject(IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > MaxObjectArguments || (arguments.Count & 1) != 0)
            throw new InvalidOperationException($"json_object 需要偶数个字段名/值参数，最多 {MaxObjectArguments} 个。");
        SqlExecutor.ThrowIfCancellationRequested();

        long encodingBytes = 2;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Count; index += 2)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (arguments[index] is not string name)
                throw new InvalidOperationException("json_object 的字段名必须是非 NULL STRING。");
            if (name.Length > MaxPathLength)
                throw new InvalidOperationException($"json_object 的字段名超过 {MaxPathLength} 个字符上限。");
            if (!names.Add(name))
                throw new InvalidOperationException("json_object 不允许重复的字段名。");
            // 默认 JSON encoder 的最坏情况是每个 UTF-16 code unit 写成六字节转义。
            // 在分配前限制此上界，避免 Utf8JsonWriter 对单个大 token 的容量预留突破预算。
            encodingBytes = checked(encodingBytes + name.Length * 6L + 4 + ValueEncodingBudget(arguments[index + 1]));
            if (encodingBytes > MaxObjectEncodingBytes)
                throw new InvalidOperationException($"json_object 的最坏编码预算超过 {MaxObjectEncodingBytes} 字节上限。");
        }

        // 额外保留 writer 的块请求余量，避免最后几个标点触发 ArrayBufferWriter 扩容。
        int capacity = checked((int)encodingBytes + 4096);
        using var reservation = ReserveMemory(checked(capacity * 3L + names.Count * 64L), "json_object");
        var output = new ArrayBufferWriter<byte>(capacity);
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            for (int index = 0; index < arguments.Count; index += 2)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                writer.WritePropertyName((string)arguments[index]!);
                WriteScalar(writer, arguments[index + 1]);
            }
            writer.WriteEndObject();
        }
        SqlExecutor.ThrowIfCancellationRequested();
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private static long ValueEncodingBudget(object? value)
        => value switch
        {
            null => 4,
            string text => checked(text.Length * 6L + 2),
            bool => 5,
            byte or sbyte or short or ushort or int or uint or long or ulong or decimal => 128,
            float number when float.IsFinite(number) => 128,
            double number when double.IsFinite(number) => 128,
            _ => throw new InvalidOperationException("json_object 的值必须是 STRING、BOOL、有限数值或 NULL。"),
        };

    private static void WriteScalar(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case byte number: writer.WriteNumberValue(number); break;
            case sbyte number: writer.WriteNumberValue(number); break;
            case short number: writer.WriteNumberValue(number); break;
            case ushort number: writer.WriteNumberValue(number); break;
            case int number: writer.WriteNumberValue(number); break;
            case uint number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case ulong number: writer.WriteNumberValue(number); break;
            case float number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            default: throw new InvalidOperationException("json_object 的值类型不受支持。");
        }
    }

    private static string? RequireString(object? value, string function, string argument)
        => value switch
        {
            null => null,
            string text => text,
            _ => throw new InvalidOperationException($"{function} 的 {argument} 参数必须是 STRING 或 NULL。"),
        };

    private static SqlQueryResources.SqlOperatorMemoryReservation? ReserveMemory(long bytes, string function)
    {
        var reservation = SqlQueryResources.Current?.CreateReservation();
        if (reservation is not null && !reservation.TryReserve(bytes))
        {
            reservation.Dispose();
            throw new InvalidOperationException($"{function} 超出 SQL 查询内存预算。");
        }
        return reservation;
    }
}
