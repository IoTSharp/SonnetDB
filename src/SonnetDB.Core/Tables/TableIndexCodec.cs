using System.Buffers.Binary;
using System.Text;
using SonnetDB.Documents;
using SonnetDB.Storage.Codecs;

namespace SonnetDB.Tables;

internal static class TableIndexCodec
{
    private static readonly Encoding _utf8 = Encoding.UTF8;

    public static byte[] EncodeIndexPrefix(TableIndex index, IReadOnlyList<object?> rowValues, TableSchema schema)
        => TryEncodeIndexPrefix(index, rowValues, schema)
            ?? throw new InvalidOperationException($"索引 '{index.Name}' 的 JSON path 值为空，无法编码索引键。");

    public static byte[]? TryEncodeIndexPrefix(TableIndex index, IReadOnlyList<object?> rowValues, TableSchema schema)
        => TryEncodeIndexPrefix(index, rowValues, schema, out _);

    /// <summary>编码索引 prefix，并返回普通索引列是否包含 NULL。</summary>
    private static byte[]? TryEncodeIndexPrefix(
        TableIndex index,
        IReadOnlyList<object?> rowValues,
        TableSchema schema,
        out bool hasNullColumn)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(rowValues);
        ArgumentNullException.ThrowIfNull(schema);

        byte[] indexNameBytes = _utf8.GetBytes(index.Name);
        ValidateIndexNameLength(index, indexNameBytes.Length);
        int? totalSize = TryGetIndexPrefixLength(
            index,
            rowValues,
            schema,
            indexNameBytes.Length,
            out object? pathValue,
            out hasNullColumn);
        if (totalSize is null)
            return null;

        var buffer = new byte[totalSize.Value];
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

        if (indexColumnValues.Count > index.Columns.Count)
            throw new ArgumentException("索引值必须从首列开始连续提供，且不能超过索引列数量。", nameof(indexColumnValues));
        if (!string.IsNullOrWhiteSpace(index.JsonPath) && indexColumnValues.Count > 1)
            throw new ArgumentException("JSON path 索引最多提供一个索引值。", nameof(indexColumnValues));

        byte[] indexNameBytes = _utf8.GetBytes(index.Name);
        if (indexNameBytes.Length > ushort.MaxValue)
            throw new InvalidOperationException($"索引 '{index.Name}' 名称过长。");

        int totalSize = 1 + 2 + indexNameBytes.Length;
        if (!string.IsNullOrWhiteSpace(index.JsonPath) && indexColumnValues.Count == 1)
        {
            ResolveJsonPathColumn(index, schema);
            if (indexColumnValues[0] is null)
                return null;
            totalSize += GetEncodedScalarSize(indexColumnValues[0]);
        }
        else
        {
            for (int i = 0; i < indexColumnValues.Count; i++)
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

        if (!string.IsNullOrWhiteSpace(index.JsonPath) && indexColumnValues.Count == 1)
        {
            offset += WriteEncodedScalar(buffer.AsSpan(offset), indexColumnValues[0]);
        }
        else
        {
            for (int i = 0; i < indexColumnValues.Count; i++)
            {
                var column = schema.TryGetColumn(index.Columns[i])!;
                offset += WriteEncodedValue(buffer.AsSpan(offset), column, indexColumnValues[i]);
            }
        }

        return buffer;
    }

    /// <summary>
    /// 在连续等值前缀后编码下一列的有符号范围界值，不改变既有索引值编码。
    /// </summary>
    public static byte[] EncodeRangeValuePrefix(
        TableIndex index,
        IReadOnlyList<object?> equalityPrefixValues,
        long value,
        TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(equalityPrefixValues);
        ArgumentNullException.ThrowIfNull(schema);
        if (!string.IsNullOrWhiteSpace(index.JsonPath)
            || equalityPrefixValues.Count >= index.Columns.Count)
        {
            throw new ArgumentException("范围界值必须位于普通联合索引等值前缀的下一列。", nameof(equalityPrefixValues));
        }

        var values = new object?[equalityPrefixValues.Count + 1];
        for (int i = 0; i < equalityPrefixValues.Count; i++)
            values[i] = equalityPrefixValues[i];
        values[^1] = value;
        return EncodeLookupPrefix(index, values, schema)
            ?? throw new InvalidOperationException($"索引 '{index.Name}' 的范围界值无法编码。");
    }

    /// <summary>
    /// 计算覆盖指定字节前缀全部 key 的最小排他上界。
    /// </summary>
    public static byte[] GetPrefixSuccessor(ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty)
            throw new ArgumentException("空前缀不存在有限后继。", nameof(prefix));

