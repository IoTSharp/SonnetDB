using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SonnetDB.Data.Documents;

/// <summary>
/// AOT 友好的 Document Store 类型值，用于 filter 与 update builder。
/// </summary>
public readonly struct SndbDocumentValue
{
    private readonly JsonElement _element;

    private SndbDocumentValue(JsonElement element)
    {
        _element = element.Clone();
    }

    /// <summary>JSON null 值。</summary>
    public static SndbDocumentValue Null { get; } = FromWriter(static writer => writer.WriteNullValue());

    /// <summary>
    /// 从已经解析的 JSON 值创建类型值。
    /// </summary>
    /// <param name="element">JSON 值。</param>
    /// <returns>可安全脱离原 <see cref="JsonDocument"/> 生命周期使用的类型值。</returns>
    public static SndbDocumentValue FromJsonElement(JsonElement element) => new(element);

    /// <summary>
    /// 从 JSON 文本创建类型值。
    /// </summary>
    /// <param name="json">单个 JSON 值的文本。</param>
    /// <returns>解析后的类型值。</returns>
    public static SndbDocumentValue FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        return new SndbDocumentValue(document.RootElement);
    }

    /// <summary>
    /// 使用调用方提供的源生成 JSON 元数据创建类型值。
    /// </summary>
    /// <typeparam name="T">值类型。</typeparam>
    /// <param name="value">要序列化的值。</param>
    /// <param name="typeInfo">源生成的 JSON 类型元数据。</param>
    /// <returns>序列化后的类型值。</returns>
    public static SndbDocumentValue From<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new SndbDocumentValue(JsonSerializer.SerializeToElement(value, typeInfo));
    }

    /// <summary>
    /// 创建 JSON 数组类型值。
    /// </summary>
    /// <param name="values">数组元素。</param>
    /// <returns>JSON 数组值。</returns>
    public static SndbDocumentValue Array(params SndbDocumentValue[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return FromWriter(writer =>
        {
            writer.WriteStartArray();
            foreach (var value in values)
                value.ToJsonElement().WriteTo(writer);
            writer.WriteEndArray();
        });
    }

    /// <summary>
    /// 返回可写入现有 Document SDK DTO 的 JSON 值副本。
    /// </summary>
    /// <returns>JSON 值副本。</returns>
    public JsonElement ToJsonElement()
    {
        if (_element.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("SndbDocumentValue 尚未初始化。");
        return _element.Clone();
    }

    /// <summary>将字符串转换为 Document 类型值。</summary>
    /// <param name="value">字符串值。</param>
    /// <returns>JSON 字符串类型值。</returns>
    public static implicit operator SndbDocumentValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return FromWriter(writer => writer.WriteStringValue(value));
    }

    /// <summary>将布尔值转换为 Document 类型值。</summary>
    /// <param name="value">布尔值。</param>
    /// <returns>JSON 布尔类型值。</returns>
    public static implicit operator SndbDocumentValue(bool value)
        => FromWriter(writer => writer.WriteBooleanValue(value));

    /// <summary>将 32 位整数转换为 Document 类型值。</summary>
    /// <param name="value">整数值。</param>
    /// <returns>JSON 数字类型值。</returns>
    public static implicit operator SndbDocumentValue(int value)
        => FromWriter(writer => writer.WriteNumberValue(value));

    /// <summary>将 64 位整数转换为 Document 类型值。</summary>
    /// <param name="value">整数值。</param>
    /// <returns>JSON 数字类型值。</returns>
    public static implicit operator SndbDocumentValue(long value)
        => FromWriter(writer => writer.WriteNumberValue(value));

    /// <summary>将单精度数转换为 Document 类型值。</summary>
    /// <param name="value">单精度数值。</param>
    /// <returns>JSON 数字类型值。</returns>
    public static implicit operator SndbDocumentValue(float value)
        => FromWriter(writer => writer.WriteNumberValue(value));

    /// <summary>将双精度数转换为 Document 类型值。</summary>
    /// <param name="value">双精度数值。</param>
    /// <returns>JSON 数字类型值。</returns>
    public static implicit operator SndbDocumentValue(double value)
        => FromWriter(writer => writer.WriteNumberValue(value));

    /// <summary>将十进制数转换为 Document 类型值。</summary>
    /// <param name="value">十进制数值。</param>
    /// <returns>JSON 数字类型值。</returns>
    public static implicit operator SndbDocumentValue(decimal value)
        => FromWriter(writer => writer.WriteNumberValue(value));

    /// <summary>将 GUID 转换为 JSON 字符串类型值。</summary>
    /// <param name="value">GUID 值。</param>
    /// <returns>JSON 字符串类型值。</returns>
    public static implicit operator SndbDocumentValue(Guid value)
        => FromWriter(writer => writer.WriteStringValue(value));

    /// <summary>将带时区偏移的时间转换为 JSON 字符串类型值。</summary>
    /// <param name="value">带时区偏移的时间。</param>
    /// <returns>ISO-8601 JSON 字符串类型值。</returns>
    public static implicit operator SndbDocumentValue(DateTimeOffset value)
        => FromWriter(writer => writer.WriteStringValue(value));

    private static SndbDocumentValue FromWriter(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new SndbDocumentValue(document.RootElement);
    }
}

