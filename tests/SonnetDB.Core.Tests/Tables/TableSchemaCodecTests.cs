using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Sql.Ast;
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
            createdAtUtcTicks: 1234,
            checkConstraints:
            [
                new TableCheckConstraintDefinition("ck_devices_name", "name IN ('pump', 'fan')"),
            ]);

        string path = Path.Combine(_root, TableSchemaCodec.FileName);
        TableSchemaCodec.Save(path, [schema]);

        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        Assert.Equal("devices", loaded.Name);
        Assert.Equal(1234, loaded.CreatedAtUtcTicks);
        Assert.Equal(["id"], loaded.PrimaryKey);
        var checkConstraint = Assert.Single(loaded.CheckConstraints);
        Assert.Equal("ck_devices_name", checkConstraint.Name);
        Assert.Equal("(\"name\" IN ('pump', 'fan'))", checkConstraint.ExpressionSql);
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

    [Fact]
    public void SaveLoad_WithColumnDefaults_RoundTripsDefaultExpressions()
    {
        var schema = TableSchema.CreateWithDefaults(
            "devices",
            [
                ("id", TableColumnType.Int64, false),
                ("site", TableColumnType.String, false),
                ("retries", TableColumnType.Int64, true),
            ],
            ["id"],
            indexes: null,
            foreignKeys: null,
            rowVersionColumns: null,
            createdAtUtcTicks: 1234,
            checkConstraints: null,
            columnDefaults: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["site"] = "'north'",
                ["retries"] = "3",
            });
        string path = Path.Combine(_root, TableSchemaCodec.FileName);

        TableSchemaCodec.Save(path, [schema]);

        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        Assert.Null(loaded.TryGetColumn("id")!.DefaultExpressionSql);
        Assert.Equal("'north'", loaded.TryGetColumn("site")!.DefaultExpressionSql);
        Assert.Equal("3", loaded.TryGetColumn("retries")!.DefaultExpressionSql);
    }

    [Fact]
    public void SaveLoad_WithAutoIncrementColumn_RoundTripsAutoIncrementFlag()
    {
        var schema = TableSchema.CreateWithDefaults(
            "events",
            [
                ("id", TableColumnType.Int64, false),
                ("name", TableColumnType.String, false),
            ],
            ["id"],
            indexes: null,
            foreignKeys: null,
            rowVersionColumns: null,
            createdAtUtcTicks: 1234,
            checkConstraints: null,
            columnDefaults: null,
            autoIncrementColumns: new HashSet<string>(["id"], StringComparer.Ordinal));
        string path = Path.Combine(_root, TableSchemaCodec.FileName);

        TableSchemaCodec.Save(path, [schema]);

        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        Assert.True(loaded.TryGetColumn("id")!.IsAutoIncrement);
        Assert.Equal("id", loaded.AutoIncrementColumn!.Name);
        Assert.False(loaded.TryGetColumn("name")!.IsAutoIncrement);
    }

    [Fact]
    public void CreateWithDefaults_WithNullableAutoIncrementColumn_ForcesNotNull()
    {
        var schema = TableSchema.CreateWithDefaults(
            "events",
            [("id", TableColumnType.Int64, true)],
            ["id"],
            indexes: null,
            foreignKeys: null,
            rowVersionColumns: null,
            createdAtUtcTicks: 1234,
            checkConstraints: null,
            columnDefaults: null,
            autoIncrementColumns: new HashSet<string>(["id"], StringComparer.Ordinal));

        var column = Assert.Single(schema.Columns);
        Assert.True(column.IsAutoIncrement);
        Assert.False(column.IsNullable);
    }

    [Fact]
    public void CreateWithDefaults_WithRowVersionDefault_ThrowsArgumentException()
    {
        var error = Assert.Throws<ArgumentException>(() => TableSchema.CreateWithDefaults(
            "devices",
            [
                ("id", TableColumnType.Int64, false),
                ("version", TableColumnType.Int64, false),
            ],
            ["id"],
            indexes: null,
            foreignKeys: null,
            rowVersionColumns: new HashSet<string>(["version"], StringComparer.Ordinal),
            createdAtUtcTicks: 1234,
            checkConstraints: null,
            columnDefaults: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["version"] = "1",
            }));

        Assert.Contains("ROWVERSION", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WithVersion1Schema_ReadsColumnsWithoutLaterSections()
    {
        string path = Path.Combine(_root, TableSchemaCodec.FileName);
        WriteLegacySchema(
            path,
            formatVersion: 1,
            columns:
            [
                new LegacyColumn("id", TableColumnType.Int64, IsPrimaryKey: true, IsNullable: false),
                new LegacyColumn("name", TableColumnType.String, IsPrimaryKey: false, IsNullable: true),
            ]);

        var loaded = Assert.Single(TableSchemaCodec.Load(path));

        Assert.Equal("legacy", loaded.Name);
        Assert.Equal(1234, loaded.CreatedAtUtcTicks);
        Assert.Equal(["id"], loaded.PrimaryKey);
        Assert.Equal(2, loaded.Columns.Count);
        Assert.True(loaded.TryGetColumn("name")!.IsNullable);
        Assert.Empty(loaded.Indexes);
        Assert.Empty(loaded.ForeignKeys);
        Assert.Empty(loaded.CheckConstraints);
        Assert.All(loaded.Columns, static column =>
        {
            Assert.False(column.IsRowVersion);
            Assert.Null(column.DefaultExpressionSql);
        });
    }

    [Fact]
    public void Load_WithVersion2Schema_ReadsIndexWithoutJsonPath()
    {
        string path = Path.Combine(_root, TableSchemaCodec.FileName);
        WriteLegacySchema(
            path,
            formatVersion: 2,
            columns:
            [
                new LegacyColumn("id", TableColumnType.Int64, IsPrimaryKey: true, IsNullable: false),
                new LegacyColumn("name", TableColumnType.String, IsPrimaryKey: false, IsNullable: false),
            ],
            index: new TableIndexDefinition(
                "ux_legacy_name",
                ["name"],
                IsUnique: true,
                CreatedAtUtcTicks: 5678));

        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        var index = Assert.Single(loaded.Indexes);

        Assert.Equal("ux_legacy_name", index.Name);
        Assert.True(index.IsUnique);
        Assert.Equal(["name"], index.Columns);
        Assert.Equal(5678, index.CreatedAtUtcTicks);
        Assert.Null(index.JsonPath);
        Assert.Empty(loaded.ForeignKeys);
    }

    [Fact]
    public void Load_WithVersion3Schema_ReadsJsonPathIndex()
    {
        string path = Path.Combine(_root, TableSchemaCodec.FileName);
        WriteLegacySchema(
            path,
            formatVersion: 3,
            columns:
            [
                new LegacyColumn("id", TableColumnType.Int64, IsPrimaryKey: true, IsNullable: false),
                new LegacyColumn("metadata", TableColumnType.Json, IsPrimaryKey: false, IsNullable: true),
            ],
            index: new TableIndexDefinition(
                "idx_legacy_site",
                ["metadata"],
                IsUnique: false,
                CreatedAtUtcTicks: 6789,
                JsonPath: "$.site"));

        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        var index = Assert.Single(loaded.Indexes);

        Assert.Equal("idx_legacy_site", index.Name);
        Assert.Equal("$.site", index.JsonPath);
        Assert.Equal(["metadata"], index.Columns);
        Assert.Empty(loaded.ForeignKeys);
    }

    [Fact]
    public void Load_WithVersion4Schema_ReadsRowVersionAndForeignKeyWithoutDeleteAction()
    {
        string path = Path.Combine(_root, TableSchemaCodec.FileName);
        WriteLegacySchema(
            path,
            formatVersion: 4,
            columns:
            [
                new LegacyColumn("id", TableColumnType.Int64, IsPrimaryKey: true, IsNullable: false),
                new LegacyColumn("parent_id", TableColumnType.Int64, IsPrimaryKey: false, IsNullable: true),
                new LegacyColumn(
                    "version",
                    TableColumnType.Int64,
                    IsPrimaryKey: false,
                    IsNullable: false,
                    IsRowVersion: true),
            ],
            foreignKey: new TableForeignKeyDefinition(
                "fk_legacy_parent",
                ["parent_id"],
                "parents",
                ["id"]));

        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        var foreignKey = Assert.Single(loaded.ForeignKeys);

        Assert.True(loaded.TryGetColumn("version")!.IsRowVersion);
        Assert.Equal("fk_legacy_parent", foreignKey.Name);
        Assert.Equal(["parent_id"], foreignKey.Columns);
        Assert.Equal("parents", foreignKey.PrincipalTable);
        Assert.Equal(["id"], foreignKey.PrincipalColumns);
        Assert.Equal(ForeignKeyAction.NoAction, foreignKey.OnDelete);
        Assert.Empty(loaded.CheckConstraints);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Load_WithPreVersion7Schema_RemainsBackwardCompatible(int formatVersion)
    {
        var schema = TableSchema.Create(
            "legacy",
            [("id", TableColumnType.Int64, false)],
            ["id"],
            createdAtUtcTicks: 1234);
        string path = Path.Combine(_root, TableSchemaCodec.FileName);
        TableSchemaCodec.Save(path, [schema]);

        DowngradeSingleColumnSchema(path, formatVersion);

        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        Assert.Equal("legacy", loaded.Name);
        Assert.Empty(loaded.CheckConstraints);
        Assert.Null(loaded.Columns[0].DefaultExpressionSql);
    }

    [Fact]
    public void Load_WithVersion7Schema_TreatsColumnsAsNonAutoIncrement()
    {
        var schema = TableSchema.Create(
            "legacy",
            [("id", TableColumnType.Int64, false)],
            ["id"],
            createdAtUtcTicks: 1234);
        string path = Path.Combine(_root, TableSchemaCodec.FileName);
        TableSchemaCodec.Save(path, [schema]);

        DowngradeSingleColumnSchema(path, formatVersion: 7);

        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        Assert.Equal("legacy", loaded.Name);
        Assert.False(loaded.Columns[0].IsAutoIncrement);
        Assert.Null(loaded.AutoIncrementColumn);
    }

    private static void DowngradeSingleColumnSchema(string path, int formatVersion)
    {
        const int headerSize = 32;
        const int footerSize = 16;
        byte[] content = File.ReadAllBytes(path);

        if (formatVersion < 7)
        {
            int tableNameLength = BinaryPrimitives.ReadUInt16LittleEndian(
                content.AsSpan(headerSize, sizeof(ushort)));
            int columnOffset = headerSize + sizeof(ushort) + tableNameLength + sizeof(long) + sizeof(ushort);
            int columnNameLength = BinaryPrimitives.ReadUInt16LittleEndian(
                content.AsSpan(columnOffset, sizeof(ushort)));
            int defaultOffset = columnOffset + sizeof(ushort) + columnNameLength + 2;
            int defaultLength = BinaryPrimitives.ReadUInt16LittleEndian(
                content.AsSpan(defaultOffset, sizeof(ushort)));
            content = RemoveRange(content, defaultOffset, sizeof(ushort) + defaultLength);
        }

        if (formatVersion < 6)
        {
            int checkConstraintCountOffset = content.Length - footerSize - sizeof(ushort);
            content = RemoveRange(content, checkConstraintCountOffset, sizeof(ushort));
        }

        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(8, sizeof(int)), formatVersion);
        int footerOffset = content.Length - footerSize;
        var crc = new Crc32();
        crc.Append(content.AsSpan(headerSize, footerOffset - headerSize));
        BinaryPrimitives.WriteUInt32LittleEndian(
            content.AsSpan(footerOffset, sizeof(uint)),
            crc.GetCurrentHashAsUInt32());
        File.WriteAllBytes(path, content);
    }

    private static byte[] RemoveRange(byte[] source, int offset, int count)
    {
        var result = new byte[source.Length - count];
        source.AsSpan(0, offset).CopyTo(result);
        source.AsSpan(offset + count).CopyTo(result.AsSpan(offset));
        return result;
    }

    private static void WriteLegacySchema(
        string path,
        int formatVersion,
        IReadOnlyList<LegacyColumn> columns,
        TableIndexDefinition? index = null,
        TableForeignKeyDefinition? foreignKey = null)
    {
        if (formatVersion is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        if (formatVersion < 2 && index is not null)
            throw new ArgumentException("Version 1 schema cannot contain indexes.", nameof(index));
        if (formatVersion < 4 && foreignKey is not null)
            throw new ArgumentException("Versions before 4 cannot contain foreign keys.", nameof(foreignKey));

        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            WriteLegacyString(writer, "legacy");
            writer.Write(1234L);
            writer.Write(checked((ushort)columns.Count));
            foreach (var column in columns)
            {
                WriteLegacyString(writer, column.Name);
                writer.Write((byte)column.DataType);
                byte flags = 0;
                if (column.IsPrimaryKey)
                    flags |= 0b0000_0001;
                if (column.IsNullable)
                    flags |= 0b0000_0010;
                if (formatVersion >= 4 && column.IsRowVersion)
                    flags |= 0b0000_0100;
                writer.Write(flags);
            }

            if (formatVersion >= 2)
            {
                writer.Write(index is null ? (ushort)0 : (ushort)1);
                if (index is not null)
                {
                    WriteLegacyString(writer, index.Name);
                    writer.Write(index.IsUnique ? (byte)1 : (byte)0);
                    writer.Write(index.CreatedAtUtcTicks);
                    writer.Write(checked((ushort)index.Columns.Count));
                    foreach (var columnName in index.Columns)
                        WriteLegacyString(writer, columnName);
                    if (formatVersion >= 3)
                        WriteLegacyString(writer, index.JsonPath ?? string.Empty);
                }
            }

            if (formatVersion >= 4)
            {
                writer.Write(foreignKey is null ? (ushort)0 : (ushort)1);
                if (foreignKey is not null)
                {
                    WriteLegacyString(writer, foreignKey.Name);
                    writer.Write(checked((ushort)foreignKey.Columns.Count));
                    foreach (var columnName in foreignKey.Columns)
                        WriteLegacyString(writer, columnName);
                    WriteLegacyString(writer, foreignKey.PrincipalTable);
                    writer.Write(checked((ushort)foreignKey.PrincipalColumns.Count));
                    foreach (var columnName in foreignKey.PrincipalColumns)
                        WriteLegacyString(writer, columnName);
                }
            }
        }

        const int headerSize = 32;
        const int footerSize = 16;
        ReadOnlySpan<byte> magic = "SDBTBLv1"u8;
        byte[] payloadBytes = payload.ToArray();
        var content = new byte[headerSize + payloadBytes.Length + footerSize];
        magic.CopyTo(content);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(8, sizeof(int)), formatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(12, sizeof(int)), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(16, sizeof(int)), 1);
        payloadBytes.CopyTo(content, headerSize);

        int footerOffset = headerSize + payloadBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(
            content.AsSpan(footerOffset, sizeof(uint)),
            Crc32.HashToUInt32(payloadBytes));
        magic.CopyTo(content.AsSpan(footerOffset + sizeof(uint)));
        File.WriteAllBytes(path, content);
    }

    private static void WriteLegacyString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }

    private sealed record LegacyColumn(
        string Name,
        TableColumnType DataType,
        bool IsPrimaryKey,
        bool IsNullable,
        bool IsRowVersion = false);
}
