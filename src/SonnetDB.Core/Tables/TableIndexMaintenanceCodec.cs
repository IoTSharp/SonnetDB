using System.Buffers.Binary;

namespace SonnetDB.Tables;

internal static partial class TableIndexCodec
{
    internal static byte[] EncodeIndexEntryValue(TableIndex index, TableSchema schema,
        IReadOnlyList<object?> values, ReadOnlySpan<byte> primaryKey)
    {
        if (index.IncludedColumns.Count == 0) return primaryKey.ToArray();
        var projection = new object?[schema.Columns.Count];
        foreach (string name in index.IncludedColumns)
        {
            int ordinal = schema.TryGetColumn(name)!.Ordinal;
            projection[ordinal] = values[ordinal];
        }
        byte[] payload = TableRowCodec.Encode(schema, projection);
        byte[] result = new byte[checked(9 + primaryKey.Length + payload.Length)];
        result[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1), primaryKey.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(5), payload.Length);
        primaryKey.CopyTo(result.AsSpan(9));
        payload.CopyTo(result.AsSpan(9 + primaryKey.Length));
        return result;
    }

    internal static ReadOnlySpan<byte> DecodeIndexPrimaryKey(TableIndex index, ReadOnlySpan<byte> value)
    {
        if (index.IncludedColumns.Count == 0) return value;
        if (value.Length < 9 || value[0] != 1) throw new InvalidDataException("索引包含列载荷版本或头部无效。");
        int primaryLength = BinaryPrimitives.ReadInt32LittleEndian(value[1..]);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(value[5..]);
        if (primaryLength <= 0 || primaryLength > value.Length - 9
            || payloadLength < 0 || payloadLength != value.Length - 9 - primaryLength)
            throw new InvalidDataException("索引包含列载荷长度无效。");
        return value.Slice(9, primaryLength);
    }

    internal static TableIndex? FindIndex(TableSchema schema, ReadOnlySpan<byte> key)
    {
        if (key.Length < 3 || key[0] != (byte)'i') return null;
        int length = BinaryPrimitives.ReadUInt16BigEndian(key[1..]);
        if (length > key.Length - 3) return null;
        try
        {
            return schema.TryGetIndex(_strictUtf8.GetString(key.Slice(3, length)));
        }
        catch (System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    internal static ReadOnlySpan<byte> DecodeIndexPrimaryKey(TableSchema schema, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        => DecodeIndexPrimaryKey(FindIndex(schema, key) ?? throw new InvalidDataException("索引键引用未知索引。"), value);

    private static void DecodeIncludedValues(TableIndex index, TableSchema schema, ReadOnlySpan<byte> entryValue, object?[] values)
    {
        if (index.IncludedColumns.Count == 0) return;
        int primaryLength = DecodeIndexPrimaryKey(index, entryValue).Length;
        object?[] included = TableRowCodec.Decode(schema, entryValue[(9 + primaryLength)..]);
        foreach (string name in index.IncludedColumns)
        {
            int ordinal = schema.TryGetColumn(name)!.Ordinal;
            values[ordinal] = included[ordinal];
        }
    }

    private static void Invert(Span<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)~bytes[i];
    }

    private static void RestoreDescendingColumn(Span<byte> bytes, int offset, TableColumnType type)
    {
        EnsureCoveredBytes(bytes, offset, 1);
        bytes[offset] = (byte)~bytes[offset];
        if (bytes[offset] > 1) throw new InvalidDataException("索引列 NULL 标记无效。");
        if (bytes[offset++] == 0) return;
        int length;
        if (type is TableColumnType.Int64 or TableColumnType.Float64 or TableColumnType.DateTime
            or TableColumnType.Time or TableColumnType.Duration or TableColumnType.DateTimeOffset)
            length = 8;
        else if (type == TableColumnType.Boolean) length = 1;
        else
        {
            EnsureCoveredBytes(bytes, offset, 4);
            Invert(bytes.Slice(offset, 4));
            length = BinaryPrimitives.ReadInt32BigEndian(bytes[offset..]);
            offset += 4;
        }
        EnsureCoveredBytes(bytes, offset, length);
        Invert(bytes.Slice(offset, length));
    }
}