/// <summary>
/// Document Store 原生 JSON 类型。
/// </summary>
public enum SndbDocumentJsonType
{
    /// <summary>JSON null。</summary>
    Null,

    /// <summary>JSON 布尔值。</summary>
    Boolean,

    /// <summary>JSON 数值。</summary>
    Number,

    /// <summary>JSON 字符串。</summary>
    String,

    /// <summary>JSON 对象。</summary>
    Object,

    /// <summary>JSON 数组。</summary>
    Array,
}

/// <summary>
/// 创建不含字符串操作符的 Document Store filter。
/// </summary>
public static class SndbDocumentFilters
{
    /// <summary>创建相等条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter Equal(string path, SndbDocumentValue value) => Field(path, "eq", value);

    /// <summary>创建不相等条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter NotEqual(string path, SndbDocumentValue value) => Field(path, "ne", value);

    /// <summary>创建大于条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter GreaterThan(string path, SndbDocumentValue value) => Field(path, "gt", value);

    /// <summary>创建大于等于条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter GreaterThanOrEqual(string path, SndbDocumentValue value) => Field(path, "gte", value);

    /// <summary>创建小于条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter LessThan(string path, SndbDocumentValue value) => Field(path, "lt", value);

    /// <summary>创建小于等于条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter LessThanOrEqual(string path, SndbDocumentValue value) => Field(path, "lte", value);

    /// <summary>创建属于给定值列表的条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="values">允许值列表。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter In(string path, params SndbDocumentValue[] values)
        => Field(path, "in", SndbDocumentValue.Array(values));

    /// <summary>创建不属于给定值列表的条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="values">排除值列表。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter NotIn(string path, params SndbDocumentValue[] values)
        => Field(path, "nin", SndbDocumentValue.Array(values));

    /// <summary>创建字段存在性条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="exists">要求字段存在时为 <c>true</c>。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter Exists(string path, bool exists = true)
        => Field(path, "exists", exists);

    /// <summary>创建数组、对象或字符串包含条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">要查找的值。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter Contains(string path, SndbDocumentValue value) => Field(path, "contains", value);

