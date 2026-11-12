using System.Collections.ObjectModel;
using System.Text;
using SonnetDB.IO;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Wal;

/// <summary>
/// WAL 记录的抽象基类，所有具体记录类型均继承自此类。
/// </summary>
/// <param name="Lsn">日志序列号（单调递增）。</param>
/// <param name="TimestampUtcTicks">记录写入时刻（UTC Ticks）。</param>
public abstract record WalRecord(long Lsn, long TimestampUtcTicks);

/// <summary>显式 WAL v2 恢复预算；只约束单 Point 恢复，不改变 v1 读取合同。</summary>
/// <param name="MaxRecords">最多校验的完整记录数。</param>
/// <param name="MaxReadBytes">最多读取的 WAL 字节数（含 64 字节文件头）。</param>
/// <param name="Timeout">恢复墙钟期限；省略时为 10 秒。</param>
public sealed record AtomicPointWalRecoveryOptions(
    int MaxRecords = 100_000,
    long MaxReadBytes = 256L * 1024 * 1024,
    TimeSpan? Timeout = null)
{
    internal TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRecords);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxReadBytes, FormatSizes.WalFileHeaderSize);
        if (EffectiveTimeout <= TimeSpan.Zero || EffectiveTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(Timeout), "WAL v2 recovery timeout must be within (0, 1 hour].");
    }
}

/// <summary>
/// 数据写入记录，包含一个时序数据点的所有信息。
/// </summary>
/// <param name="Lsn">日志序列号。</param>
/// <param name="TimestampUtcTicks">记录写入时刻（UTC Ticks）。</param>
/// <param name="SeriesId">序列唯一标识（XxHash64 值）。</param>
/// <param name="PointTimestamp">数据点时间戳（Unix 毫秒）。</param>
/// <param name="FieldName">字段名称。</param>
/// <param name="Value">字段值。</param>
public sealed record WritePointRecord(
    long Lsn,
    long TimestampUtcTicks,
    ulong SeriesId,
    long PointTimestamp,
    string FieldName,
    FieldValue Value)
    : WalRecord(Lsn, TimestampUtcTicks);

/// <summary>显式 WAL v2 的单个完整 Point；不代表整个 WriteMany 批次原子提交。</summary>
/// <param name="Lsn">整点共享的日志序列号。</param>
/// <param name="TimestampUtcTicks">记录写入时刻。</param>
/// <param name="SeriesId">序列唯一标识。</param>
/// <param name="PointTimestamp">Unix 毫秒时间戳。</param>
/// <param name="Fields">按编码顺序保存的只读字段集合。</param>
public sealed record AtomicPointRecord(
    long Lsn,
    long TimestampUtcTicks,
    ulong SeriesId,
    long PointTimestamp,
    IReadOnlyDictionary<string, FieldValue> Fields)
    : WalRecord(Lsn, TimestampUtcTicks);

/// <summary>
/// 序列创建记录，包含序列的 measurement 和 tag 信息。
/// </summary>
/// <param name="Lsn">日志序列号。</param>
/// <param name="TimestampUtcTicks">记录写入时刻（UTC Ticks）。</param>
/// <param name="SeriesId">序列唯一标识。</param>
/// <param name="Measurement">Measurement 名称。</param>
/// <param name="Tags">Tag 键值对。</param>
public sealed record CreateSeriesRecord(
    long Lsn,
    long TimestampUtcTicks,
    ulong SeriesId,
    string Measurement,
    IReadOnlyDictionary<string, string> Tags)
    : WalRecord(Lsn, TimestampUtcTicks);

/// <summary>
/// 检查点记录，标记截至指定 LSN 的数据已落盘到 segment。
/// </summary>
/// <param name="Lsn">日志序列号。</param>
/// <param name="TimestampUtcTicks">记录写入时刻（UTC Ticks）。</param>
/// <param name="CheckpointLsn">检查点 LSN（截止该 LSN 的数据已落盘）。</param>
public sealed record CheckpointRecord(
    long Lsn,
    long TimestampUtcTicks,
    long CheckpointLsn)
    : WalRecord(Lsn, TimestampUtcTicks);

/// <summary>
/// WAL 截断记录，标记该位置之前的所有记录已全部落盘，可以安全截断。
/// </summary>
/// <param name="Lsn">日志序列号。</param>
/// <param name="TimestampUtcTicks">记录写入时刻（UTC Ticks）。</param>
public sealed record TruncateRecord(long Lsn, long TimestampUtcTicks)
    : WalRecord(Lsn, TimestampUtcTicks);

/// <summary>
/// 删除记录，声明 (SeriesId, FieldName) 在 [FromTimestamp, ToTimestamp] 闭区间内的所有数据点已被墓碑标记。
/// </summary>
/// <param name="Lsn">日志序列号。</param>
/// <param name="TimestampUtcTicks">记录写入时刻（UTC Ticks）。</param>
/// <param name="SeriesId">序列唯一标识。</param>
/// <param name="FieldName">字段名称。</param>
/// <param name="FromTimestamp">删除时间窗起始时间戳（Unix 毫秒，闭区间）。</param>
/// <param name="ToTimestamp">删除时间窗结束时间戳（Unix 毫秒，闭区间）。</param>
public sealed record DeleteRecord(
    long Lsn,
    long TimestampUtcTicks,
    ulong SeriesId,
    string FieldName,
    long FromTimestamp,
    long ToTimestamp)
    : WalRecord(Lsn, TimestampUtcTicks);

