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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryView_EmptyAndAllNull_ExposeCompleteDeclarations(bool insertRow)
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE ViewItems (DeviceID INT, Name STRING, Amount DECIMAL(20,6), Payload BLOB, PRIMARY KEY(DeviceID))";
        command.ExecuteNonQuery();
        if (insertRow)
        {
            command.CommandText = "INSERT INTO ViewItems (DeviceID, Name, Amount, Payload) VALUES (1, NULL, NULL, NULL)";
            command.ExecuteNonQuery();
        }
        command.CommandText = "CREATE VIEW ItemView AS SELECT DeviceID AS ItemID, Name, Amount, Payload FROM ViewItems";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE VIEW NestedView AS SELECT * FROM ItemView";
        command.ExecuteNonQuery();

        foreach (string suffix in new[] { " LIMIT 0", " WHERE ItemID < 0", string.Empty })
        {
            command.CommandText = "SELECT * FROM NestedView" + suffix;
            using var reader = command.ExecuteReader();
            Assert.Equal(["ItemID", "Name", "Amount", "Payload"],
                Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray());
            AssertViewDeclarations(reader);
            if (insertRow && suffix.Length == 0)
            {
                Assert.True(reader.Read());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.True(reader.IsDBNull(1));
                Assert.True(reader.IsDBNull(2));
                Assert.True(reader.IsDBNull(3));
            }
            Assert.False(reader.Read());
            AssertViewDeclarations(reader);
        }
        Assert.Empty(connection.GetSchema("Columns", [null, null, "ItemView", null]).Rows.Cast<DataRow>());
    }

    private static void AssertViewDeclarations(DbDataReader reader)
    {
        Assert.Equal([typeof(long), typeof(string), typeof(decimal), typeof(byte[])],
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType).ToArray());
        Assert.Equal(["Int64", "String", "Decimal", "Blob"],
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetDataTypeName).ToArray());
        var schema = reader.GetColumnSchema();
        Assert.Equal(new bool?[] { false, true, true, true }, schema.Select(column => column.AllowDBNull).ToArray());
        Assert.True(schema[0].IsKey);
        Assert.Equal(20, schema[2].NumericPrecision);
        Assert.Equal(6, schema[2].NumericScale);
        Assert.Null(schema[1].ColumnSize);
        Assert.Null(schema[3].ColumnSize);
        Assert.Equal(19, schema[0].NumericPrecision);
        Assert.Equal(0, schema[0].NumericScale);
        Assert.Equal("Decimal", schema[2].DataTypeName);
        var table = reader.GetSchemaTable()!;
        Assert.Equal((short)20, table.Rows[2][SchemaTableColumn.NumericPrecision]);
        Assert.Equal((short)6, table.Rows[2][SchemaTableColumn.NumericScale]);
        Assert.Equal(-1, table.Rows[1][SchemaTableColumn.ColumnSize]);
    }

    [Fact]
    public void ViewExpressions_EmptyReader_PreservesTypesNullabilityAndUnknownDeclarations()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE ExprItems (id INT, value INT NULL, amount DECIMAL(18,4), PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "CREATE VIEW ExprView AS SELECT id + 1 AS shifted, id / 2 AS divided, "
            + "value > 0 AS positive, CASE WHEN id > 0 THEN value ELSE 0 END AS chosen, "
            + "COALESCE(value,0) AS filled, CAST(value AS FLOAT) AS floated, amount, "
            + "CASE WHEN id > 0 THEN id ELSE CAST(id AS FLOAT) END AS mixed FROM ExprItems");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "SELECT * FROM ExprView LIMIT 0"));
        Assert.Empty(result.Rows);
        var columns = result.ColumnInfo!;
        Assert.Equal(new TableColumnType?[] { TableColumnType.Int64, TableColumnType.Float64, TableColumnType.Boolean,
            TableColumnType.Int64, TableColumnType.Int64, TableColumnType.Float64, TableColumnType.Decimal, null },
            columns.Select(column => column.DataType).ToArray());
        Assert.Equal(new bool?[] { false, false, true, true, false, true, true, false },
            columns.Select(column => column.IsNullable).ToArray());
        Assert.Equal((byte)18, columns[6].NumericPrecision);
        Assert.Equal((byte)4, columns[6].NumericScale);
        Assert.Null(columns[7].NumericPrecision);
    }

    [Theory]
    [InlineData("LEFT", false, true)]
    [InlineData("RIGHT", true, false)]
    [InlineData("FULL", true, true)]
    public void ViewOuterJoin_EmptyReader_WidensNullableSide(string join, bool leftNullable, bool rightNullable)
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE left_items (id INT, PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "CREATE TABLE right_items (id INT, PRIMARY KEY(id))");
        SqlExecutor.Execute(database, $"CREATE VIEW JoinedView AS SELECT l.id AS LeftID, r.id AS RightID "
            + $"FROM left_items l {join} JOIN right_items r ON l.id = r.id");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "SELECT * FROM JoinedView LIMIT 0"));
        Assert.Equal(new bool?[] { leftNullable, rightNullable }, result.ColumnInfo!.Select(column => column.IsNullable).ToArray());
        Assert.All(result.ColumnInfo!, column => Assert.Equal(TableColumnType.Int64, column.DataType));
    }

    [Fact]
    public void ViewAggregates_EmptyReader_KeepsCountAndNullableAggregateContracts()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE aggregate_items (id INT, amount DECIMAL(18,4), PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "CREATE VIEW AggregateView AS SELECT COUNT(*) AS total, SUM(id) AS summed, "
            + "AVG(amount) AS average, MIN(amount) AS minimum FROM aggregate_items");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "SELECT * FROM AggregateView LIMIT 0"));
        Assert.Equal(new bool?[] { false, true, true, true }, result.ColumnInfo!.Select(column => column.IsNullable).ToArray());
        Assert.Equal(new TableColumnType?[] { TableColumnType.Int64, TableColumnType.Int64, TableColumnType.Decimal, TableColumnType.Decimal },
            result.ColumnInfo!.Select(column => column.DataType).ToArray());
        Assert.Null(result.ColumnInfo![2].NumericPrecision);
        Assert.Equal((byte)18, result.ColumnInfo![3].NumericPrecision);
        Assert.Equal((byte)4, result.ColumnInfo![3].NumericScale);
    }

    [Fact]
    public void ViewOuterJoin_UnknownCaseAndCoalesce_DoNotTreatNullableUnknownAsNullLiteral()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using var command = connection.CreateCommand();
        foreach (string sql in new[]
        {
            "CREATE TABLE BranchKeys (id INT, PRIMARY KEY(id))",
            "INSERT INTO BranchKeys (id) VALUES (1), (2)",
            "CREATE VIEW UnknownSource AS SELECT id, concat('id=', CAST(id AS STRING)) AS Rendered FROM BranchKeys WHERE id = 1",
            "CREATE VIEW UnknownBranches AS SELECT l.id, CASE WHEN l.id = 1 THEN r.Rendered ELSE 0 END AS chosen, "
                + "COALESCE(r.Rendered, 0) AS filled, CASE WHEN l.id = 1 THEN l.id ELSE NULL END AS genuine_null, "
                + "CASE WHEN l.id = 1 THEN l.id END AS implicit_null, COALESCE(NULL, l.id) AS null_first, "
                + "CASE WHEN l.id = 1 THEN l.id ELSE CAST(NULL AS INT) END AS typed_null "
                + "FROM BranchKeys l LEFT JOIN UnknownSource r ON l.id = r.id",
        })
        {
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        foreach (string suffix in new[] { " LIMIT 0", " ORDER BY id" })
        {
            command.CommandText = "SELECT * FROM UnknownBranches" + suffix;
            using var reader = command.ExecuteReader();
            AssertUnknownBranchDeclarations(reader);
            if (suffix == " ORDER BY id")
            {
                Assert.True(reader.Read());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal("id=1", reader.GetString(1));
                Assert.Equal("id=1", reader.GetString(2));
                Assert.Equal(1L, reader.GetInt64(3));
                Assert.Equal(1L, reader.GetInt64(4));
                Assert.Equal(1L, reader.GetInt64(5));
                Assert.Equal(1L, reader.GetInt64(6));
                AssertUnknownBranchDeclarations(reader);
                Assert.True(reader.Read());
                Assert.Equal(2L, reader.GetInt64(0));
                Assert.Equal(0L, reader.GetInt64(1));
                Assert.Equal(0L, reader.GetInt64(2));
                Assert.True(reader.IsDBNull(3));
                Assert.True(reader.IsDBNull(4));
                Assert.Equal(2L, reader.GetInt64(5));
                Assert.True(reader.IsDBNull(6));
                AssertUnknownBranchDeclarations(reader);
            }
            Assert.False(reader.Read());
            AssertUnknownBranchDeclarations(reader);
        }
    }

    private static void AssertUnknownBranchDeclarations(DbDataReader reader)
    {
        Assert.Equal(new[] { typeof(long), typeof(object), typeof(object), typeof(long), typeof(long), typeof(long), typeof(long) },
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType).ToArray());
        Assert.Equal(new bool?[] { false, true, false, true, true, false, true },
            reader.GetColumnSchema().Select(column => column.AllowDBNull).ToArray());
        Assert.Equal(new int?[] { 19, null, null, 19, 19, 19, 19 },
            reader.GetColumnSchema().Select(column => column.NumericPrecision).ToArray());
    }

    [Fact]
    public void ViewSchemaOnly_NestedUdf_DoesNotExecuteOrSampleValues()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        int calls = 0;
        database.Functions.RegisterScalar("schema_probe", _ => { calls++; return 7L; }, 1, 1);
        SqlExecutor.Execute(database, "CREATE TABLE probe_items (id INT, PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "INSERT INTO probe_items (id) VALUES (1)");
        SqlExecutor.Execute(database, "CREATE VIEW ProbeView AS SELECT id, schema_probe(id) AS probed FROM probe_items "
            + "WHERE schema_probe(id) > 0 ORDER BY schema_probe(id)");
        SqlExecutor.Execute(database, "CREATE VIEW NestedProbe AS SELECT * FROM ProbeView");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "SELECT * FROM NestedProbe LIMIT 0"));
        Assert.Empty(result.Rows);
        Assert.Equal(0, calls);
        Assert.Equal(TableColumnType.Int64, result.ColumnInfo![0].DataType);
        Assert.Null(result.ColumnInfo![1].DataType);
        var populated = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT id, schema_probe(id) AS probed FROM probe_items"));
        Assert.Equal(7L, Assert.Single(populated.Rows)[1]);
        Assert.Null(populated.ColumnInfo![1].DataType);
        Assert.True(calls > 0);
    }

    /// <summary>派生列改名与 LIMIT 0 保留精度和可空性，SELECT/VALUES 均不消费来源。</summary>
    [Fact]
    public void DerivedAliases_LimitZero_PreservesDeclarationsWithoutConsumingSelectOrValues()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE central_metadata(id INT, amount DECIMAL(18,4), PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "INSERT INTO central_metadata(id,amount) VALUES(1,'1.2500')");
        int calls = 0;
        database.Functions.RegisterScalar("central_schema_probe", arguments => { calls++; return arguments[0]; }, 1, 1);
        database.Functions.RegisterScalar("central_values_probe", _ => { calls++; return "different-type"; }, 1, 1);
        const string sql = """
            SELECT d."Shown.Amount" AS AmountOut, d."Shown.ID" AS KeyOut,
                   d."Shown.Probe" AS ProbeOut, v."Value.ID" AS ValuesOut,
                   -l.id AS Negated, l.id + 1 AS Incremented,
                   CASE WHEN l.id=1 THEN l.amount ELSE l.amount END AS Chosen,
                   COALESCE(l.amount,l.amount) AS Filled,
                   shift_left(l.id,1) AS Shifted, lpad('7',3,'0') AS Padded
            FROM (SELECT id,amount FROM central_metadata WHERE central_schema_probe(id)>0) AS l
            JOIN (SELECT id,amount,central_schema_probe(id) AS probed FROM central_metadata
                  WHERE central_schema_probe(id)>0) AS d("Shown.ID","Shown.Amount","Shown.Probe")
                ON l.id=d."Shown.ID"
            JOIN (VALUES(1),(central_values_probe(1))) AS v("Value.ID") ON d."Shown.ID"=v."Value.ID"
            LIMIT 0
            """;
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));
        Assert.Empty(result.Rows);
        Assert.Equal(0, calls);
        Assert.Equal(new[] { "AmountOut", "KeyOut", "ProbeOut", "ValuesOut", "Negated", "Incremented", "Chosen", "Filled", "Shifted", "Padded" }, result.Columns);
        var info = result.ColumnInfo!;
        Assert.Equal(new TableColumnType?[] { TableColumnType.Decimal, TableColumnType.Int64, null, null,
            TableColumnType.Int64, TableColumnType.Int64, TableColumnType.Decimal, TableColumnType.Decimal,
            TableColumnType.Int64, TableColumnType.String }, info.Select(column => column.DataType).ToArray());
        Assert.True(info[0].IsNullable);
        Assert.False(info[1].IsNullable);
        Assert.False(info[4].IsNullable);
        Assert.False(info[5].IsNullable);
        foreach (int index in new[] { 0, 6, 7 })
        {
            Assert.Equal((byte)18, info[index].NumericPrecision);
            Assert.Equal((byte)4, info[index].NumericScale);
        }
        var populated = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            sql.Replace("LIMIT 0", string.Empty, StringComparison.Ordinal)));
        Assert.Single(populated.Rows);
        Assert.Equal(1.25m, populated.Rows[0][0]);
        Assert.True(calls > 0);
    }
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
        SqlExecutor.Execute(db, "INSERT INTO sum_rows (id, value) VALUES (1, 9223372036854775807)");
        var boundary = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT SUM(value) AS total FROM sum_rows"));
        Assert.Equal(long.MaxValue, Assert.IsType<long>(Assert.Single(boundary.Rows)[0]));
        Assert.Equal(TableColumnType.Int64, Assert.Single(boundary.ColumnInfo!).DataType);
        SqlExecutor.Execute(db, "INSERT INTO sum_rows (id, value) VALUES (2, 1)");
        var overflow = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "SELECT SUM(value) AS total FROM sum_rows"));
        Assert.Contains("SUM(INT) 的 Int64 累加发生溢出", overflow.Message, StringComparison.Ordinal);
        var exactDecimal = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT SUM(CAST(value AS DECIMAL)) AS total FROM sum_rows"));
        Assert.Equal(9223372036854775808m, Assert.IsType<decimal>(Assert.Single(exactDecimal.Rows)[0]));
        Assert.Equal(TableColumnType.Decimal, Assert.Single(exactDecimal.ColumnInfo!).DataType);
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
