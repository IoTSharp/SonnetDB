using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

public sealed class TableEncodingCompatibilityTests
{
    [Fact]
    public void EncodePrimaryKeyValues_AllLegacyTypes_MatchesFrozenV1Bytes()
    {
        TableSchema schema = TableSchema.Create(
            "legacy",
            [
                ("id", TableColumnType.Int64, false),
                ("score", TableColumnType.Float64, false),
                ("flag", TableColumnType.Boolean, false),
                ("at", TableColumnType.DateTime, false),
                ("name", TableColumnType.String, false),
                ("data", TableColumnType.Blob, false),
                ("json", TableColumnType.Json, false),
            ],
            ["id", "score", "flag", "at", "name", "data", "json"]);

        byte[] encoded = TableKeyCodec.EncodePrimaryKeyValues(
            schema,
            [-1L, -1.5, true, DateTimeOffset.UnixEpoch, "A", new byte[] { 0, 255 }, "{}"]);

        Assert.Equal(
            "FFFFFFFFFFFFFFFF" +
            "BFF8000000000000" +
            "01" +
            "0000000000000000" +
            "0000000141" +
            "0000000200FF" +
            "000000027B7D",
            Convert.ToHexString(encoded));
    }

    [Fact]
    public void EncodeIndexPrefix_NullAndSignedValues_MatchesFrozenV1Bytes()
    {
        TableSchema schema = TableSchema.Create(
            "legacy_index",
            [
                ("id", TableColumnType.Int64, false),
                ("value", TableColumnType.Int64, true),
                ("name", TableColumnType.String, true),
            ],
            ["id"],
            [new TableIndexDefinition("idx", ["value", "name"], false)]);
        TableIndex index = Assert.Single(schema.Indexes);

        byte[] encoded = TableIndexCodec.EncodeIndexPrefix(index, [1L, -2L, null], schema);

        Assert.Equal(
            "690003696478" +
            "01FFFFFFFFFFFFFFFE" +
            "00",
            Convert.ToHexString(encoded));
    }
}
