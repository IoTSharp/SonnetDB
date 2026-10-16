using System.Buffers.Binary;
using SonnetDB.Kv;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>维护索引的新编码、原生类型载荷与恢复修复合同。</summary>
public sealed class TableMaintenanceEncodingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sndb-maint-codec-{Guid.NewGuid():N}");

    /// <summary>包含列保持原生值和精度，排序键本身仍按声明方向编码。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncludedValues_NativeTypesAndNulls_RoundTrip(bool nulls)
    {
        var schema = TableSchema.Create("native", [
            ("Id", TableColumnType.Int64, false), ("Rank", TableColumnType.Int64, false),
            ("Amount", TableColumnType.Decimal, true), ("Duration", TableColumnType.Duration, true),
            ("Stamp", TableColumnType.DateTimeOffset, true), ("Blob", TableColumnType.Blob, true),
            ("Json", TableColumnType.Json, true), ("Text", TableColumnType.String, true)], ["Id"],
            [new TableIndexDefinition("Ix", ["Rank"], false)
            { Descending = [true], IncludedColumns = ["Amount", "Duration", "Stamp", "Blob", "Json", "Text"] }]);
        object?[] values = nulls ? [1L, -1L, null, null, null, null, null, null]
            : [1L, -1L, 1.2300m, TimeSpan.FromTicks(-123456789),
                new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(8)).AddTicks(1234),
                new byte[] { 0, 128, 255 }, "{\"v\":1}", "木垒\0"];
        var index = schema.Indexes[0];
        byte[] primary = TableKeyCodec.EncodePrimaryKey(schema, values);
        byte[] key = TableIndexCodec.EncodeIndexEntryKey(index, values, schema, primary);
        byte[] value = TableIndexCodec.EncodeIndexEntryValue(index, schema, values, primary);
        object?[] actual = TableIndexCodec.DecodeCoveredValues(index, schema, key, value,
            TableIndexCodec.EncodeLookupPrefix(index, [], schema)!);
        object?[] expected = TableRowCodec.Decode(schema, TableRowCodec.Encode(schema, values));
        expected[0] = null;
        Assert.Equal(expected, actual);
        Assert.Equal(primary, TableIndexCodec.DecodeIndexPrimaryKey(index, value).ToArray());
        if (!nulls) Assert.Equal(decimal.GetBits(1.2300m), decimal.GetBits(Assert.IsType<decimal>(actual[2])));
    }

    /// <summary>损坏的包含列头部、长度和降序 NULL 标记不能变成有效查询结果。</summary>
    [Theory]
    [InlineData("version")]
    [InlineData("truncated")]
    [InlineData("primary_length")]
    [InlineData("payload_length")]
    [InlineData("descending_marker")]
    [InlineData("payload")]
    public void IncludedValues_CorruptEncoding_Rejects(string corruption)
    {
        TableSchema schema = CreateSchema(false);
        TableIndex index = schema.Indexes[0];
        object?[] values = [1L, "code", "payload"];
        byte[] primary = TableKeyCodec.EncodePrimaryKey(schema, values);
        byte[] prefix = TableIndexCodec.EncodeLookupPrefix(index, [], schema)!;
        byte[] key = TableIndexCodec.EncodeIndexEntryKey(index, values, schema, primary);
        byte[] value = TableIndexCodec.EncodeIndexEntryValue(index, schema, values, primary);
        switch (corruption)
        {
            case "version": value[0] = 2; break;
            case "truncated": value = value[..8]; break;
            case "primary_length": BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(1), int.MaxValue); break;
            case "payload_length": BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(5), -1); break;
            case "descending_marker": key[prefix.Length] = 253; break;
            case "payload": value[9 + primary.Length] = 255; break;
        }
        Assert.Throws<InvalidDataException>(() => TableIndexCodec.DecodeCoveredValues(index, schema, key, value, prefix));
    }

    /// <summary>有效但陈旧的包含列载荷重开时从主行修复，同一主键不误报唯一冲突。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncludedValues_StalePayloadAfterUncleanClose_RepairsFromBaseRow(bool unique)
    {
        var schema = CreateSchema(unique);
        var index = schema.Indexes[0];
        using (var keyspace = KvKeyspace.Open("maintenance", _root, KvOptions.Default))
        using (var store = new TableStore(schema, keyspace))
            store.Insert([1L, "code", "current"]);
        using (var keyspace = KvKeyspace.Open("maintenance", _root, KvOptions.Default))
        {
            byte[] primary = TableKeyCodec.EncodePrimaryKeyValues(schema, [1L]);
            byte[] key = TableIndexCodec.EncodeIndexEntryKey(index, [1L, "code", "current"], schema, primary);
            keyspace.Put(key, TableIndexCodec.EncodeIndexEntryValue(index, schema, [1L, "code", "stale"], primary));
        }
        using var recoveredSpace = KvKeyspace.Open("maintenance", _root, KvOptions.Default);
        using var recovered = new TableStore(schema, recoveredSpace);
        int decoded = 0;
        recovered.RowDecodedTestHook = _ => decoded++;
        Assert.Equal("current", Assert.Single(recovered.EnumerateCoveredIndex(index, ["code"])).Values[2]);
        Assert.Equal(0, decoded);
        if (unique) Assert.Throws<TableConstraintException>(() => recovered.Insert([2L, "code", "duplicate"]));
    }

    private static TableSchema CreateSchema(bool unique)
        => TableSchema.Create("maintenance", [("Id", TableColumnType.Int64, false),
            ("Code", TableColumnType.String, true), ("Payload", TableColumnType.String, true)], ["Id"],
            [new TableIndexDefinition("Ix", ["Code"], unique) { Descending = [true], IncludedColumns = ["Payload"] }]);

    /// <summary>清理独占数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