        byte[] successor = prefix.ToArray();
        for (int i = successor.Length - 1; i >= 0; i--)
        {
            if (successor[i] == byte.MaxValue)
                continue;

            successor[i]++;
            return successor[..(i + 1)];
        }

        throw new ArgumentException("全 0xFF 前缀不存在有限后继。", nameof(prefix));
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
        byte[]? prefix = TryEncodeIndexPrefix(index, rowValues, schema, out bool hasNullColumn);
        if (prefix is null)
            return null;
        if (index.IsUnique && hasNullColumn)
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

    /// <summary>
    /// 在不创建索引 key 的前提下计算其精确编码长度；不产生索引项时返回 false。
    /// </summary>
    /// <param name="index">待计算的索引定义。</param>
    /// <param name="rowValues">按 schema 顺序排列的行值。</param>
    /// <param name="schema">关系表 schema。</param>
    /// <param name="primaryKey">已编码的主键。</param>
    /// <param name="encodedLength">成功时返回完整索引 key 的字节数。</param>
    /// <returns>该行会产生索引项时返回 true；JSON path 缺失或唯一索引含 NULL 时返回 false。</returns>
    public static bool TryGetIndexEntryKeyLength(
        TableIndex index,
        IReadOnlyList<object?> rowValues,
        TableSchema schema,
        ReadOnlySpan<byte> primaryKey,
        out int encodedLength)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(rowValues);
        ArgumentNullException.ThrowIfNull(schema);

        int indexNameLength = _utf8.GetByteCount(index.Name);
        ValidateIndexNameLength(index, indexNameLength);
        int? prefixLength = TryGetIndexPrefixLength(
            index,
            rowValues,
            schema,
            indexNameLength,
            out _,
            out bool hasNullColumn);

        if (prefixLength is null || (index.IsUnique && hasNullColumn))
        {
            encodedLength = 0;
            return false;
        }

        int suffixLength = index.IsUnique ? 0 : checked(4 + primaryKey.Length);
        encodedLength = checked(prefixLength.Value + suffixLength);
        return true;
    }

    /// <summary>
    /// 统一计算索引 prefix 长度与可索引性，供真实编码和统计测量共享格式决策。
    /// </summary>
    private static int? TryGetIndexPrefixLength(
        TableIndex index,
        IReadOnlyList<object?> rowValues,
        TableSchema schema,
        int indexNameLength,
        out object? pathValue,
        out bool hasNullColumn)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(rowValues);
        ArgumentNullException.ThrowIfNull(schema);

        int prefixLength = checked(1 + 2 + indexNameLength);
        pathValue = null;
        hasNullColumn = false;
        if (!string.IsNullOrWhiteSpace(index.JsonPath))
        {
            TableColumn column = ResolveJsonPathColumn(index, schema);
            pathValue = JsonPathEvaluator.Evaluate(rowValues[column.Ordinal] as string, index.JsonPath);
            return pathValue is null
                ? null
                : checked(prefixLength + GetEncodedScalarSize(pathValue));
        }

        foreach (string columnName in index.Columns)
        {
            TableColumn column = schema.TryGetColumn(columnName)
                ?? throw new InvalidOperationException($"索引 '{index.Name}' 引用了未知列 '{columnName}'。");
            object? value = rowValues[column.Ordinal];
            hasNullColumn |= value is null;
            prefixLength = checked(prefixLength + GetEncodedValueSize(column, value));
        }

        return prefixLength;
    }

    /// <summary>校验索引名称的 UTF-8 长度是否能写入现有二进制格式。</summary>
    private static void ValidateIndexNameLength(TableIndex index, int indexNameLength)
    {
        if ((uint)indexNameLength > ushort.MaxValue)
            throw new InvalidOperationException($"索引 '{index.Name}' 名称过长。");
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
                SortableScalarCodec.WriteTableLegacyInt64(
                    payload,
                    Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                return 9;
            case TableColumnType.Float64:
                SortableScalarCodec.WriteTableLegacyDouble(
                    payload,
                    Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                return 9;
            case TableColumnType.Boolean:
                payload[0] = (bool)value ? (byte)1 : (byte)0;
                return 2;
            case TableColumnType.DateTime:
                SortableScalarCodec.WriteTableLegacyDateTime(payload, ToUnixMilliseconds(value));
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
        SortableScalarCodec.WriteTableLegacyLengthPrefixed(destination, bytes);
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
