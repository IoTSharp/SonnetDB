using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

public sealed class TableSchemaCodecTests : IDisposable
{
    private readonly string _root;

    public TableSchemaCodecTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-table-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void SaveLoad_WithTableSchema_RoundTripsColumnsAndPrimaryKey()
    {
        var schema = TableSchema.Create(
            "devices",
            [
                ("id", TableColumnType.Int64, false),
                ("name", TableColumnType.String, false),
                ("metadata", TableColumnType.Json, true),
            ],
            ["id"],
            createdAtUtcTicks: 1234);

        string path = Path.Combine(_root, TableSchemaCodec.FileName);
        TableSchemaCodec.Save(path, [schema]);

        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        Assert.Equal("devices", loaded.Name);
        Assert.Equal(1234, loaded.CreatedAtUtcTicks);
        Assert.Equal(["id"], loaded.PrimaryKey);
        Assert.Equal(3, loaded.Columns.Count);
        Assert.True(loaded.Columns[0].IsPrimaryKey);
        Assert.False(loaded.Columns[0].IsNullable);
        Assert.True(loaded.Columns[2].IsNullable);
        Assert.Equal(TableColumnType.Json, loaded.Columns[2].DataType);
    }

    [Fact]
    public void Create_WithPrimaryKeyColumn_ForcesNotNull()
    {
        var schema = TableSchema.Create(
            "kv",
            [
                ("key", TableColumnType.String, true),
                ("value", TableColumnType.String, true),
            ],
            ["key"]);

        Assert.False(schema.Columns[0].IsNullable);
        Assert.True(schema.Columns[1].IsNullable);
    }
}
