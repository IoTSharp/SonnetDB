using System.Buffers.Binary;
using System.Text;

namespace SonnetDB.Tables;

internal static class TableKeyCodec
{
    private static readonly Encoding _utf8 = Encoding.UTF8;

    public static byte[] EncodePrimaryKey(TableSchema schema, IReadOnlyList<object?> rowValues)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rowValues);

        var keyValues = new object?[schema.PrimaryKey.Count];
        for (int i = 0; i < schema.PrimaryKey.Count; i++)
        {
            var column = schema.TryGetColumn(schema.PrimaryKey[i])
                ?? throw new InvalidOperationException($"PRIMARY KEY 引用了未知列 '{schema.PrimaryKey[i]}'。");
            keyValues[i] = rowValues[column.Ordinal];
        }

        return EncodePrimaryKeyValues(schema, keyValues);
    }

    public static byte[] EncodePrimaryKeyValues(TableSchema schema, IReadOnlyList<object?> keyValues)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(keyValues);
        if (keyValues.Count != schema.PrimaryKey.Count)
            throw new ArgumentException("主键值数量与 PRIMARY KEY 列数量不一致。", nameof(keyValues));

        int totalSize = 0;
        for (int i = 0; i < schema.PrimaryKey.Count; i++)
        {
            var column = schema.TryGetColumn(schema.PrimaryKey[i])
                ?? throw new InvalidOperationException($"PRIMARY KEY 引用了未知列 '{schema.PrimaryKey[i]}'。");
            var value = keyValues[i] ?? throw new InvalidOperationException($"PRIMARY KEY 列 '{column.Name}' 不允许为 NULL。");
            totalSize += GetEncodedSize(column, value);
        }

        var buffer = new byte[totalSize];
        int offset = 0;
        for (int i = 0; i < schema.PrimaryKey.Count; i++)
        {
            var column = schema.TryGetColumn(schema.PrimaryKey[i])!;
            WriteEncoded(buffer.AsSpan(offset), column, keyValues[i]!);
            offset += GetEncodedSize(column, keyValues[i]!);
        }

        return buffer;
    }

    private static int GetEncodedSize(TableColumn column, object value)
        => column.DataType switch
        {
            TableColumnType.Int64 or TableColumnType.DateTime => 8,
            TableColumnType.Float64 => 8,
            TableColumnType.Boolean => 1,
            TableColumnType.String or TableColumnType.Json => 4 + _utf8.GetByteCount((string)value),
            TableColumnType.Blob => 4 + ((byte[])value).Length,
            _ => throw new InvalidOperationException($"不支持的主键列类型 {column.DataType}。"),
        };

    private static void WriteEncoded(Span<byte> destination, TableColumn column, object value)
    {
        switch (column.DataType)
        {
            case TableColumnType.Int64:
                BinaryPrimitives.WriteInt64BigEndian(
                    destination,
                    Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            case TableColumnType.Float64:
                BinaryPrimitives.WriteInt64BigEndian(
                    destination,
                    BitConverter.DoubleToInt64Bits(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)));
                return;
            case TableColumnType.Boolean:
                destination[0] = (bool)value ? (byte)1 : (byte)0;
                return;
            case TableColumnType.DateTime:
                BinaryPrimitives.WriteInt64BigEndian(destination, ToUnixMilliseconds(value));
                return;
            case TableColumnType.String:
            case TableColumnType.Json:
                WriteLengthPrefixed(destination, _utf8.GetBytes((string)value));
                return;
            case TableColumnType.Blob:
                WriteLengthPrefixed(destination, (byte[])value);
                return;
            default:
                throw new InvalidOperationException($"不支持的主键列类型 {column.DataType}。");
        }
    }

    private static void WriteLengthPrefixed(Span<byte> destination, byte[] bytes)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination[..4], bytes.Length);
        bytes.CopyTo(destination[4..]);
    }

    private static long ToUnixMilliseconds(object value)
        => value switch
        {
            DateTimeOffset dto => dto.ToUnixTimeMilliseconds(),
            DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt).ToUnixTimeMilliseconds(),
            long ms => ms,
            _ => throw new InvalidOperationException($"无法把 {value.GetType().Name} 转换为 DATETIME。"),
        };
}
