using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证跨绑定阶段的派生表和 CTE 使用最终输出列拼写。</summary>
public sealed class SqlIdentifierDerivedOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-derived-identifier-" + Guid.NewGuid().ToString("N"));

    /// <summary>回收本测试独占的数据库目录。</summary>
    public void Dispose()
    {
        string root = Path.GetFullPath(_root);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporaryRoot, root, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sndb-derived-identifier-", Path.GetFileName(root), StringComparison.Ordinal);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    /// <summary>跨绑定阶段的派生来源使用最终规范输出列名。</summary>
    /// <param name="sql">使用不同引用形式的查询。</param>
    [Theory]
    [InlineData("SELECT value FROM (SELECT VALUE FROM sensor) AS X")]
    [InlineData("WITH Result AS (SELECT VALUE FROM sensor) SELECT value FROM result")]
    [InlineData("SELECT x.value FROM (SELECT VALUE FROM sensor) AS X")]
    [InlineData("SELECT \"Value\" FROM (SELECT VALUE FROM sensor) AS X")]
    public void Select_MeasurementDerivedOutput_RebindsToFinalStoredSpelling(string sql)
    {
        using var db = OpenSensor();

        var result = Select(db, sql);

        Assert.Equal(1.5, Assert.Single(result.Rows)[0]);
        Assert.Equal(sql.Contains("x.value", StringComparison.Ordinal) ? "X.Value" : "Value",
            Assert.Single(result.Columns));
    }

    /// <summary>派生来源的引号引用必须与最终输出列名精确匹配。</summary>
    /// <param name="sql">包含错误引号拼写的查询。</param>
    [Theory]
    [InlineData("SELECT \"VALUE\" FROM (SELECT VALUE FROM sensor) AS X")]
    [InlineData("WITH Result AS (SELECT VALUE FROM sensor) SELECT \"VALUE\" FROM result")]
    public void Select_MeasurementDerivedOutput_WithWrongQuotedSpelling_Rejects(string sql)
    {
        using var db = OpenSensor();

        Assert.Throws<InvalidOperationException>(() => Select(db, sql));
    }

    /// <summary>IN 子查询重写产生的物化值可以再次完成名称绑定。</summary>
    [Fact]
    public void Select_InSubquerySemijoin_AcceptsMaterializedValuesDuringRebinding()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE Device (Id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE Allowed (Id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO device (id) VALUES (1), (2)");
        SqlExecutor.Execute(db, "INSERT INTO allowed (id) VALUES (2)");

        var result = Select(db, "SELECT ID FROM device WHERE id IN (SELECT ID FROM allowed)");

        Assert.Equal(2L, Assert.Single(result.Rows)[0]);
    }

    /// <summary>派生来源中未显式命名的限定列通过 SQL 可见列名参与关联。</summary>
    /// <param name="sql">使用派生来源参与关联的查询。</param>
    /// <param name="metadataName">结果保留的限定列标签。</param>
    /// <param name="expectedId">匹配行的标识符。</param>
    [Theory]
    [InlineData("""
        SELECT "d"."Id"
        FROM "Devices" AS "d"
        JOIN (
            SELECT "p"."Id", "p"."Name"
            FROM "Produces" AS "p"
            ORDER BY "p"."Name", "p"."Id"
            LIMIT 1
        ) AS "s" ON "d"."ProduceId" = "s"."Id"
        """, "d.Id", 10L)]
    [InlineData("""
        SELECT "s"."Id"
        FROM (
            SELECT "p"."Id", "p"."Name"
            FROM "Produces" AS "p"
            ORDER BY "p"."Name", "p"."Id"
            LIMIT 1
        ) AS "s"
        JOIN "Devices" AS "d" ON "s"."Id" = "d"."ProduceId"
        """, "s.Id", 1L)]
    public void Select_QualifiedDerivedOutputInJoin_BindsSqlVisibleColumn(
        string sql, string metadataName, long expectedId)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE \"Produces\" (\"Id\" INT, \"Name\" STRING, PRIMARY KEY (\"Id\"))");
        SqlExecutor.Execute(db, "CREATE TABLE \"Devices\" (\"Id\" INT, \"ProduceId\" INT, PRIMARY KEY (\"Id\"))");
        SqlExecutor.Execute(db, "INSERT INTO \"Produces\" (\"Id\", \"Name\") VALUES (1, 'alpha'), (2, 'beta')");
        SqlExecutor.Execute(db, "INSERT INTO \"Devices\" (\"Id\", \"ProduceId\") VALUES (10, 1), (20, 2)");

        var result = Select(db, sql);

        Assert.Equal(metadataName, Assert.Single(result.Columns));
        Assert.Equal(expectedId, Assert.Single(result.Rows)[0]);
    }

    private Tsdb OpenSensor()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        try
        {
            SqlExecutor.Execute(db, "CREATE MEASUREMENT Sensor (Host TAG, Value FIELD FLOAT)");
            SqlExecutor.Execute(db, "INSERT INTO sensor (time, host, value) VALUES (1000, 'a', 1.5)");
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
}
