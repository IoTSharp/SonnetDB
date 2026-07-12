using System.Buffers.Binary;
using System.Text;
using SonnetDB.Ingest;
using SonnetDB.Model;

namespace SonnetDB.Mqtt;

/// <summary>
/// 手写 Sparkplug B protobuf reader，把标量 metric 直接转换为 SonnetDB <see cref="Point"/>。
/// </summary>
internal sealed class SparkplugPayloadReader : IPointReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly SparkplugTopicRoute _route;
    private readonly SparkplugAliasStore _aliases;
    private readonly IReadOnlyList<SparkplugMetric> _metrics;
    private readonly IReadOnlyDictionary<string, string> _tags;
    private readonly long _payloadTimestamp;
    private int _cursor;

    /// <summary>
    /// 创建 reader。BIRTH alias 仅在生命周期校验通过后由调用方提交。
    /// </summary>
    public SparkplugPayloadReader(
        ReadOnlyMemory<byte> payload,
        in SparkplugTopicRoute route,
        SparkplugAliasStore aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        _route = route;
        _aliases = aliases;

        var decoded = DecodePayload(payload);
        _metrics = decoded.Metrics;
        _payloadTimestamp = ToTimestamp(decoded.Timestamp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _tags = CreateTags(route);
        Sequence = decoded.Sequence;
        BirthDeathSequence = FindBirthDeathSequence(_metrics);
    }

    /// <summary>Payload 顶层的 0..255 滚动序列号。</summary>
    public byte? Sequence { get; }

    /// <summary>BIRTH/DEATH 中由 <c>bdSeq</c> metric 携带的会话序列。</summary>
    public ulong? BirthDeathSequence { get; }

    /// <summary>Payload 中解码出的 metric 数量。</summary>
    public int MetricCount => _metrics.Count;

    /// <summary>被跳过的 metric 总数。</summary>
    public int SkippedMetrics { get; private set; }

    /// <summary>因 BIRTH alias 上下文缺失而跳过的 DATA metric 数。</summary>
    public int OrphanMetrics { get; private set; }

    /// <summary>因类型不是 SonnetDB 标量 field 而跳过的 metric 数。</summary>
    public int UnsupportedMetrics { get; private set; }

    /// <summary>
    /// 生命周期校验通过后提交本次 BIRTH 的 alias 快照。
    /// </summary>
    public void CommitBirthAliases()
    {
        if (!_route.IsBirth)
            return;

        _aliases.ReplaceBirthAliases(
            _route,
            _metrics
                .Where(static metric => metric.Alias.HasValue && !string.IsNullOrWhiteSpace(metric.Name))
                .Select(static metric => (metric.Alias!.Value, metric.Name!)));
    }

    /// <inheritdoc />
    public bool TryRead(out Point point)
    {
        while (_cursor < _metrics.Count)
        {
            SparkplugMetric metric = _metrics[_cursor++];
            string? name = metric.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                if (!metric.Alias.HasValue || !_aliases.TryResolveAlias(_route, metric.Alias.Value, out name))
                {
                    OrphanMetrics++;
                    SkippedMetrics++;
                    continue;
                }
            }

            if (metric.IsNull)
            {
                SkippedMetrics++;
                continue;
            }

            if (!TryMapValue(metric, out var fieldValue))
            {
                UnsupportedMetrics++;
                SkippedMetrics++;
                continue;
            }

            string fieldName = NormalizeFieldName(name);
            long timestamp = ToTimestamp(metric.Timestamp, _payloadTimestamp);
            try
            {
                point = Point.Create(
                    _route.Measurement,
                    timestamp,
                    _tags,
                    new Dictionary<string, FieldValue>(1, StringComparer.Ordinal)
                    {
                        [fieldName] = fieldValue,
                    });
                return true;
            }
            catch (ArgumentException)
            {
                SkippedMetrics++;
            }
        }

        point = null!;
        return false;
    }

    private static SparkplugPayload DecodePayload(ReadOnlyMemory<byte> payload)
    {
        var metrics = new List<SparkplugMetric>();
        ulong? timestamp = null;
        byte? sequence = null;
        int cursor = 0;
        while (cursor < payload.Length)
        {
            var (fieldNumber, wireType) = ReadTag(payload.Span, ref cursor, "Payload");
            switch (fieldNumber)
            {
                case 1:
                    RequireWireType(wireType, 0, "Payload.timestamp");
                    timestamp = ReadVarint(payload.Span, ref cursor, "Payload.timestamp");
                    break;
                case 2:
                    RequireWireType(wireType, 2, "Payload.metrics");
                    var metricBytes = ReadLengthDelimited(payload, ref cursor, "Payload.metrics");
                    metrics.Add(DecodeMetric(metricBytes));
                    break;
                case 3:
                    RequireWireType(wireType, 0, "Payload.seq");
                    ulong rawSequence = ReadVarint(payload.Span, ref cursor, "Payload.seq");
                    if (rawSequence > byte.MaxValue)
                        throw new BulkIngestException("Sparkplug protobuf Payload.seq 必须位于 0..255。");
                    sequence = (byte)rawSequence;
                    break;
                default:
                    SkipField(payload.Span, ref cursor, wireType, "Payload");
                    break;
            }
        }

        return new SparkplugPayload(timestamp, sequence, metrics);
    }

    private static ulong? FindBirthDeathSequence(IReadOnlyList<SparkplugMetric> metrics)
    {
        foreach (SparkplugMetric metric in metrics)
        {
            if (string.Equals(metric.Name, "bdSeq", StringComparison.Ordinal)
                && metric.LongValue.HasValue)
            {
                return metric.LongValue.Value;
            }
        }

        return null;
    }

    private static SparkplugMetric DecodeMetric(ReadOnlyMemory<byte> payload)
    {
        var metric = new SparkplugMetric();
        int cursor = 0;
        while (cursor < payload.Length)
        {
            var (fieldNumber, wireType) = ReadTag(payload.Span, ref cursor, "Metric");
            switch (fieldNumber)
            {
                case 1:
                    RequireWireType(wireType, 2, "Metric.name");
                    metric.Name = ReadString(payload, ref cursor, "Metric.name");
                    break;
                case 2:
                    RequireWireType(wireType, 0, "Metric.alias");
                    metric.Alias = ReadVarint(payload.Span, ref cursor, "Metric.alias");
                    break;
                case 3:
                    RequireWireType(wireType, 0, "Metric.timestamp");
                    metric.Timestamp = ReadVarint(payload.Span, ref cursor, "Metric.timestamp");
                    break;
                case 4:
                    RequireWireType(wireType, 0, "Metric.datatype");
                    metric.DataType = checked((uint)ReadVarint(payload.Span, ref cursor, "Metric.datatype"));
                    break;
                case 7:
                    RequireWireType(wireType, 0, "Metric.is_null");
                    metric.IsNull = ReadVarint(payload.Span, ref cursor, "Metric.is_null") != 0;
                    break;
                case 10:
                    RequireWireType(wireType, 0, "Metric.int_value");
                    metric.IntValue = checked((uint)ReadVarint(payload.Span, ref cursor, "Metric.int_value"));
                    break;
                case 11:
                    RequireWireType(wireType, 0, "Metric.long_value");
                    metric.LongValue = ReadVarint(payload.Span, ref cursor, "Metric.long_value");
                    break;
                case 12:
                    RequireWireType(wireType, 5, "Metric.float_value");
                    metric.FloatValue = ReadSingle(payload.Span, ref cursor, "Metric.float_value");
                    break;
                case 13:
                    RequireWireType(wireType, 1, "Metric.double_value");
                    metric.DoubleValue = ReadDouble(payload.Span, ref cursor, "Metric.double_value");
                    break;
                case 14:
                    RequireWireType(wireType, 0, "Metric.boolean_value");
                    metric.BooleanValue = ReadVarint(payload.Span, ref cursor, "Metric.boolean_value") != 0;
                    break;
                case 15:
                    RequireWireType(wireType, 2, "Metric.string_value");
                    metric.StringValue = ReadString(payload, ref cursor, "Metric.string_value");
                    break;
                default:
                    SkipField(payload.Span, ref cursor, wireType, "Metric");
                    break;
            }
        }

        return metric;
    }

    private static bool TryMapValue(SparkplugMetric metric, out FieldValue value)
    {
        value = default;
        if (!metric.DataType.HasValue)
            return false;

        switch ((SparkplugDataType)metric.DataType.Value)
        {
            case SparkplugDataType.Int8 when metric.IntValue.HasValue:
                value = FieldValue.FromLong(unchecked((sbyte)(byte)metric.IntValue.Value));
                return true;
            case SparkplugDataType.Int16 when metric.IntValue.HasValue:
                value = FieldValue.FromLong(unchecked((short)(ushort)metric.IntValue.Value));
                return true;
            case SparkplugDataType.Int32 when metric.IntValue.HasValue:
                value = FieldValue.FromLong(unchecked((int)metric.IntValue.Value));
                return true;
            case SparkplugDataType.Int64 when metric.LongValue.HasValue:
                value = FieldValue.FromLong(unchecked((long)metric.LongValue.Value));
                return true;
            case SparkplugDataType.UInt8 when metric.IntValue.HasValue:
                value = FieldValue.FromLong((byte)metric.IntValue.Value);
                return true;
            case SparkplugDataType.UInt16 when metric.IntValue.HasValue:
                value = FieldValue.FromLong((ushort)metric.IntValue.Value);
                return true;
            case SparkplugDataType.UInt32 when metric.IntValue.HasValue:
                value = FieldValue.FromLong(metric.IntValue.Value);
                return true;
            case SparkplugDataType.UInt64 when metric.LongValue is <= long.MaxValue:
                value = FieldValue.FromLong((long)metric.LongValue.Value);
                return true;
            case SparkplugDataType.Float when metric.FloatValue.HasValue && float.IsFinite(metric.FloatValue.Value):
                value = FieldValue.FromDouble(metric.FloatValue.Value);
                return true;
            case SparkplugDataType.Double when metric.DoubleValue.HasValue && double.IsFinite(metric.DoubleValue.Value):
                value = FieldValue.FromDouble(metric.DoubleValue.Value);
                return true;
            case SparkplugDataType.Boolean when metric.BooleanValue.HasValue:
                value = FieldValue.FromBool(metric.BooleanValue.Value);
                return true;
            case SparkplugDataType.String or SparkplugDataType.Text or SparkplugDataType.Uuid
                when metric.StringValue is not null:
                value = FieldValue.FromString(metric.StringValue);
                return true;
            case SparkplugDataType.DateTime when metric.LongValue is <= long.MaxValue:
                value = FieldValue.FromLong((long)metric.LongValue.Value);
                return true;
            default:
                return false;
        }
    }

    private static IReadOnlyDictionary<string, string> CreateTags(in SparkplugTopicRoute route)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["group_id"] = route.GroupId,
            ["edge_node_id"] = route.EdgeNodeId,
        };
        if (route.DeviceId is not null)
            tags["device_id"] = route.DeviceId;
        return tags;
    }

    private static string NormalizeFieldName(string name)
    {
        if (name.AsSpan().IndexOfAny(",=\n\r\t\"") < 0)
            return name;

        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] is ',' or '=' or '\n' or '\r' or '\t' or '"')
                chars[i] = '.';
        }
        return new string(chars);
    }

    private static long ToTimestamp(ulong? value, long fallback)
        => value is <= long.MaxValue ? (long)value.Value : fallback;

    private static (int FieldNumber, int WireType) ReadTag(ReadOnlySpan<byte> span, ref int cursor, string scope)
    {
        ulong tag = ReadVarint(span, ref cursor, scope + ".tag");
        int fieldNumber = checked((int)(tag >> 3));
        int wireType = (int)(tag & 0x07);
        if (fieldNumber <= 0)
            throw new BulkIngestException($"Sparkplug protobuf {scope}: field number 必须大于 0。");
        return (fieldNumber, wireType);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int cursor, string field)
    {
        ulong value = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (cursor >= span.Length)
                throw new BulkIngestException($"Sparkplug protobuf {field}: varint 被截断。");

            byte current = span[cursor++];
            if (shift == 63 && (current & 0xFE) != 0)
                throw new BulkIngestException($"Sparkplug protobuf {field}: varint 超出 UInt64。" );
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
                return value;
        }

        throw new BulkIngestException($"Sparkplug protobuf {field}: varint 长度非法。");
    }

    private static ReadOnlyMemory<byte> ReadLengthDelimited(ReadOnlyMemory<byte> payload, ref int cursor, string field)
    {
        ulong rawLength = ReadVarint(payload.Span, ref cursor, field + ".length");
        if (rawLength > int.MaxValue || rawLength > (ulong)(payload.Length - cursor))
            throw new BulkIngestException($"Sparkplug protobuf {field}: length-delimited 字段被截断。");

        int length = (int)rawLength;
        var result = payload.Slice(cursor, length);
        cursor += length;
        return result;
    }

    private static string ReadString(ReadOnlyMemory<byte> payload, ref int cursor, string field)
    {
        try
        {
            return StrictUtf8.GetString(ReadLengthDelimited(payload, ref cursor, field).Span);
        }
        catch (DecoderFallbackException ex)
        {
            throw new BulkIngestException($"Sparkplug protobuf {field}: 字符串不是有效 UTF-8。", ex);
        }
    }

    private static float ReadSingle(ReadOnlySpan<byte> span, ref int cursor, string field)
    {
        EnsureRemaining(span, cursor, sizeof(uint), field);
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor, sizeof(uint)));
        cursor += sizeof(uint);
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    private static double ReadDouble(ReadOnlySpan<byte> span, ref int cursor, string field)
    {
        EnsureRemaining(span, cursor, sizeof(ulong), field);
        ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(cursor, sizeof(ulong)));
        cursor += sizeof(ulong);
        return BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }

    private static void SkipField(ReadOnlySpan<byte> span, ref int cursor, int wireType, string scope)
    {
        switch (wireType)
        {
            case 0:
                _ = ReadVarint(span, ref cursor, scope + ".unknown");
                break;
            case 1:
                EnsureRemaining(span, cursor, sizeof(ulong), scope + ".unknown");
                cursor += sizeof(ulong);
                break;
            case 2:
                ulong length = ReadVarint(span, ref cursor, scope + ".unknown.length");
                if (length > int.MaxValue || length > (ulong)(span.Length - cursor))
                    throw new BulkIngestException($"Sparkplug protobuf {scope}: 未知 length-delimited 字段被截断。");
                cursor += (int)length;
                break;
            case 5:
                EnsureRemaining(span, cursor, sizeof(uint), scope + ".unknown");
                cursor += sizeof(uint);
                break;
            default:
                throw new BulkIngestException($"Sparkplug protobuf {scope}: 不支持 wire type {wireType}。" );
        }
    }

    private static void RequireWireType(int actual, int expected, string field)
    {
        if (actual != expected)
            throw new BulkIngestException($"Sparkplug protobuf {field}: wire type 应为 {expected}，实际 {actual}。" );
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> span, int cursor, int count, string field)
    {
        if (cursor < 0 || count < 0 || cursor > span.Length - count)
            throw new BulkIngestException($"Sparkplug protobuf {field}: fixed-width 字段被截断。");
    }

    private sealed record SparkplugPayload(
        ulong? Timestamp,
        byte? Sequence,
        IReadOnlyList<SparkplugMetric> Metrics);

    private sealed class SparkplugMetric
    {
        public string? Name { get; set; }
        public ulong? Alias { get; set; }
        public ulong? Timestamp { get; set; }
        public uint? DataType { get; set; }
        public bool IsNull { get; set; }
        public uint? IntValue { get; set; }
        public ulong? LongValue { get; set; }
        public float? FloatValue { get; set; }
        public double? DoubleValue { get; set; }
        public bool? BooleanValue { get; set; }
        public string? StringValue { get; set; }
    }

    private enum SparkplugDataType : uint
    {
        Int8 = 1,
        Int16 = 2,
        Int32 = 3,
        Int64 = 4,
        UInt8 = 5,
        UInt16 = 6,
        UInt32 = 7,
        UInt64 = 8,
        Float = 9,
        Double = 10,
        Boolean = 11,
        String = 12,
        DateTime = 13,
        Text = 14,
        Uuid = 15,
    }
}
