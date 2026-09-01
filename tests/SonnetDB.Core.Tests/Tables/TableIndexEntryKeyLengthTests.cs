using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>索引 key 无分配长度计算与持久编码格式的差分回归。</summary>
public sealed class TableIndexEntryKeyLengthTests
{
    /// <summary>覆盖全部关系标量、复合索引、空值、Unicode 与主键后缀边界。</summary>
    [Fact]
    public void TryGetIndexEntryKeyLength_AllColumnTypes_MatchesEncodedKey()
    {
        TableSchema schema = TableSchema.Create(
            "entry_lengths",
            [
                ("id", TableColumnType.Int64, false),
                ("number", TableColumnType.Int64, true),
                ("score", TableColumnType.Float64, true),
                ("enabled", TableColumnType.Boolean, true),
                ("occurred", TableColumnType.DateTime, true),
                ("name", TableColumnType.String, true),
                ("payload", TableColumnType.Blob, true),
                ("metadata", TableColumnType.Json, true),
            ],
            ["id"],
            [
                new TableIndexDefinition(
                    "ix_全部类型",
                    ["number", "score", "enabled", "occurred", "name", "payload", "metadata"],
                    false),
                new TableIndexDefinition("ux_name", ["name"], true),
            ]);
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, [long.MinValue]);
        object?[] row =
        [
            long.MinValue,
            long.MaxValue,
            -0.0d,
            true,
            DateTimeOffset.UnixEpoch,
            "木垒-\0-vehicle",
            new byte[] { 0, 1, 127, 128, 255 },
            "{\"site\":\"north\",\"value\":1}",
        ];

        foreach (TableIndex index in schema.Indexes)
            AssertMatchesEncodedLength(index, row, schema, primaryKey);

        row[5] = null;
        AssertMatchesEncodedLength(schema.Indexes[0], row, schema, primaryKey);
        AssertDoesNotContain(
            schema.Indexes[1],
            row,
            schema,
            primaryKey);
    }

    /// <summary>覆盖 JSON path 标量、对象、数组、JSON null 与缺失 path 的索引语义。</summary>
    [Theory]
    [InlineData("{\"value\":42}", true)]
    [InlineData("{\"value\":\"木垒\"}", true)]
    [InlineData("{\"value\":true}", true)]
    [InlineData("{\"value\":{\"nested\":1}}", true)]
    [InlineData("{\"value\":[1,2,3]}", true)]
    [InlineData("{\"value\":null}", false)]
    [InlineData("{\"other\":1}", false)]
    public void TryGetIndexEntryKeyLength_JsonPath_MatchesEncodedKey(string json, bool expectedEntry)
    {
        TableSchema schema = TableSchema.Create(
            "json_lengths",
            [("id", TableColumnType.Int64, false), ("document", TableColumnType.Json, false)],
            ["id"],
            [new TableIndexDefinition("ix_json_value", ["document"], false, JsonPath: "$.value")]);
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, [1L]);

        if (expectedEntry)
            AssertMatchesEncodedLength(schema.Indexes[0], [1L, json], schema, primaryKey);
        else
            AssertDoesNotContain(schema.Indexes[0], [1L, json], schema, primaryKey);
    }

    /// <summary>使用固定随机种子覆盖可变字符串、BLOB、数值尾部和唯一索引 NULL 回退。</summary>
    [Fact]
    public void TryGetIndexEntryKeyLength_DeterministicRandomRows_MatchesEncodedKey()
    {
        TableSchema schema = TableSchema.Create(
            "random_lengths",
            [
                ("id", TableColumnType.Int64, false),
                ("number", TableColumnType.Int64, true),
                ("score", TableColumnType.Float64, true),
                ("name", TableColumnType.String, true),
                ("payload", TableColumnType.Blob, true),
            ],
            ["id"],
            [
                new TableIndexDefinition("ix_random", ["number", "score", "name", "payload"], false),
                new TableIndexDefinition("ux_random_name", ["name"], true),
            ]);
        var random = new Random(0x534E4442);

        for (int iteration = 0; iteration < 256; iteration++)
        {
            long id = random.NextInt64();
            byte[] primaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, [id]);
            var blob = new byte[random.Next(0, 257)];
            random.NextBytes(blob);
            string? name = iteration % 11 == 0
                ? null
                : new string((char)('a' + iteration % 26), random.Next(0, 129)) + "木垒";
            object?[] row =
            [
                id,
                iteration % 7 == 0 ? null : random.NextInt64(),
                iteration % 13 == 0 ? null : BitConverter.Int64BitsToDouble(random.NextInt64()),
                name,
                iteration % 17 == 0 ? null : blob,
            ];

            AssertMatchesEncodedLength(schema.Indexes[0], row, schema, primaryKey);
            if (name is null)
                AssertDoesNotContain(schema.Indexes[1], row, schema, primaryKey);
            else
                AssertMatchesEncodedLength(schema.Indexes[1], row, schema, primaryKey);
        }
    }

    /// <summary>复合主键必须按唯一性规则精确计入 key 后缀和索引 value。</summary>
    [Fact]
    public void TryGetIndexEntryKeyLength_CompositePrimaryKey_MatchesCompleteEntryWidth()
    {
        TableSchema schema = TableSchema.Create(
            "composite_primary",
            [
                ("tenant", TableColumnType.String, false),
                ("id", TableColumnType.Int64, false),
                ("status", TableColumnType.String, false),
                ("external_id", TableColumnType.String, false),
            ],
            ["tenant", "id"],
            [
                new TableIndexDefinition("ix_status", ["status"], false),
                new TableIndexDefinition("ux_external", ["external_id"], true),
            ]);
        object?[] row = ["木垒", long.MaxValue, "ready", "event-0001"];
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, row);

        foreach (TableIndex index in schema.Indexes)
            AssertMatchesEncodedLength(index, row, schema, primaryKey);
    }

    /// <summary>比较无分配长度计算与实际 key 编码长度。</summary>
    private static void AssertMatchesEncodedLength(
        TableIndex index,
        IReadOnlyList<object?> row,
        TableSchema schema,
        ReadOnlySpan<byte> primaryKey)
    {
        byte[]? encoded = TableIndexCodec.TryEncodeIndexEntryKey(index, row, schema, primaryKey);
        Assert.NotNull(encoded);
        Assert.True(TableIndexCodec.TryGetIndexEntryKeyLength(index, row, schema, primaryKey, out int length));
        Assert.Equal(encoded.Length, length);
        byte[] value = TableIndexCodec.EncodeIndexEntryValue(primaryKey);
        Assert.Equal(checked(encoded.Length + value.Length), checked(length + primaryKey.Length));
    }

    /// <summary>确认两条路径都判定当前行不产生索引项。</summary>
    private static void AssertDoesNotContain(
        TableIndex index,
        IReadOnlyList<object?> row,
        TableSchema schema,
        ReadOnlySpan<byte> primaryKey)
    {
        Assert.Null(TableIndexCodec.TryEncodeIndexEntryKey(index, row, schema, primaryKey));
        Assert.False(TableIndexCodec.TryGetIndexEntryKeyLength(index, row, schema, primaryKey, out int length));
        Assert.Equal(0, length);
    }
}
