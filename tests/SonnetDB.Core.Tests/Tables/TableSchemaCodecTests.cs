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
                ("version", TableColumnType.Int64, false),
            ],
            ["id"],
            indexes:
            [
                new TableIndexDefinition("idx_devices_name", ["name"], IsUnique: false, CreatedAtUtcTicks: 5678),
                new TableIndexDefinition("ux_devices_metadata", ["metadata"], IsUnique: true, CreatedAtUtcTicks: 6789),
            ],
            foreignKeys:
            [
                new TableForeignKeyDefinition("fk_devices_sites", ["name"], "sites", ["id"]),
            ],
            rowVersionColumns: new HashSet<string>(["version"], StringComparer.Ordinal),
            createdAtUtcTicks: 1234);

        string path = Path.Combine(_root, TableSchemaCodec.FileName);
        TableSchemaCodec.Save(path, [schema]);

        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        Assert.Equal("devices", loaded.Name);
        Assert.Equal(1234, loaded.CreatedAtUtcTicks);
        Assert.Equal(["id"], loaded.PrimaryKey);
        Assert.Equal(4, loaded.Columns.Count);
        Assert.True(loaded.Columns[0].IsPrimaryKey);
        Assert.False(loaded.Columns[0].IsNullable);
        Assert.True(loaded.Columns[2].IsNullable);
        Assert.Equal(TableColumnType.Json, loaded.Columns[2].DataType);
        Assert.Equal(2, loaded.Indexes.Count);
        Assert.Equal("idx_devices_name", loaded.Indexes[0].Name);
        Assert.False(loaded.Indexes[0].IsUnique);
        Assert.Equal(["name"], loaded.Indexes[0].Columns);
        Assert.Equal(5678, loaded.Indexes[0].CreatedAtUtcTicks);
        Assert.Equal("ux_devices_metadata", loaded.Indexes[1].Name);
        Assert.True(loaded.Indexes[1].IsUnique);
        Assert.True(loaded.Columns[3].IsRowVersion);
        var foreignKey = Assert.Single(loaded.ForeignKeys);
        Assert.Equal("fk_devices_sites", foreignKey.Name);
        Assert.Equal(["name"], foreignKey.Columns);
        Assert.Equal("sites", foreignKey.PrincipalTable);
        Assert.Equal(["id"], foreignKey.PrincipalColumns);
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