    /// <summary>创建数组元素匹配条件。</summary>
    /// <param name="path">数组字段 JSON path。</param>
    /// <param name="filter">针对单个数组元素执行的子过滤表达式。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter ElementMatch(string path, SndbDocumentFilter filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(filter);
        return new SndbDocumentFilter(path, "elemMatch", ElemMatch: filter);
    }

    /// <summary>创建有界正则匹配条件。</summary>
    /// <param name="path">字符串字段 JSON path。</param>
    /// <param name="pattern">正则模式。</param>
    /// <param name="options">可选 i/c/m/s/x 标志。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter Regex(string path, string pattern, string? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var value = (SndbDocumentValue)pattern;
        var filter = Field(path, "regex", value);
        return filter with { RegexOptions = options };
    }

    /// <summary>创建 JSON 类型匹配条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="types">允许的 JSON 类型。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter Type(string path, params SndbDocumentJsonType[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (types.Length == 0 || types.Any(static type => !Enum.IsDefined(type)))
            throw new ArgumentException("至少需要一个有效的 JSON 类型。", nameof(types));
        var values = types
            .Select(static type => (SndbDocumentValue)type.ToString().ToLowerInvariant())
            .ToArray();
        return Field(path, "type", SndbDocumentValue.Array(values));
    }

    /// <summary>创建数组长度匹配条件。</summary>
    /// <param name="path">数组字段 JSON path。</param>
    /// <param name="size">要求的非负数组长度。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter Size(string path, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return Field(path, "size", size);
    }

    /// <summary>创建数组包含全部给定值的条件。</summary>
    /// <param name="path">数组字段 JSON path。</param>
    /// <param name="values">必须全部存在的值。</param>
    /// <returns>过滤表达式。</returns>
    public static SndbDocumentFilter All(string path, params SndbDocumentValue[] values)
        => Field(path, "all", SndbDocumentValue.Array(values));

    /// <summary>创建 AND 组合条件。</summary>
    /// <param name="filters">子过滤表达式。</param>
    /// <returns>组合后的过滤表达式。</returns>
    public static SndbDocumentFilter And(params SndbDocumentFilter[] filters)
        => new(And: ValidateFilters(filters));

    /// <summary>创建 OR 组合条件。</summary>
    /// <param name="filters">子过滤表达式。</param>
    /// <returns>组合后的过滤表达式。</returns>
    public static SndbDocumentFilter Or(params SndbDocumentFilter[] filters)
        => new(Or: ValidateFilters(filters));

    /// <summary>创建 NOT 条件。</summary>
    /// <param name="filter">要取反的表达式。</param>
    /// <returns>取反后的过滤表达式。</returns>
    public static SndbDocumentFilter Not(SndbDocumentFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return new SndbDocumentFilter(Not: filter);
    }

    private static SndbDocumentFilter Field(string path, string op, SndbDocumentValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new SndbDocumentFilter(path, op, value.ToJsonElement());
    }

    private static IReadOnlyList<SndbDocumentFilter> ValidateFilters(SndbDocumentFilter[] filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (filters.Length == 0)
            throw new ArgumentException("过滤条件列表不能为空。", nameof(filters));

        var result = filters.ToArray();
        if (result.Any(static filter => filter is null))
            throw new ArgumentException("过滤条件列表不能包含 null。", nameof(filters));
        return result;
    }
}

/// <summary>
/// 以 AND 语义逐步构造 Document Store filter。
/// </summary>
public sealed class SndbDocumentFilterBuilder
{
    private readonly List<SndbDocumentFilter> _filters = [];

