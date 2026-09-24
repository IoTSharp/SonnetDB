using System.Buffers.Binary;
using SonnetDB.Kv;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

public sealed class TableCoveredIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sndb-covered-index-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodeCoveredValues_AllScalarTypes_MatchesPersistedRows(bool unique)
    {
        TableSchema schema = TableSchema.Create("all_types",
        [
            ("id", TableColumnType.Int64, false),
            ("number", TableColumnType.Int64, true),
            ("score", TableColumnType.Float64, true),
            ("enabled", TableColumnType.Boolean, true),
            ("occurred", TableColumnType.DateTime, true),
            ("clock", TableColumnType.Time, true),
            ("amount", TableColumnType.Decimal, true),
            ("name", TableColumnType.String, true),
            ("payload", TableColumnType.Blob, true),
            ("document", TableColumnType.Json, true),
        ], ["id"],
        [new TableIndexDefinition("ix_覆盖", ["number", "score", "enabled", "occurred", "clock", "amount", "name", "payload", "document"], unique)]);
        object?[] row = [1L, long.MinValue, -0.0d, true, DateTimeOffset.UnixEpoch, new TimeOnly(23, 59, 59),
            decimal.MaxValue, "木垒\0", new byte[] { 0, 128, 255 }, "{\"v\":1}"];
        TableIndex index = schema.Indexes[0];
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, row);
        byte[] key = TableIndexCodec.EncodeIndexEntryKey(index, row, schema, primaryKey);

        object?[] decoded = TableIndexCodec.DecodeCoveredValues(index, schema, key, primaryKey,
            TableIndexCodec.EncodeLookupPrefix(index, [], schema)!);
        object?[] persisted = TableRowCodec.Decode(schema, TableRowCodec.Encode(schema, row));
        persisted[0] = null;
        Assert.Equal(persisted, decoded);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0d), BitConverter.DoubleToInt64Bits(Assert.IsType<double>(decoded[2])));
    }

    [Fact]
    public void DecodeCoveredValues_NullsAndEmptyPayloads_RoundTrips()
    {
        TableSchema schema = TableSchema.Create("null_values",
            [("id", TableColumnType.Int64, false), ("name", TableColumnType.String, true), ("blob", TableColumnType.Blob, true)],
            ["id"], [new TableIndexDefinition("ix_values", ["name", "blob"], false)]);
        object?[] row = [1L, null, Array.Empty<byte>()];
        TableIndex index = schema.Indexes[0];
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, row);

        object?[] decoded = TableIndexCodec.DecodeCoveredValues(index, schema,
            TableIndexCodec.EncodeIndexEntryKey(index, row, schema, primaryKey), primaryKey,
            TableIndexCodec.EncodeLookupPrefix(index, [], schema)!);

        Assert.Null(decoded[1]);
        Assert.Empty(Assert.IsType<byte[]>(decoded[2]));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("null_marker")]
    [InlineData("negative_length")]
    [InlineData("suffix")]
    [InlineData("primary_key")]
    [InlineData("header")]
    public void DecodeCoveredValues_CorruptPersistedEntry_Rejects(string corruption)
    {
        TableSchema schema = TableSchema.Create("corrupt",
            [("id", TableColumnType.Int64, false), ("name", TableColumnType.String, true)],
            ["id"], [new TableIndexDefinition("ix_name", ["name"], false)]);
        TableIndex index = schema.Indexes[0];
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, [1L]);
        byte[] prefix = TableIndexCodec.EncodeLookupPrefix(index, [], schema)!;
        byte[] key = TableIndexCodec.EncodeIndexEntryKey(index, [1L, "north"], schema, primaryKey);
        switch (corruption)
        {
            case "truncated": key = key[..(prefix.Length + 2)]; break;
            case "null_marker": key[prefix.Length] = 2; break;
            case "negative_length": BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(prefix.Length + 1), -1); break;
            case "suffix": key[^1] ^= 1; break;
            case "primary_key": primaryKey = [1]; break;
            case "header": key[0] = 0; break;
        }
        Assert.Throws<InvalidDataException>(() => TableIndexCodec.DecodeCoveredValues(index, schema, key, primaryKey, prefix));
    }

    [Fact]
    public void EnumerateCoveredIndex_MultiplePages_MaintainsSnapshotAndStopsAtLimit()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        TableSchema schema = CreateRangeSchema();
        using var keyspace = KvKeyspace.Open("covered", _root, KvOptions.Default with { SyncWalOnEveryWrite = false });
        using var store = new TableStore(schema, keyspace);
        var rows = new List<IReadOnlyList<object?>>(520);
        for (int i = 0; i < 520; i++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            rows.Add(new object?[] { (long)i, "north", (long)i, "payload" });
        }
        store.InsertMany(rows);
        int snapshots = 0;
        store.ReadSnapshotAcquiredTestHook = () => snapshots++;
        using var enumerator = store.EnumerateCoveredIndex(schema.Indexes[0], ["north"],
            limit: 300, cancellationToken: deadline.Token).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var values = new List<long> { (long)enumerator.Current.Values[2]! };
        store.Upsert([256L, "north", -1L, "changed"]);
        for (int i = 1; i < 301 && enumerator.MoveNext(); i++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            values.Add((long)enumerator.Current.Values[2]!);
        }
        Assert.Equal(Enumerable.Range(0, 300).Select(static value => (long)value), values);
        Assert.Equal(1, snapshots);
    }

    [Fact]
    public void EnumerateCoveredIndex_CanceledBetweenRows_ReleasesSnapshot()
    {
        TableSchema schema = CreateRangeSchema();
        using var keyspace = KvKeyspace.Open("covered", _root, KvOptions.Default with { SyncWalOnEveryWrite = false });
        using var store = new TableStore(schema, keyspace);
        store.InsertMany([new object?[] { 1L, "north", -1L, "a" }, new object?[] { 2L, "north", 1L, "b" }]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var enumerator = store.EnumerateCoveredIndex(schema.Indexes[0], ["north"],
            cancellationToken: cancellation.Token).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => enumerator.MoveNext());
        Assert.Single(store.EnumerateCoveredIndex(schema.Indexes[0], ["north"], limit: 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnumerateCoveredIndex_SignedRangeAfterReopen_MatchesBaseRowsWithoutLargePayloadCopies(bool unique)
    {
        TableSchema schema = CreateRangeSchema(unique);
        using (var keyspace = KvKeyspace.Open("covered", _root, KvOptions.Default with { SyncWalOnEveryWrite = false }))
        using (var store = new TableStore(schema, keyspace))
        {
            string payload = new('x', 1024 * 1024);
            store.InsertMany([new object?[] { 1L, "north", -1L, payload }, new object?[] { 2L, "north", 0L, payload },
                new object?[] { 3L, "north", 1L, payload }, new object?[] { 4L, "south", 1L, payload }]);
            keyspace.Compact();
        }
        using var reopenedKeyspace = KvKeyspace.Open("covered", _root, KvOptions.Default);
        using var reopened = new TableStore(schema, reopenedKeyspace);
        var range = new TableIndexRange(schema.TryGetColumn("value")!, new(-1, true), new(1, true));
        var expected = reopened.EnumerateByIndexRange(schema.Indexes[0], ["north"], range).ToArray();
        int decodedRows = 0;
        reopened.RowDecodedTestHook = _ => decodedRows++;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var actual = reopened.EnumerateCoveredIndex(schema.Indexes[0], ["north"], range).ToArray();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected.Select(static row => row.Values[2]), actual.Select(static row => row.Values[2]));
        Assert.Equal(new object?[] { -1L, 0L, 1L }, actual.Select(static row => row.Values[2]));
        Assert.Equal(0, decodedRows);
        Assert.All(actual, row => Assert.Null(row.Values[3]));
        Assert.True(allocated < 256 * 1024, $"Covered range allocated {allocated:N0} bytes for three 1 MiB base payloads.");
        _ = reopened.EnumerateByIndexRange(schema.Indexes[0], ["north"], range).ToArray();
        Assert.Equal(3, decodedRows);
    }

    private static TableSchema CreateRangeSchema(bool unique = false)
        => TableSchema.Create("covered", [("id", TableColumnType.Int64, false), ("region", TableColumnType.String, false),
            ("value", TableColumnType.Int64, false), ("payload", TableColumnType.String, false)],
            ["id"], [new TableIndexDefinition("ix_range", ["region", "value"], unique)]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
