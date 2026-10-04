using System.Buffers.Binary;
using System.Text;
using SonnetDB.IO;

namespace SonnetDB.Tables;

internal static class TableRowCodec
{
    private static readonly Encoding _utf8 = Encoding.UTF8;

    public static byte[] Encode(TableSchema schema, IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != schema.Columns.Count)
            throw new ArgumentException("行值数量必须与表 schema 列数量一致。", nameof(values));

        int totalSize = schema.Columns.Count;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var value = values[i];
            if (value is null)
                continue;

            totalSize += GetPayloadSize(schema.Columns[i], value);
        }

        var buffer = new byte[totalSize];
        var writer = new SpanWriter(buffer);
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var value = values[i];
            if (value is null)
            {
                writer.WriteByte(0);
                continue;
            }

            writer.WriteByte(1);
            WriteValue(ref writer, schema.Columns[i], value);
        }

        return buffer;
    }

    public static object?[] Decode(TableSchema schema, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var reader = new SpanReader(payload);
        var values = new object?[schema.Columns.Count];

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            byte present = reader.ReadByte();
            values[i] = present switch
            {
                0 => null,
                1 => ReadValue(ref reader, schema.Columns[i]),
                _ => throw new InvalidDataException($"Table row: invalid null marker {present}."),
            };
        }

        if (!reader.IsEnd)
            throw new InvalidDataException("Table row: trailing bytes after row payload.");

        return values;
    }

    /// <summary>完整校验行格式，只实例化索引恢复需要的列，避免解码无关的大文本和二进制字段。</summary>
    internal static object?[] DecodeProjection(
        TableSchema schema,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<bool> materializedColumns)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (materializedColumns.Length != schema.Columns.Count)
            throw new ArgumentException("投影列掩码长度必须与表 schema 列数量一致。", nameof(materializedColumns));

        var reader = new SpanReader(payload);
        var values = new object?[schema.Columns.Count];
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            byte present = reader.ReadByte();
            if (present == 0)
                continue;
            if (present != 1)
                throw new InvalidDataException($"Table row: invalid null marker {present}.");

            if (materializedColumns[i])
                values[i] = ReadValue(ref reader, schema.Columns[i]);
            else
                SkipUnmaterializedValue(ref reader, schema.Columns[i]);
        }

        if (!reader.IsEnd)
            throw new InvalidDataException("Table row: trailing bytes after row payload.");
        return values;
    }

    /// <summary>跳过未投影的大字段，定长字段仍沿用解码校验，保留非法 decimal、时间及日期的拒绝行为。</summary>
    private static void SkipUnmaterializedValue(ref SpanReader reader, TableColumn column)
    {
        if (column.DataType is TableColumnType.String or TableColumnType.Json or TableColumnType.Blob)
        {
            int length = reader.ReadInt32();
            if (length < 0)
            {
                string kind = column.DataType == TableColumnType.Blob ? "blob" : "string";
                throw new InvalidDataException($"Table row: invalid {kind} length {length}.");
            }
            reader.Skip(length);
            return;
        }

        // 定长值分配很小；复用原解码器可确保跳过列不会弱化已有的值域校验。
        _ = ReadValue(ref reader, column);
    }

    private static int GetPayloadSize(TableColumn column, object value)
        => column.DataType switch
        {
            TableColumnType.Int64 => 8,
            TableColumnType.Float64 => 8,
            TableColumnType.Decimal => 16,
            TableColumnType.Time => 8,
            TableColumnType.Boolean => 1,
            TableColumnType.DateTime => 8,
            TableColumnType.String or TableColumnType.Json => 4 + _utf8.GetByteCount((string)value),
            TableColumnType.Blob => 4 + ((byte[])value).Length,
            _ => throw new InvalidOperationException($"不支持的关系表类型 {column.DataType}。"),
        };

    private static void WriteValue(ref SpanWriter writer, TableColumn column, object value)
    {
        switch (column.DataType)
        {
            case TableColumnType.Int64:
                writer.WriteInt64(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            case TableColumnType.Float64:
                writer.WriteDouble(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            case TableColumnType.Decimal:
                foreach (int part in decimal.GetBits(Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)))
                    writer.WriteInt32(part);
                return;
            case TableColumnType.Time:
                writer.WriteInt64(ConvertTimeValue(value).Ticks);
                return;
            case TableColumnType.Boolean:
                writer.WriteByte((bool)value ? (byte)1 : (byte)0);
                return;
            case TableColumnType.DateTime:
                writer.WriteInt64(ToUnixMilliseconds(value));
                return;
            case TableColumnType.String:
            case TableColumnType.Json:
                WriteString(ref writer, (string)value);
                return;
            case TableColumnType.Blob:
                WriteBytes(ref writer, (byte[])value);
                return;
            default:
                throw new InvalidOperationException($"不支持的关系表类型 {column.DataType}。");
        }
    }

    private static object ReadValue(ref SpanReader reader, TableColumn column)
    {
        return column.DataType switch
        {
            TableColumnType.Int64 => reader.ReadInt64(),
            TableColumnType.Float64 => reader.ReadDouble(),
            TableColumnType.Decimal => ReadDecimal(ref reader),
            TableColumnType.Time => TimeOnly.FromTimeSpan(TimeSpan.FromTicks(reader.ReadInt64())),
            TableColumnType.Boolean => reader.ReadByte() != 0,
            TableColumnType.DateTime => DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64()).UtcDateTime,
            TableColumnType.String => ReadString(ref reader),
            TableColumnType.Json => ReadString(ref reader),
            TableColumnType.Blob => ReadBytes(ref reader),
            _ => throw new InvalidOperationException($"不支持的关系表类型 {column.DataType}。"),
        };
    }

    private static void WriteString(ref SpanWriter writer, string value)
    {
        int byteCount = _utf8.GetByteCount(value);
        writer.WriteInt32(byteCount);
        int written = _utf8.GetBytes(value, writer.FreeSpan);
        writer.Advance(written);
    }

    private static decimal ReadDecimal(ref SpanReader reader)
    {
        try
        {
            return new decimal([
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
            ]);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Table row: invalid decimal payload.", exception);
        }
    }

    private static string ReadString(ref SpanReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0)
            throw new InvalidDataException($"Table row: invalid string length {length}.");
        return _utf8.GetString(reader.ReadBytes(length));
    }

    private static void WriteBytes(ref SpanWriter writer, byte[] value)
    {
        writer.WriteInt32(value.Length);
        writer.WriteBytes(value);
    }

    private static byte[] ReadBytes(ref SpanReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0)
            throw new InvalidDataException($"Table row: invalid blob length {length}.");
        return reader.ReadBytes(length).ToArray();
    }

    private static long ToUnixMilliseconds(object value)
    {
        return value switch
        {
            DateTimeOffset dto => dto.ToUnixTimeMilliseconds(),
            DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt).ToUnixTimeMilliseconds(),
            long ms => ms,
            _ => throw new InvalidOperationException($"无法把 {value.GetType().Name} 转换为 DATETIME。"),
        };
    }

    private static TimeOnly ConvertTimeValue(object value)
    {
        if (value is TimeOnly time)
            return time;
        if (value is TimeSpan span && span >= TimeSpan.Zero && span < TimeSpan.FromDays(1))
            return TimeOnly.FromTimeSpan(span);
        if (value is string text && TimeOnly.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;
        throw new InvalidOperationException($"无法把 {value.GetType().Name} 转换为 TIME。");
    }
}