    /// <summary>添加自定义过滤表达式。</summary>
    /// <param name="filter">要添加的表达式。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder Add(SndbDocumentFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filters.Add(filter);
        return this;
    }

    /// <summary>添加相等条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder Equal(string path, SndbDocumentValue value) => Add(SndbDocumentFilters.Equal(path, value));

    /// <summary>添加不相等条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder NotEqual(string path, SndbDocumentValue value) => Add(SndbDocumentFilters.NotEqual(path, value));

    /// <summary>添加大于条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder GreaterThan(string path, SndbDocumentValue value) => Add(SndbDocumentFilters.GreaterThan(path, value));

    /// <summary>添加大于等于条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder GreaterThanOrEqual(string path, SndbDocumentValue value) => Add(SndbDocumentFilters.GreaterThanOrEqual(path, value));

    /// <summary>添加小于条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder LessThan(string path, SndbDocumentValue value) => Add(SndbDocumentFilters.LessThan(path, value));

    /// <summary>添加小于等于条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">比较值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder LessThanOrEqual(string path, SndbDocumentValue value) => Add(SndbDocumentFilters.LessThanOrEqual(path, value));

    /// <summary>添加 in 条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="values">允许值列表。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder In(string path, params SndbDocumentValue[] values) => Add(SndbDocumentFilters.In(path, values));

    /// <summary>添加 nin 条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="values">排除值列表。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder NotIn(string path, params SndbDocumentValue[] values) => Add(SndbDocumentFilters.NotIn(path, values));

    /// <summary>添加字段存在性条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="exists">要求字段存在时为 <c>true</c>。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder Exists(string path, bool exists = true) => Add(SndbDocumentFilters.Exists(path, exists));

    /// <summary>添加包含条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="value">要查找的值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder Contains(string path, SndbDocumentValue value) => Add(SndbDocumentFilters.Contains(path, value));

    /// <summary>添加数组元素匹配条件。</summary>
    /// <param name="path">数组字段 JSON path。</param>
    /// <param name="filter">元素级子过滤表达式。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder ElementMatch(string path, SndbDocumentFilter filter)
        => Add(SndbDocumentFilters.ElementMatch(path, filter));

    /// <summary>添加正则匹配条件。</summary>
    /// <param name="path">字符串字段 JSON path。</param>
    /// <param name="pattern">正则模式。</param>
    /// <param name="options">可选 i/c/m/s/x 标志。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder Regex(string path, string pattern, string? options = null)
        => Add(SndbDocumentFilters.Regex(path, pattern, options));

    /// <summary>添加 JSON 类型匹配条件。</summary>
    /// <param name="path">字段 JSON path。</param>
    /// <param name="types">允许的 JSON 类型。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder Type(string path, params SndbDocumentJsonType[] types)
        => Add(SndbDocumentFilters.Type(path, types));

    /// <summary>添加数组长度匹配条件。</summary>
    /// <param name="path">数组字段 JSON path。</param>
    /// <param name="size">要求的非负数组长度。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder Size(string path, int size) => Add(SndbDocumentFilters.Size(path, size));

    /// <summary>添加数组包含全部给定值的条件。</summary>
    /// <param name="path">数组字段 JSON path。</param>
    /// <param name="values">必须全部存在的值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentFilterBuilder All(string path, params SndbDocumentValue[] values)
        => Add(SndbDocumentFilters.All(path, values));

    /// <summary>
    /// 生成过滤表达式；多个条件按 AND 组合。
    /// </summary>
    /// <returns>可直接放入 <see cref="SndbDocumentFindOptions.Filter"/> 的表达式。</returns>
    public SndbDocumentFilter Build()
        => _filters.Count switch
        {
            0 => throw new InvalidOperationException("至少需要添加一个过滤条件。"),
            1 => _filters[0],
            _ => SndbDocumentFilters.And(_filters.ToArray()),
        };
}

/// <summary>
/// 构造 Document Store 投影字段列表。
/// </summary>
public sealed class SndbDocumentProjectionBuilder
{
    private readonly List<SndbDocumentProjection> _fields = [];

    /// <summary>包含一个字段。</summary>
    /// <param name="path">源字段 JSON path。</param>
    /// <param name="name">可选输出字段名。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentProjectionBuilder Include(string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _fields.Add(new SndbDocumentProjection(name, path));
        return this;
    }

    /// <summary>生成投影字段列表。</summary>
    /// <returns>投影字段快照。</returns>
    public IReadOnlyList<SndbDocumentProjection> Build()
        => _fields.Count == 0
            ? throw new InvalidOperationException("至少需要添加一个投影字段。")
            : _fields.ToArray();
}

