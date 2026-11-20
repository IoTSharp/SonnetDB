using System.Buffers;
using System.Text;
using System.Text.Json;
using SonnetDB.Model;

namespace SonnetDB.Ingest;

/// <summary>
/// JSON 数组形式的批量 reader：基于 <see cref="Utf8JsonReader"/> 流式解析，避免一次性反序列化大对象。
/// </summary>
/// <remarks>
/// 期望 JSON 形如：
/// <code>
/// {
///   "m": "sensor_data",        // 可选；若 ctor 传入了 measurementOverride 则忽略
///   "precision": "ms",          // 可选：ns / us / ms / s（默认 ms）
///   "points": [
///     { "t": 1704067200000, "tags": { "host": "server001" }, "fields": { "value": 0.5 } },
///     { "t": 1704067200001, "tags": { "host": "server001" }, "fields": { "value": 0.6, "ok": true, "msg": "hi" } }
///   ]
/// }
/// </code>
/// </remarks>
public sealed class JsonPointsReader : IPointReader, IDisposable
{
    private readonly ReadOnlyMemory<byte> _utf8Memory;
    private readonly byte[]? _pooledBuffer; // 非 null 时需在 Dispose 中返还到 ArrayPool
    private readonly TimePrecision _precision;
    private readonly string? _measurementOverride;

    private string? _measurementFromBody;
    private TimePrecision _precisionFromBody;
    private bool _initialized;
    private int _arrayElementCursor; // 仅做计数，便于诊断；真正的状态保存在 _readerState

    private JsonReaderState _readerState;
    private int _readerPosition;

    /// <summary>从已经解码为 UTF-8 的字节切片构造 reader（零拷贝；caller 需保证 buffer 生命周期覆盖 reader）。</summary>
    public JsonPointsReader(
        ReadOnlyMemory<byte> utf8Json,
        TimePrecision precision = TimePrecision.Milliseconds,
        string? measurementOverride = null)
    {
        // PR #47：直接持有 ROM<byte>，不再复制。Utf8JsonReader 按需 .Span 读取即可。
        _utf8Memory = utf8Json;
        _pooledBuffer = null;
        _precision = precision;
        _precisionFromBody = precision;
        _measurementOverride = measurementOverride;
    }

    /// <summary>从字符串构造 reader。会一次性 UTF-8 编码到内部缓冲。</summary>
    public JsonPointsReader(
        ReadOnlyMemory<char> json,
        TimePrecision precision = TimePrecision.Milliseconds,
        string? measurementOverride = null)
    {
        int max = Encoding.UTF8.GetMaxByteCount(json.Length);
        var buf = ArrayPool<byte>.Shared.Rent(max);
        int len = Encoding.UTF8.GetBytes(json.Span, buf);
        _pooledBuffer = buf;
        _utf8Memory = new ReadOnlyMemory<byte>(buf, 0, len);
        _precision = precision;
        _precisionFromBody = precision;
        _measurementOverride = measurementOverride;
    }

