using System.Data;
using System.Data.Common;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #261：内置 CONCAT 的视图列声明独立于数据行，并尊重同名 UDF。</summary>
public sealed class SqlConcatViewDeclarationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-concat-" + Guid.NewGuid().ToString("N"));

    /// <summary>空、NULL 和非空源在普通、嵌套及物化视图重开后仍提供准确声明。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ConcatViews_EmptyNullAndText_PreserveDeclarationsAfterReopen(int rowMode)
    {
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            SqlExecutor.Execute(db, "CREATE TABLE Items(id INT, name STRING, PRIMARY KEY(id))");
            if (rowMode > 0)
                SqlExecutor.Execute(db, rowMode == 1
                    ? "INSERT INTO Items(id,name) VALUES(1,NULL)"
                    : "INSERT INTO Items(id,name) VALUES(1,'文字')");
            SqlExecutor.Execute(db, "CREATE VIEW ConcatView AS SELECT CONCAT(name,NULL) AS Joined, "
                + "CONCAT(NULL,NULL) AS AllNull, CONCAT('id=',CAST(id AS STRING)) AS Identifier, "
                + "CONCAT(CAST(CONCAT(name,'!') AS STRING),'!') AS Nested FROM Items");
            SqlExecutor.Execute(db, "CREATE VIEW NestedView AS SELECT * FROM ConcatView");
            SqlExecutor.Execute(db, "CREATE MATERIALIZED VIEW SnapshotView AS SELECT * FROM NestedView");
            SqlExecutor.Execute(db, "REFRESH MATERIALIZED VIEW SnapshotView");
            foreach (string view in new[] { "ConcatView", "NestedView", "SnapshotView" })
            {
                var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT * FROM " + view + " LIMIT 0"));
                Assert.Empty(result.Rows);
                Assert.All(result.ColumnInfo!, column =>
                {
                    Assert.Equal(TableColumnType.String, column.DataType);
                    Assert.False(column.IsNullable);
                    Assert.Null(column.ColumnSize);
                    Assert.Null(column.NumericPrecision);
                    Assert.Null(column.NumericScale);
                    Assert.False(column.IsKey);
                });
            }
        }
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        foreach (string view in new[] { "ConcatView", "NestedView", "SnapshotView" })
        {
            foreach (string suffix in new[] { " LIMIT 0", " WHERE 1=0", "" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM " + view + suffix;
                using var reader = command.ExecuteReader();
                AssertDeclarations(reader);
                if (suffix.Length == 0 && rowMode > 0)
                {
                    Assert.True(reader.Read());
                    Assert.Equal(rowMode == 1 ? "" : "文字", reader.GetString(0));
                    Assert.Equal("", reader.GetString(1));
                    Assert.Equal("id=1", reader.GetString(2));
                    Assert.Equal(rowMode == 1 ? "!!" : "文字!!", reader.GetString(3));
                    AssertDeclarations(reader);
                }
                Assert.False(reader.Read());
                AssertDeclarations(reader);
            }
        }
    }

    /// <summary>声明推断不调用 CONCAT 的参数 UDF，且同名 UDF 不能冒用内置字符串声明。</summary>
    [Fact]
    public void ConcatViews_UserFunctions_DoNotSampleOrAssumeBuiltInReturnType()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE Items(id INT, PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "INSERT INTO Items(id) VALUES(1)");
        int calls = 0;
        db.Functions.RegisterScalar("probe", _ => { calls++; return 7L; }, 1, 1);
        SqlExecutor.Execute(db, "CREATE VIEW ProbeView AS SELECT concat(probe(id)) AS Value FROM Items");
        var empty = Select(db, "SELECT * FROM ProbeView LIMIT 0");
        Assert.Equal(TableColumnType.String, Assert.Single(empty.ColumnInfo!).DataType);
        Assert.False(empty.ColumnInfo![0].IsNullable);
        Assert.Equal(0, calls);
        Assert.Equal("7", Assert.Single(Select(db, "SELECT * FROM ProbeView").Rows)[0]);
        Assert.Equal(1, calls);

        int overrideCalls = 0;
        db.Functions.RegisterScalar("CoNcAt", _ => { overrideCalls++; return 42L; }, 1, 1);
        empty = Select(db, "SELECT * FROM ProbeView LIMIT 0");
        Assert.Null(Assert.Single(empty.ColumnInfo!).DataType);
        Assert.Null(empty.ColumnInfo![0].IsNullable);
        Assert.Equal(0, overrideCalls);
        Assert.Equal(1, calls);
        var actual = Select(db, "SELECT * FROM ProbeView");
        Assert.Equal(42L, Assert.Single(actual.Rows)[0]);
        Assert.Null(Assert.Single(actual.ColumnInfo!).DataType);
        SqlExecutor.Execute(db, "CREATE MATERIALIZED VIEW OverrideSnapshot AS SELECT * FROM ProbeView");
        SqlExecutor.Execute(db, "REFRESH MATERIALIZED VIEW OverrideSnapshot");
        Assert.Null(Assert.Single(Select(db, "SELECT * FROM OverrideSnapshot LIMIT 0").ColumnInfo!).DataType);
        Assert.True(db.Functions.Unregister("CONCAT"));
        Assert.Equal(TableColumnType.String, Assert.Single(Select(db, "SELECT * FROM ProbeView LIMIT 0").ColumnInfo!).DataType);
        Assert.Null(Assert.Single(Select(db, "SELECT * FROM OverrideSnapshot LIMIT 0").ColumnInfo!).DataType);
        SqlExecutor.Execute(db, "REFRESH MATERIALIZED VIEW OverrideSnapshot");
        Assert.Equal(TableColumnType.String, Assert.Single(Select(db, "SELECT * FROM OverrideSnapshot LIMIT 0").ColumnInfo!).DataType);
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    private static void AssertDeclarations(DbDataReader reader)
    {
        Assert.Equal(["Joined", "AllNull", "Identifier", "Nested"], Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        var columns = reader.GetColumnSchema();
        using var schema = reader.GetSchemaTable()!;
        for (int ordinal = 0; ordinal < 4; ordinal++)
        {
            Assert.Equal(typeof(string), reader.GetFieldType(ordinal));
            Assert.Equal("String", reader.GetDataTypeName(ordinal));
            Assert.Equal(ordinal, columns[ordinal].ColumnOrdinal);
            Assert.False(columns[ordinal].AllowDBNull);
            Assert.Null(columns[ordinal].ColumnSize);
            Assert.Null(columns[ordinal].NumericPrecision);
            Assert.Null(columns[ordinal].NumericScale);
            Assert.Equal(typeof(string), schema.Rows[ordinal][SchemaTableColumn.DataType]);
            Assert.Equal(false, schema.Rows[ordinal][SchemaTableColumn.AllowDBNull]);
            Assert.Equal(-1, schema.Rows[ordinal][SchemaTableColumn.ColumnSize]);
        }
    }

    /// <summary>关闭数据库后删除当前用例的专属临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