/// <summary>
/// 构造 Document Store 排序字段列表。
/// </summary>
public sealed class SndbDocumentSortBuilder
{
    private readonly List<SndbDocumentSort> _fields = [];

    /// <summary>添加升序字段。</summary>
    /// <param name="path">排序字段 JSON path。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentSortBuilder Ascending(string path) => Add(path, descending: false);

    /// <summary>添加降序字段。</summary>
    /// <param name="path">排序字段 JSON path。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentSortBuilder Descending(string path) => Add(path, descending: true);

    /// <summary>生成排序字段列表。</summary>
    /// <returns>排序字段快照。</returns>
    public IReadOnlyList<SndbDocumentSort> Build()
        => _fields.Count == 0
            ? throw new InvalidOperationException("至少需要添加一个排序字段。")
            : _fields.ToArray();

    private SndbDocumentSortBuilder Add(string path, bool descending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _fields.Add(new SndbDocumentSort(path, descending));
        return this;
    }
}

/// <summary>
/// Document Store <c>$currentDate</c> 写入形式。
/// </summary>
public enum SndbDocumentCurrentDateKind
{
    /// <summary>写入 ISO-8601 UTC 字符串。</summary>
    Date,

    /// <summary>写入 Unix 毫秒时间戳。</summary>
    Timestamp,
}

/// <summary>
/// Document Store <c>$pop</c> 移除数组元素的方向。
/// </summary>
public enum SndbDocumentPopDirection
{
    /// <summary>移除数组首项。</summary>
    First = -1,

    /// <summary>移除数组末项。</summary>
    Last = 1,
}

/// <summary>
/// 构造 Document Store 局部更新操作符集合。
/// </summary>
public sealed class SndbDocumentUpdateBuilder
{
    private Dictionary<string, JsonElement>? _set;
    private Dictionary<string, JsonElement>? _unset;
    private Dictionary<string, JsonElement>? _inc;
    private Dictionary<string, JsonElement>? _min;
    private Dictionary<string, JsonElement>? _max;
    private Dictionary<string, string>? _rename;
    private Dictionary<string, JsonElement>? _push;
    private Dictionary<string, JsonElement>? _pull;
    private Dictionary<string, JsonElement>? _addToSet;
    private Dictionary<string, JsonElement>? _currentDate;
    private Dictionary<string, JsonElement>? _mul;
    private Dictionary<string, JsonElement>? _pop;

    /// <summary>添加 <c>$set</c> 操作。</summary>
    /// <param name="path">目标 JSON path。</param>
    /// <param name="value">写入值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder Set(string path, SndbDocumentValue value) { Add(ref _set, path, value); return this; }

    /// <summary>添加 <c>$unset</c> 操作。</summary>
    /// <param name="path">目标 JSON path。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder Unset(string path) { Add(ref _unset, path, true); return this; }

    /// <summary>添加 <c>$inc</c> 操作。</summary>
    /// <param name="path">目标 JSON path。</param>
    /// <param name="value">递增数值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder Increment(string path, SndbDocumentValue value) { Add(ref _inc, path, value); return this; }

    /// <summary>添加 <c>$mul</c> 操作。</summary>
    /// <param name="path">目标 JSON path。</param>
    /// <param name="value">乘数。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder Multiply(string path, SndbDocumentValue value) { Add(ref _mul, path, value); return this; }

    /// <summary>添加 <c>$min</c> 操作。</summary>
    /// <param name="path">目标 JSON path。</param>
    /// <param name="value">候选最小值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder Minimum(string path, SndbDocumentValue value) { Add(ref _min, path, value); return this; }

    /// <summary>添加 <c>$max</c> 操作。</summary>
    /// <param name="path">目标 JSON path。</param>
    /// <param name="value">候选最大值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder Maximum(string path, SndbDocumentValue value) { Add(ref _max, path, value); return this; }

