using System.Buffers;
using System.Data;
using System.Data.Common;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Protocol;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #198：空结果与全 NULL 列保留声明类型。</summary>
public sealed class SqlSelectColumnMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "sndb-select-metadata-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Select_EmptyAliasAndAllNull_RetainsDeclaredColumnInfo()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE typed_rows (id INT, value INT NULL, amount DECIMAL(20,6), PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO typed_rows (id, value, amount) VALUES (1, NULL, '1.250000')");

        var empty = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id AS key_alias, value AS nullable_alias, amount AS exact_alias "
            + "FROM typed_rows WHERE id = 404 ORDER BY id"));
        Assert.Empty(empty.Rows);
        Assert.Equal(["key_alias", "nullable_alias", "exact_alias"], empty.Columns);
        var info = Assert.IsType<SelectColumnInfo[]>(empty.ColumnInfo);
        Assert.Equal([TableColumnType.Int64, TableColumnType.Int64, TableColumnType.Decimal],
            info.Select(static column => column.DataType).ToArray());
        Assert.Equal([false, true, true],
            info.Select(static column => column.IsNullable).ToArray());
        Assert.True(info[0].IsKey);

        var allNull = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT DISTINCT value AS nullable_alias FROM typed_rows LIMIT 1 OFFSET 10"));
        Assert.Empty(allNull.Rows);
        Assert.Equal(TableColumnType.Int64, Assert.Single(allNull.ColumnInfo!).DataType);
        var oneNull = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT value AS nullable_alias FROM typed_rows"));
        Assert.Null(Assert.Single(oneNull.Rows)[0]);
        Assert.Equal(TableColumnType.Int64, Assert.Single(oneNull.ColumnInfo!).DataType);

        var aggregate = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT COUNT(*) AS total, MIN(value) AS minimum, AVG(amount) AS average "
            + "FROM typed_rows WHERE id = 404"));
        Assert.Equal(new object?[] { 0L, null, null }, Assert.Single(aggregate.Rows));
        Assert.Equal([TableColumnType.Int64, TableColumnType.Int64, TableColumnType.Decimal],
            Assert.IsType<SelectColumnInfo[]>(aggregate.ColumnInfo)
                .Select(static column => column.DataType).ToArray());

        var sum = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT SUM(value) AS total FROM typed_rows"));
        Assert.Null(Assert.Single(sum.Rows)[0]);
        Assert.Equal(TableColumnType.Int64, Assert.Single(sum.ColumnInfo!).DataType);
        var paged = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT COUNT(*) AS total, MIN(value) AS minimum FROM typed_rows LIMIT 1 OFFSET 1"));
        Assert.Empty(paged.Rows);
        Assert.Equal([TableColumnType.Int64, TableColumnType.Int64],
            Assert.IsType<SelectColumnInfo[]>(paged.ColumnInfo)
                .Select(static column => column.DataType).ToArray());

        SqlExecutor.Execute(db, "CREATE TABLE sum_rows (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO sum_rows (id, value) VALUES (1, 9223372036854775807), (2, 1)");
        var promoted = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT SUM(value) AS total FROM sum_rows"));
        Assert.IsType<double>(Assert.Single(promoted.Rows)[0]);
        Assert.Null(Assert.Single(promoted.ColumnInfo!).DataType);
    }

    [Fact]
    public void EmbeddedAdo_EmptyAndAllNull_ExposeInt64SchemaBeforeRead()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE typed_ado (id INT, value INT NULL, PRIMARY KEY (id))";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO typed_ado (id, value) VALUES (1, NULL)";
        command.ExecuteNonQuery();

        command.CommandText = "SELECT value AS nullable_alias FROM typed_ado WHERE id = 404";
        using (var empty = command.ExecuteReader())
        {
            Assert.Equal(typeof(long), empty.GetFieldType(0));
            var schema = Assert.IsType<DataTable>(empty.GetSchemaTable());
            Assert.Equal(typeof(long), schema.Rows[0][SchemaTableColumn.DataType]);
            Assert.True((bool)schema.Rows[0][SchemaTableColumn.AllowDBNull]);
            Assert.False(empty.Read());
        }

        command.CommandText = "SELECT value AS nullable_alias FROM typed_ado";
        using var allNull = command.ExecuteReader();
        Assert.Equal(typeof(long), allNull.GetFieldType(0));
        Assert.True(allNull.Read());
        Assert.Equal(DBNull.Value, allNull.GetValue(0));
        Assert.False(allNull.Read());

        allNull.Close();
        command.CommandText = "SELECT COUNT(*) AS total, MIN(value) AS minimum "
            + "FROM typed_ado WHERE id = 404";
        using var aggregate = command.ExecuteReader();
        Assert.Equal(typeof(long), aggregate.GetFieldType(0));
        Assert.Equal(typeof(long), aggregate.GetFieldType(1));
        Assert.False((bool)Assert.IsType<DataTable>(aggregate.GetSchemaTable())
            .Rows[0][SchemaTableColumn.AllowDBNull]);
        Assert.True(aggregate.Read());
        Assert.Equal(0L, aggregate.GetInt64(0));
        Assert.Equal(DBNull.Value, aggregate.GetValue(1));
    }

    [Fact]
    public void FrameMeta_NewAndLegacyLayouts_DecodeDeclaredInfo()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 1, ["number", "name"],
            [new SelectColumnInfo(TableColumnType.Int64, false, IsKey: true),
             new SelectColumnInfo(TableColumnType.String, true)]);
        ReadOnlySequence<byte> encoded = new(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref encoded, out _, out var payload));
        var (columns, info) = SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload.ToArray());
        Assert.Equal(["number", "name"], columns);
        Assert.Equal(TableColumnType.Int64, info![0].DataType);
        Assert.False(info[0].IsNullable);
        Assert.True(info[0].IsKey);
        Assert.Equal(TableColumnType.String, info[1].DataType);
        Assert.True(info[1].IsNullable);
        Assert.Equal(columns, SqlFrameCodec.DecodeQueryMetaFrame(payload.ToArray()));

        writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 2, ["legacy"]);
        encoded = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref encoded, out _, out payload));
        var legacy = SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload.ToArray());
        Assert.Equal(["legacy"], legacy.Columns);
        Assert.Null(legacy.ColumnInfo);
    }
}