internal static class AtomicPointWalCodec
{
    internal const int MaxFields = 1024;
    internal const int MaxPayloadBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static byte[] Encode(ulong seriesId, long timestamp, IReadOnlyDictionary<string, FieldValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentOutOfRangeException.ThrowIfNegative(timestamp);
        if (fields.Count is < 1 or > MaxFields)
            throw new ArgumentOutOfRangeException(nameof(fields), $"Point fields must be within 1..{MaxFields}.");

        var snapshot = new List<(string Name, FieldValue Value, int Size)>(fields.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        long size = sizeof(int);
        foreach (var (name, value) in fields)
        {
            if (snapshot.Count == MaxFields)
                throw new ArgumentOutOfRangeException(nameof(fields));
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ValidateStrictUtf8(name, nameof(fields));
            if (value.Type == FieldType.String)
                ValidateStrictUtf8(value.AsString(), nameof(fields));
            if (!names.Add(name))
                throw new ArgumentException("Point fields contain a duplicate name.", nameof(fields));
            int fieldSize = WalPayloadCodec.MeasureWritePoint(name, value);
            size = checked(size + sizeof(int) + fieldSize);
            if (fieldSize <= 0 || size > MaxPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(fields), $"Point payload exceeds {MaxPayloadBytes} bytes.");
            snapshot.Add((name, value, fieldSize));
        }
        if (snapshot.Count != fields.Count)
            throw new ArgumentException("Point fields changed during encoding.", nameof(fields));

        byte[] payload = new byte[(int)size];
        var writer = new SpanWriter(payload);
        writer.WriteInt32(snapshot.Count);
        foreach (var (name, value, fieldSize) in snapshot)
        {
            writer.WriteInt32(fieldSize);
            var fieldWriter = new SpanWriter(writer.FreeSpan[..fieldSize]);
            WalPayloadCodec.WriteWritePointPayload(ref fieldWriter, seriesId, timestamp, name, value);
            writer.Advance(fieldSize);
        }
        return payload;
    }

    private static void ValidateStrictUtf8(string value, string parameterName)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("WAL v2 field names and string values must contain valid UTF-16.", parameterName, ex);
        }
    }

    internal static AtomicPointRecord Decode(ReadOnlySpan<byte> payload, long lsn, long ticks)
    {
        var reader = new SpanReader(payload);
        int count = reader.ReadInt32();
        if (count is < 1 or > MaxFields)
            throw new InvalidDataException("Invalid atomic Point field count.");
        var fields = new Dictionary<string, FieldValue>(count, StringComparer.Ordinal);
        ulong seriesId = 0;
        long timestamp = 0;
        for (int i = 0; i < count; i++)
        {
            int length = reader.ReadInt32();
            if (length < 28 || length > reader.Remaining)
                throw new InvalidDataException("Invalid atomic Point field length.");
            ReadOnlySpan<byte> fieldPayload = reader.ReadBytes(length);
            ValidateFieldPayload(fieldPayload);
            var fieldReader = new SpanReader(fieldPayload);
            WritePointRecord field = WalPayloadCodec.ReadWritePointPayload(fieldReader, lsn, ticks);
            if (WalPayloadCodec.MeasureWritePoint(field.FieldName, field.Value) != length ||
                string.IsNullOrWhiteSpace(field.FieldName) || field.PointTimestamp < 0 ||
                (i > 0 && (field.SeriesId != seriesId || field.PointTimestamp != timestamp)) ||
                !fields.TryAdd(field.FieldName, field.Value))
                throw new InvalidDataException("Invalid atomic Point field identity or shape.");
            seriesId = field.SeriesId;
            timestamp = field.PointTimestamp;
        }
        if (reader.Remaining != 0)
            throw new InvalidDataException("Unexpected atomic Point payload suffix.");
        return new AtomicPointRecord(lsn, ticks, seriesId, timestamp,
            new ReadOnlyDictionary<string, FieldValue>(fields));
    }

    private static void ValidateFieldPayload(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        reader.Skip(16);
        var type = (FieldType)reader.ReadByte();
        if (reader.ReadByte() != 0 || reader.ReadByte() != 0 || reader.ReadByte() != 0)
            throw new InvalidDataException("Invalid atomic Point padding.");
        int nameLength = reader.ReadInt32();
        if (nameLength < 1 || nameLength > reader.Remaining - sizeof(int))
            throw new InvalidDataException("Invalid atomic Point field name.");
        _ = StrictUtf8.GetCharCount(reader.ReadBytes(nameLength));
        int valueLength = reader.ReadInt32();
        bool valid = type switch
        {
            FieldType.Float64 or FieldType.Int64 => valueLength == 0 && reader.Remaining == 8,
            FieldType.Boolean => valueLength == 0 && reader.Remaining == 1 && reader.ReadByte() <= 1,
            FieldType.String => valueLength >= 0 && valueLength == reader.Remaining &&
                StrictUtf8.GetCharCount(reader.ReadBytes(valueLength)) >= 0,
            FieldType.GeoPoint => valueLength == 16 && reader.Remaining == 16,
            FieldType.Vector => ValidateVector(ref reader, valueLength),
            _ => false,
        };
        if (!valid)
            throw new InvalidDataException("Invalid atomic Point value shape.");
    }

    private static bool ValidateVector(ref SpanReader reader, int valueLength)
    {
        if (valueLength < 8 || valueLength != reader.Remaining)
            return false;
        int dimension = reader.ReadInt32();
        return dimension > 0 && (long)dimension * sizeof(float) == reader.Remaining;
    }
}