    /// <summary>添加 <c>$rename</c> 操作。</summary>
    /// <param name="path">源 JSON path。</param>
    /// <param name="newPath">目标 JSON path。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder Rename(string path, string newPath)
    {
        ValidatePath(path);
        ValidatePath(newPath);
        (_rename ??= new(StringComparer.Ordinal)).Add(path, newPath);
        return this;
    }

    /// <summary>添加 <c>$push</c> 操作。</summary>
    /// <param name="path">数组 JSON path。</param>
    /// <param name="value">追加值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder Push(string path, SndbDocumentValue value) { Add(ref _push, path, value); return this; }

    /// <summary>添加 <c>$pull</c> 操作。</summary>
    /// <param name="path">数组 JSON path。</param>
    /// <param name="value">移除值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder Pull(string path, SndbDocumentValue value) { Add(ref _pull, path, value); return this; }

    /// <summary>添加 <c>$addToSet</c> 操作。</summary>
    /// <param name="path">数组 JSON path。</param>
    /// <param name="value">去重追加值。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder AddToSet(string path, SndbDocumentValue value) { Add(ref _addToSet, path, value); return this; }

    /// <summary>添加 <c>$pop</c> 操作。</summary>
    /// <param name="path">数组 JSON path。</param>
    /// <param name="direction">移除首项或末项。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder Pop(string path, SndbDocumentPopDirection direction = SndbDocumentPopDirection.Last)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "不支持的 pop 方向。");
        Add(ref _pop, path, (int)direction);
        return this;
    }

    /// <summary>添加 <c>$currentDate</c> 操作。</summary>
    /// <param name="path">目标 JSON path。</param>
    /// <param name="kind">日期写入形式。</param>
    /// <returns>当前 builder。</returns>
    public SndbDocumentUpdateBuilder CurrentDate(string path, SndbDocumentCurrentDateKind kind = SndbDocumentCurrentDateKind.Date)
    {
        string value = kind switch
        {
            SndbDocumentCurrentDateKind.Date => "date",
            SndbDocumentCurrentDateKind.Timestamp => "timestamp",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "不支持的 current date 类型。"),
        };
        Add(ref _currentDate, path, value);
        return this;
    }

    /// <summary>
    /// 生成局部更新 DTO。
    /// </summary>
    /// <returns>可直接传给 Document 更新 API 的操作符集合。</returns>
    public SndbDocumentUpdate Build()
    {
        if (_set is null && _unset is null && _inc is null && _min is null && _max is null
            && _rename is null && _push is null && _pull is null && _addToSet is null && _currentDate is null
            && _mul is null && _pop is null)
        {
            throw new InvalidOperationException("至少需要添加一个更新操作。");
        }

        return new SndbDocumentUpdate(
            Copy(_set),
            Copy(_unset),
            Copy(_inc),
            Copy(_min),
            Copy(_max),
            _rename is null ? null : new Dictionary<string, string>(_rename, StringComparer.Ordinal),
            Copy(_push),
            Copy(_pull),
            Copy(_addToSet),
            Copy(_currentDate),
            Copy(_mul),
            Copy(_pop));
    }

    private static void Add(ref Dictionary<string, JsonElement>? target, string path, SndbDocumentValue value)
    {
        ValidatePath(path);
        (target ??= new(StringComparer.Ordinal)).Add(path, value.ToJsonElement());
    }

    private static IReadOnlyDictionary<string, JsonElement>? Copy(Dictionary<string, JsonElement>? source)
        => source is null ? null : new Dictionary<string, JsonElement>(source, StringComparer.Ordinal);

    private static void ValidatePath(string path) => ArgumentException.ThrowIfNullOrWhiteSpace(path);
}

/// <summary>
/// 构造 mixed bulk write 单项操作的便捷工厂。
/// </summary>
public static class SndbDocumentBulkWrites
{
    /// <summary>构造 insertOne 操作。</summary>
    /// <param name="id">文档 ID。</param>
    /// <param name="json">JSON 文档文本。</param>
    /// <returns>可传给 bulk write 的操作。</returns>
    public static SndbDocumentBulkWriteOperation InsertOne(string id, string json)
        => new(SndbDocumentBulkWriteOperationType.InsertOne, id, json);

