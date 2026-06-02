using System.Buffers.Binary;
using System.Text;
using SonnetDB.Documents;

namespace SonnetDB.Tables;

internal static class TableIndexCodec
{
    private static readonly Encoding _utf8 = Encoding.UTF8;

    public static byte[] EncodeIndexPrefix(TableIndex index, IReadOnlyList<object?> rowValues, TableSchema schema)
        => TryEncodeIndexPrefix(index, rowValues, schema)
            ?? throw new InvalidOperationException($"索引 '{index.Name}' 的 JSON path 值为空，无法编码索引键。");

    public static byte[]? TryEncodeIndexPrefix(TableIndex index, IReadOnlyList<object?> rowValues, TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(rowValues);
        ArgumentNullException.ThrowIfNull(schema);

        byte[] indexNameBytes = _utf8.GetBytes(index.Name);
        if (indexNameBytes.Length > ushort.MaxValue)
            throw new InvalidOperationException($"索引 '{index.Name}' 名称过长。");

        int totalSize = 1 + 2 + indexNameBytes.Length;
        object? pathValue = null;
        if (!string.IsNullOrWhiteSpace(index.JsonPath))
        {
            var column = ResolveJsonPathColumn(index, schema);
            pathValue = JsonPathEvaluator.Evaluate(rowValues[column.Ordinal] as string, index.JsonPath);
            if (pathValue is null)
                return null;
            totalSize += GetEncodedScalarSize(pathValue);
        }
        else
        {
            foreach (var columnName in index.Columns)
            {
                var column = schema.TryGetColumn(columnName)
                    ?? throw new InvalidOperationException($"索引 '{index.Name}' 引用了未知列 '{columnName}'。");
                totalSize += GetEncodedValueSize(column, rowValues[column.Ordinal]);
            }
        }

        var buffer = new byte[totalSize];
        int offset = 0;
        buffer[offset++] = (byte)'i';
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)indexNameBytes.Length);
        offset += 2;
        indexNameBytes.CopyTo(buffer.AsSpan(offset));
        offset += indexNameBytes.Length;
        if (!string.IsNullOrWhiteSpace(index.JsonPath))
        {
            offset += WriteEncodedScalar(buffer.AsSpan(offset), pathValue);
        }
        else
        {
            foreach (var columnName in index.Columns)
            {
                var column = schema.TryGetColumn(columnName)!;
                offset += WriteEncodedValue(buffer.AsSpan(offset), column, rowValues[column.Ordinal]);
            }
        }

        return buffer;
    }

    public static byte[]? EncodeLookupPrefix(TableIndex index, IReadOnlyList<object?> indexColumnValues, TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(indexColumnValues);
        ArgumentNullException.ThrowIfNull(schema);

        if (indexColumnValues.Count != index.Columns.Count)
            throw new ArgumentException("索引值数量与索引列数量不一致。", nameof(indexColumnValues));

        byte[] indexNameBytes = _utf8.GetBytes(index.Name);
        if (indexNameBytes.Length > ushort.MaxValue)
            throw new InvalidOperationException($"索引 '{index.Name}' 名称过长。");

        int totalSize = 1 + 2 + indexNameBytes.Length;
        if (!string.IsNullOrWhiteSpace(index.JsonPath))
        {
            ResolveJsonPathColumn(index, schema);
            if (indexColumnValues[0] is null)
                return null;
            totalSize += GetEncodedScalarSize(indexColumnValues[0]);
        }
        else
        {
            for (int i = 0; i < index.Columns.Count; i++)
            {
                var column = schema.TryGetColumn(index.Columns[i])
                    ?? throw new InvalidOperationException($"索引 '{index.Name}' 引用了未知列 '{index.Columns[i]}'。");
                totalSize += GetEncodedValueSize(column, indexColumnValues[i]);
            }
        }

        var buffer = new byte[totalSize];
        int offset = 0;
        buffer[offset++] = (byte)'i';
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)indexNameBytes.Length);
        offset += 2;
        indexNameBytes.CopyTo(buffer.AsSpan(offset));
        offset += indexNameBytes.Length;

        if (!string.IsNullOrWhiteSpace(index.JsonPath))
        {
            offset += WriteEncodedScalar(buffer.AsSpan(offset), indexColumnValues[0]);
        }
        else
        {
            for (int i = 0; i < index.Columns.Count; i++)
            {
                var column = schema.TryGetColumn(index.Columns[i])!;
                offset += WriteEncodedValue(buffer.AsSpan(offset), column, indexColumnValues[i]);
            }
        }

        return buffer;
    }

    public static byte[] EncodePrimaryRowKey(ReadOnlySpan<byte> primaryKey)
    {
        var key = new byte[1 + primaryKey.Length];
        key[0] = (byte)'r';
        primaryKey.CopyTo(key.AsSpan(1));
        return key;
    }

    public static ReadOnlyMemory<byte> DecodePrimaryKeyFromRowKey(ReadOnlyMemory<byte> rowKey)
    {
        if (rowKey.Length == 0 || rowKey.Span[0] != (byte)'r')
            throw new InvalidDataException("Table row key is invalid.");
        return rowKey[1..];
    }

    public static byte[] EncodeIndexEntryKey(TableIndex index, IReadOnlyList<object?> rowValues, TableSchema schema, ReadOnlySpan<byte> primaryKey)
        => TryEncodeIndexEntryKey(index, rowValues, schema, primaryKey)
            ?? throw new InvalidOperationException($"索引 '{index.Name}' 的 JSON path 值为空，无法编码索引键。");

    public static byte[]? TryEncodeIndexEntryKey(TableIndex index, IReadOnlyList<object?> rowValues, TableSchema schema, ReadOnlySpan<byte> primaryKey)
    {
        byte[]? prefix = TryEncodeIndexPrefix(index, rowValues, schema);
        if (prefix is null)
            return null;
        int suffixBytes = index.IsUnique ? 0 : 4 + primaryKey.Length;
        var key = new byte[prefix.Length + suffixBytes];
        prefix.CopyTo(key);
        if (!index.IsUnique)
        {
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(prefix.Length, 4), primaryKey.Length);
            primaryKey.CopyTo(key.AsSpan(prefix.Length + 4));
        }

        return key;
    }

    public static byte[] EncodeIndexEntryValue(ReadOnlySpan<byte> primaryKey)
        => primaryKey.ToArray();

    private static int GetEncodedValueSize(TableColumn column, object? value)
        => value is null
            ? 1
            : column.DataType switch
            {
                TableColumnType.Int64 or TableColumnType.DateTime => 1 + 8,
                TableColumnType.Float64 => 1 + 8,
                TableColumnType.Boolean => 1 + 1,
                TableColumnType.String or TableColumnType.Json => 1 + 4 + _utf8.GetByteCount((string)value),
                TableColumnType.Blob => 1 + 4 + ((byte[])value).Length,
                _ => throw new InvalidOperationException($"不支持的索引列类型 {column.DataType}。"),
            };

    private static int GetEncodedScalarSize(object? value)
    {
        var scalar = JsonPathEvaluator.ToIndexScalar(value);
        return scalar is null
            ? 1
            : 1 + 4 + _utf8.GetByteCount(scalar);
    }

    private static int WriteEncodedValue(Span<byte> destination, TableColumn column, object? value)
    {
        if (value is null)
        {
            destination[0] = 0;
            return 1;
        }

        destination[0] = 1;
        var payload = destination[1..];
        switch (column.DataType)
        {
            case TableColumnType.Int64:
                BinaryPrimitives.WriteInt64BigEndian(
                    payload,
                    Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                return 9;
            case TableColumnType.Float64:
                BinaryPrimitives.WriteInt64BigEndian(
                    payload,
                    BitConverter.DoubleToInt64Bits(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)));
                return 9;
            case TableColumnType.Boolean:
                payload[0] = (bool)value ? (byte)1 : (byte)0;
                return 2;
            case TableColumnType.DateTime:
                BinaryPrimitives.WriteInt64BigEndian(payload, ToUnixMilliseconds(value));
                return 9;
            case TableColumnType.String:
            case TableColumnType.Json:
                return 1 + WriteLengthPrefixed(payload, _utf8.GetBytes((string)value));
            case TableColumnType.Blob:
                return 1 + WriteLengthPrefixed(payload, (byte[])value);
            default:
                throw new InvalidOperationException($"不支持的索引列类型 {column.DataType}。");
        }
    }

    private static int WriteEncodedScalar(Span<byte> destination, object? value)
    {
        var scalar = JsonPathEvaluator.ToIndexScalar(value);
        if (scalar is null)
        {
            destination[0] = 0;
            return 1;
        }

        destination[0] = 1;
        return 1 + WriteLengthPrefixed(destination[1..], _utf8.GetBytes(scalar));
    }

    private static int WriteLengthPrefixed(Span<byte> destination, byte[] bytes)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination[..4], bytes.Length);
        bytes.CopyTo(destination[4..]);
        return 4 + bytes.Length;
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

    private static TableColumn ResolveJsonPathColumn(TableIndex index, TableSchema schema)
    {
        if (index.Columns.Count != 1)
            throw new InvalidOperationException($"JSON path 索引 '{index.Name}' 只能引用 1 个 JSON 列。");

        var column = schema.TryGetColumn(index.Columns[0])
            ?? throw new InvalidOperationException($"索引 '{index.Name}' 引用了未知列 '{index.Columns[0]}'。");
        if (column.DataType != TableColumnType.Json)
            throw new InvalidOperationException($"JSON path 索引 '{index.Name}' 的列 '{column.Name}' 必须是 JSON 类型。");
        return column;
    }
}