    /// <inheritdoc />
    public bool TryRead(out Point point)
    {
        if (!_initialized)
        {
            InitializeAndSeekIntoPoints();
            _initialized = true;
        }

        var slice = _utf8Memory.Span.Slice(_readerPosition);
        var reader = new Utf8JsonReader(slice, isFinalBlock: true, _readerState);

        // 在 points 数组内：下一个 token 应是 StartObject 或 EndArray
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    try
                    {
                        point = ReadPoint(ref reader);
                        _readerState = reader.CurrentState;
                        _readerPosition += checked((int)reader.BytesConsumed);
                        _arrayElementCursor++;
                        return true;
                    }
                    catch (BulkIngestException)
                    {
                        AdvancePastFailedObject(ref reader);
                        throw;
                    }
                    catch (Exception exception) when (exception is ArgumentException
                        or JsonException or FormatException or InvalidOperationException or OverflowException)
                    {
                        AdvancePastFailedObject(ref reader);
                        throw new BulkIngestException(
                            $"JSON point: {exception.Message}", exception);
                    }

                case JsonTokenType.EndArray:
                    _readerState = reader.CurrentState;
                    _readerPosition += (int)reader.BytesConsumed;
                    point = null!;
                    return false;

                case JsonTokenType.Comment:
                    continue;

                default:
                    throw new BulkIngestException(
                        $"JSON: 在 points 数组内遇到非预期 token {reader.TokenType}（第 {_arrayElementCursor + 1} 个元素位置）。");
            }
        }

        point = null!;
        return false;
    }

    private void AdvancePastFailedObject(ref Utf8JsonReader reader)
    {
        // Point.Create 校验失败时 ReadPoint 可能已经读到 EndObject，也可能停在对象内部。
        // 从对象起点单独跳过整个 JSON value，确保 onerror=skip 的下一次读取从下一个元素开始。
        var remaining = _utf8Memory.Span.Slice(_readerPosition);
        var skipper = new Utf8JsonReader(remaining, isFinalBlock: true, _readerState);
        try
        {
            if (skipper.Read() && skipper.TokenType == JsonTokenType.StartObject)
            {
                skipper.Skip();
                _readerState = skipper.CurrentState;
                _readerPosition += checked((int)skipper.BytesConsumed);
                return;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // 语法本身损坏时无法安全跳到下一个对象，保留当前 parser 已消费的位置。
        }

        _readerState = reader.CurrentState;
        _readerPosition += checked((int)reader.BytesConsumed);
    }

    private void InitializeAndSeekIntoPoints()
    {
        var span = _utf8Memory.Span;
        var reader = new Utf8JsonReader(span, isFinalBlock: true, default);

        // 期望 StartObject
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new BulkIngestException("JSON: 顶层必须是对象 '{'。");

        bool seenPointsArray = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new BulkIngestException($"JSON: 顶层键预期 PropertyName，实际 {reader.TokenType}。");

            string propName = reader.GetString()!;
            if (!reader.Read())
                throw new BulkIngestException($"JSON: 顶层键 '{propName}' 缺少值。");

            switch (propName)
            {
                case "m":
                case "measurement":
                    if (reader.TokenType != JsonTokenType.String)
                        throw new BulkIngestException($"JSON: '{propName}' 必须为字符串。");
                    _measurementFromBody = reader.GetString();
                    break;

                case "precision":
                    if (reader.TokenType != JsonTokenType.String)
                        throw new BulkIngestException("JSON: 'precision' 必须为字符串。");
                    _precisionFromBody = ParsePrecision(reader.GetString()!);
                    break;

                case "points":
                    if (reader.TokenType != JsonTokenType.StartArray)
                        throw new BulkIngestException("JSON: 'points' 必须为数组。");
                    seenPointsArray = true;
                    _readerState = reader.CurrentState;
                    _readerPosition = (int)reader.BytesConsumed;
                    return;

                default:
                    reader.Skip();
                    break;
            }
        }

        if (!seenPointsArray)
            throw new BulkIngestException("JSON: 缺少 'points' 数组。");
    }

    private Point ReadPoint(ref Utf8JsonReader reader)
    {
        long? timestamp = null;
        Dictionary<string, string>? tags = null;
        Dictionary<string, FieldValue>? fields = null;
        string? perPointMeasurement = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new BulkIngestException($"JSON point: 预期 PropertyName，实际 {reader.TokenType}。");

            string name = reader.GetString()!;
            if (!reader.Read())
                throw new BulkIngestException($"JSON point: 键 '{name}' 缺少值。");

            switch (name)
            {
                case "t":
                case "time":
                case "timestamp":
                    timestamp = ConvertTimestamp(reader.GetInt64());
                    break;

                case "m":
                case "measurement":
                    perPointMeasurement = reader.GetString();
                    break;

                case "tags":
                    tags = ReadStringDict(ref reader);
                    break;

                case "fields":
                    fields = ReadFieldsDict(ref reader);
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        if (fields is null || fields.Count == 0)
            throw new BulkIngestException("JSON point: 至少需要一个 field。");

        string measurement = _measurementOverride
            ?? perPointMeasurement
            ?? _measurementFromBody
            ?? throw new BulkIngestException("JSON point: 既未提供顶层 'm'，也未提供单点 'measurement'。");

        return Point.Create(
            measurement,
            timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            tags,
            fields);
    }

    private long ConvertTimestamp(long raw) => _precisionFromBody switch
    {
        TimePrecision.Nanoseconds => raw / 1_000_000L,
        TimePrecision.Microseconds => raw / 1_000L,
        TimePrecision.Milliseconds => raw,
        TimePrecision.Seconds => checked(raw * 1_000L),
        _ => raw,
    };

    private static Dictionary<string, string> ReadStringDict(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new BulkIngestException($"JSON: 'tags' 必须为对象，实际 {reader.TokenType}。");
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return dict;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new BulkIngestException("JSON: tags 内预期 PropertyName。");
            string k = reader.GetString()!;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                throw new BulkIngestException($"JSON: tag '{k}' 必须为字符串。");
            dict[k] = reader.GetString()!;
        }
        throw new BulkIngestException("JSON: tags 对象未闭合。");
    }

    private static Dictionary<string, FieldValue> ReadFieldsDict(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new BulkIngestException($"JSON: 'fields' 必须为对象，实际 {reader.TokenType}。");
        var dict = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return dict;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new BulkIngestException("JSON: fields 内预期 PropertyName。");
            string k = reader.GetString()!;
            if (!reader.Read())
                throw new BulkIngestException($"JSON: field '{k}' 缺少值。");

            FieldValue v = reader.TokenType switch
            {
                JsonTokenType.Number => reader.TryGetInt64(out long l) && !LooksLikeDouble(reader)
                    ? FieldValue.FromLong(l)
                    : FieldValue.FromDouble(reader.GetDouble()),
                JsonTokenType.True => FieldValue.FromBool(true),
                JsonTokenType.False => FieldValue.FromBool(false),
                JsonTokenType.String => FieldValue.FromString(reader.GetString()!),
                _ => throw new BulkIngestException($"JSON: field '{k}' 的值类型 {reader.TokenType} 不支持。"),
            };
            dict[k] = v;
        }
        throw new BulkIngestException("JSON: fields 对象未闭合。");
    }

    // Utf8JsonReader 没有"是不是带小数点"的直接判定；采用保守策略：能转 long 即为 long
    private static bool LooksLikeDouble(in Utf8JsonReader reader) => false;

    private static TimePrecision ParsePrecision(string s) => s switch
    {
        "ns" => TimePrecision.Nanoseconds,
        "us" or "µs" => TimePrecision.Microseconds,
        "ms" => TimePrecision.Milliseconds,
        "s" => TimePrecision.Seconds,
        _ => throw new BulkIngestException($"JSON: precision 取值无效 '{s}'，应为 ns/us/ms/s。"),
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_pooledBuffer is not null)
            ArrayPool<byte>.Shared.Return(_pooledBuffer);
    }
}