    /// <summary>构造 replaceOne 操作。</summary>
    /// <param name="id">文档 ID。</param>
    /// <param name="json">JSON 文档文本。</param>
    /// <param name="upsert">文档不存在时是否插入。</param>
    /// <param name="expectedVersion">可选预期版本。</param>
    /// <returns>可传给 bulk write 的操作。</returns>
    public static SndbDocumentBulkWriteOperation ReplaceOne(
        string id,
        string json,
        bool upsert = false,
        long? expectedVersion = null)
        => new(
            SndbDocumentBulkWriteOperationType.ReplaceOne,
            id,
            json,
            Upsert: upsert,
            ExpectedVersion: expectedVersion);

    /// <summary>构造 updateOne 操作。</summary>
    /// <param name="filter">过滤条件。</param>
    /// <param name="update">局部更新操作符。</param>
    /// <param name="id">可选 ID 等值条件。</param>
    /// <param name="upsert">未匹配时是否插入。</param>
    /// <param name="upsertId">upsert 文档 ID。</param>
    /// <param name="expectedVersion">可选预期版本。</param>
    /// <returns>可传给 bulk write 的操作。</returns>
    public static SndbDocumentBulkWriteOperation UpdateOne(
        SndbDocumentFilter? filter,
        SndbDocumentUpdate update,
        string? id = null,
        bool upsert = false,
        string? upsertId = null,
        long? expectedVersion = null)
        => new(
            SndbDocumentBulkWriteOperationType.UpdateOne,
            id,
            Filter: filter,
            Update: update,
            Upsert: upsert,
            UpsertId: upsertId,
            ExpectedVersion: expectedVersion);

    /// <summary>构造 updateMany 操作。</summary>
    /// <param name="filter">过滤条件；为空时匹配全部文档。</param>
    /// <param name="update">局部更新操作符。</param>
    /// <param name="upsert">未匹配时是否插入。</param>
    /// <param name="upsertId">upsert 文档 ID。</param>
    /// <returns>可传给 bulk write 的操作。</returns>
    public static SndbDocumentBulkWriteOperation UpdateMany(
        SndbDocumentFilter? filter,
        SndbDocumentUpdate update,
        bool upsert = false,
        string? upsertId = null)
        => new(
            SndbDocumentBulkWriteOperationType.UpdateMany,
            Filter: filter,
            Update: update,
            Upsert: upsert,
            UpsertId: upsertId);

    /// <summary>构造 deleteOne 操作。</summary>
    /// <param name="filter">过滤条件。</param>
    /// <param name="id">可选 ID 等值条件。</param>
    /// <param name="expectedVersion">可选预期版本。</param>
    /// <returns>可传给 bulk write 的操作。</returns>
    public static SndbDocumentBulkWriteOperation DeleteOne(
        SndbDocumentFilter? filter = null,
        string? id = null,
        long? expectedVersion = null)
        => new(
            SndbDocumentBulkWriteOperationType.DeleteOne,
            id,
            Filter: filter,
            ExpectedVersion: expectedVersion);

    /// <summary>构造 deleteMany 操作。</summary>
    /// <param name="filter">过滤条件；为空时匹配全部文档。</param>
    /// <returns>可传给 bulk write 的操作。</returns>
    public static SndbDocumentBulkWriteOperation DeleteMany(SndbDocumentFilter? filter = null)
        => new(SndbDocumentBulkWriteOperationType.DeleteMany, Filter: filter);
}

/// <summary>
/// 构造 SonnetDB-native Document aggregation 表达式。
/// </summary>
public static class SndbDocumentAggregationExpressions
{
    /// <summary>读取一个文档字段。</summary>
    /// <param name="path">字段名或 JSON path。</param>
    /// <returns>字段表达式。</returns>
    public static SndbDocumentAggregateExpression Field(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new("field", Path: path);
    }

    /// <summary>创建 JSON 字面量表达式。</summary>
    /// <param name="value">AOT 友好的 JSON 值。</param>
    /// <returns>字面量表达式。</returns>
    public static SndbDocumentAggregateExpression Literal(SndbDocumentValue value)
        => new("literal", Value: value.ToJsonElement());

    /// <summary>创建数值加法表达式。</summary>
    /// <param name="left">左输入。</param>
    /// <param name="right">右输入。</param>
    /// <returns>加法表达式。</returns>
    public static SndbDocumentAggregateExpression Add(
        SndbDocumentAggregateExpression left,
        SndbDocumentAggregateExpression right)
        => Binary("add", left, right);

    /// <summary>创建数值减法表达式。</summary>
    /// <param name="left">左输入。</param>
    /// <param name="right">右输入。</param>
    /// <returns>减法表达式。</returns>
    public static SndbDocumentAggregateExpression Subtract(
        SndbDocumentAggregateExpression left,
        SndbDocumentAggregateExpression right)
        => Binary("subtract", left, right);

    /// <summary>创建数值乘法表达式。</summary>
    /// <param name="left">左输入。</param>
    /// <param name="right">右输入。</param>
    /// <returns>乘法表达式。</returns>
    public static SndbDocumentAggregateExpression Multiply(
        SndbDocumentAggregateExpression left,
        SndbDocumentAggregateExpression right)
        => Binary("multiply", left, right);

    /// <summary>创建数值除法表达式。</summary>
    /// <param name="left">左输入。</param>
    /// <param name="right">右输入。</param>
    /// <returns>除法表达式。</returns>
    public static SndbDocumentAggregateExpression Divide(
        SndbDocumentAggregateExpression left,
        SndbDocumentAggregateExpression right)
        => Binary("divide", left, right);

    /// <summary>创建字符串拼接表达式。</summary>
    /// <param name="values">按顺序拼接的输入。</param>
    /// <returns>字符串拼接表达式。</returns>
    public static SndbDocumentAggregateExpression Concat(
        params SndbDocumentAggregateExpression[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
            throw new ArgumentException("concat 至少需要一个输入表达式。", nameof(values));
        EnsureArguments(values);
        return new("concat", Arguments: values.ToArray());
    }

    /// <summary>创建 null 替代表达式。</summary>
    /// <param name="input">主输入。</param>
    /// <param name="replacement">主输入为 null 或缺失时使用的值。</param>
    /// <returns>null 替代表达式。</returns>
    public static SndbDocumentAggregateExpression IfNull(
        SndbDocumentAggregateExpression input,
        SndbDocumentAggregateExpression replacement)
        => Binary("if_null", input, replacement);

    /// <summary>创建条件分支表达式。</summary>
    /// <param name="condition">条件输入。</param>
    /// <param name="ifTrue">条件为真时的输入。</param>
    /// <param name="ifFalse">条件为假时的输入。</param>
    /// <returns>条件表达式。</returns>
    public static SndbDocumentAggregateExpression Cond(
        SndbDocumentAggregateExpression condition,
        SndbDocumentAggregateExpression ifTrue,
        SndbDocumentAggregateExpression ifFalse)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(ifTrue);
        ArgumentNullException.ThrowIfNull(ifFalse);
        return new("cond", Arguments: [condition, ifTrue, ifFalse]);
    }

    private static SndbDocumentAggregateExpression Binary(
        string op,
        SndbDocumentAggregateExpression left,
        SndbDocumentAggregateExpression right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new(op, Arguments: [left, right]);
    }

    private static void EnsureArguments(IEnumerable<SndbDocumentAggregateExpression> values)
    {
        foreach (SndbDocumentAggregateExpression value in values)
            ArgumentNullException.ThrowIfNull(value);
    }
}
