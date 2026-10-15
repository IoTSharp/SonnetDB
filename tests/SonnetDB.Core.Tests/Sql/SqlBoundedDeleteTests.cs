using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证有界关系删除的复合键、事务、约束和失败原子性。</summary>
public sealed class SqlBoundedDeleteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-delete243-" + Guid.NewGuid().ToString("N"));

    /// <summary>LIMIT 保留参数和 Int64 上限，语法不接受表达式、负数或 OFFSET。</summary>
    [Fact]
    public void ParseDelete_RowLimit_PreservesInt64AndRejectsInvalidSyntax()
    {
        var parsed = Assert.IsType<DeleteStatement>(SqlParser.Parse(
            "DELETE FROM \"Items\" WHERE TRUE LIMIT 9223372036854775807 RETURNING \"A\",\"B\""));
        Assert.Equal(long.MaxValue, Assert.IsType<LiteralExpression>(parsed.RowLimit).IntegerValue);
        Assert.True(parsed.MeasurementIsQuoted);
        Assert.Equal(["A", "B"], parsed.ReturningColumns);
        Assert.All(parsed.ReturningColumnIsQuoted, Assert.True);
        var parameterized = Assert.IsType<DeleteStatement>(SqlParser.Parse("DELETE FROM Items WHERE TRUE LIMIT @maximum"));
        Assert.IsType<ParameterExpression>(parameterized.RowLimit);
        foreach (string suffix in new[] { "-1", "1.5", "NULL", "1+1", "1 OFFSET 1", "" })
            Assert.Throws<SqlParseException>(() => SqlParser.Parse("DELETE FROM Items WHERE TRUE LIMIT " + suffix));
    }

    /// <summary>相同前缀的复合主键只删除直接选中的完整键；影响数与 RETURNING 一致并持久化。</summary>
    [Theory]
    [InlineData(0L, 0)]
    [InlineData(1L, 1)]
    [InlineData(2L, 2)]
    [InlineData(99L, 3)]
    [InlineData(long.MaxValue, 3)]
    public void Delete_CompositeKey_DeletesAtMostRequestedRowsAndPersists(long maximum, int expected)
    {
        using (var database = Open())
        {
            Create(database);
            var result = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(database, null,
                "DELETE FROM items WHERE A>0 LIMIT @maximum RETURNING A,B,Value",
                new SqlParameters().AddNamed("maximum", maximum)));
            Assert.Equal(expected, result.SeriesAffected);
            Assert.Equal(expected, result.Returning!.Rows.Count);
            Assert.Equal(expected, result.Returning.Rows.Select(row => (row[0], row[1])).Distinct().Count());
            Assert.Equal(4 - expected, Rows(database).Rows.Count);
            Assert.Contains(Rows(database).Rows, row => Equals(row[0], -1L));
            Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM Items WHERE Value='negative'"));
        }
        using var reopened = Open();
        Assert.Equal(4 - expected, Rows(reopened).Rows.Count);
    }

    /// <summary>零上限不扫描、不触发 generation 快速清表，仍返回声明列元数据。</summary>
    [Fact]
    public void Delete_ZeroLimit_DoesNotScanOrTruncate()
    {
        using var database = Open();
        Create(database);
        var store = database.Tables.Open("Items");
        long scans = store.FullScanCount;
        var result = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(database,
            "DELETE FROM Items WHERE TRUE LIMIT 0 RETURNING *"));
        Assert.Equal(0, result.SeriesAffected);
        Assert.Equal(["A", "B", "Value"], result.Returning!.Columns);
        Assert.Equal(scans, store.FullScanCount);
        Assert.Equal(4, Rows(database).Rows.Count);
    }

    /// <summary>NULL、字符串、小数和负参数在写入前拒绝，不静默转换。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData("1")]
    [InlineData(1.0)]
    [InlineData(true)]
    public void Delete_InvalidLimitParameter_PreservesRows(object? maximum)
    {
        using var database = Open();
        Create(database);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, null,
            "DELETE FROM Items WHERE TRUE LIMIT @maximum",
            new SqlParameters().AddNamed("maximum", maximum)));
        Assert.Equal(4, Rows(database).Rows.Count);
    }

    /// <summary>日期参数不可被通用字面量绑定转换为毫秒删除上限。</summary>
    [Fact]
    public void Delete_DateAndOutOfRangeLimitParameters_PreservesRows()
    {
        using var database = Open();
        Create(database);
        foreach (object maximum in new object[] { DateTime.UtcNow, DateTimeOffset.UtcNow, ulong.MaxValue, 1m })
        {
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, null,
                "DELETE FROM Items WHERE TRUE LIMIT @maximum",
                new SqlParameters().AddNamed("maximum", maximum)));
            Assert.Equal(4, Rows(database).Rows.Count);
        }
    }

    /// <summary>CLR 整数族在 Int64 范围内均可作为有界删除参数。</summary>
    [Fact]
    public void BindDelete_IntegerLimitParameters_PreservesExactValue()
    {
        var statement = SqlParser.Parse("DELETE FROM Items WHERE TRUE LIMIT @maximum");
        foreach (object maximum in new object[] { (byte)1, (sbyte)1, (short)1, (ushort)1, 1, 1u, 1L, 1ul })
        {
            var bound = Assert.IsType<DeleteStatement>(SqlParameterBinder.Bind(statement,
                new SqlParameters().AddNamed("maximum", maximum)));
            Assert.Equal(1L, Assert.IsType<LiteralExpression>(bound.RowLimit).IntegerValue);
        }
    }

    /// <summary>WHERE 的 NULL 三值逻辑保持原合同，并在匹配不足时返回实际行数。</summary>
    [Fact]
    public void Delete_NullPredicateAndTooFewMatches_ReturnsActualCount()
    {
        using var database = Open();
        Create(database);
        SqlExecutor.Execute(database, "UPDATE Items SET Value=NULL WHERE A=1 AND B=1");
        var result = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(database,
            "DELETE FROM Items WHERE Value='first' LIMIT 2"));
        Assert.Equal(0, result.SeriesAffected);
        result = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(database,
            "DELETE FROM Items WHERE Value IS NULL LIMIT 2"));
        Assert.Equal(1, result.SeriesAffected);
    }

    /// <summary>显式事务中的多次有界删除叠加自身写入，提交与回滚后正常重开一致。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_ExplicitTransaction_PreservesCommitRollbackAndOverlay(bool commit)
    {
        using (var connection = new SndbConnection($"Data Source={_root}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Items(A INT,B INT,PRIMARY KEY(A,B))";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO Items(A,B) VALUES(1,1),(1,2),(2,1)";
            command.ExecuteNonQuery();
            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Items WHERE TRUE LIMIT @maximum";
            command.Parameters.Add(new SndbParameter("maximum", 1L));
            Assert.Equal(1, command.ExecuteNonQuery());
            Assert.Equal(1, command.ExecuteNonQuery());
            command.CommandText = "SELECT COUNT(*) FROM Items";
            Assert.Equal(1L, command.ExecuteScalar());
            if (commit) transaction.Commit();
            else transaction.Rollback();
        }
        using var reopened = new SndbConnection($"Data Source={_root}");
        reopened.Open();
        using var query = reopened.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM Items";
        Assert.Equal(commit ? 1L : 3L, query.ExecuteScalar());
    }

    /// <summary>外键拒绝时整句保持原行和索引，不能留下前半批删除。</summary>
    [Fact]
    public void Delete_ForeignKeyFailure_PreservesWholeStatement()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE Parents(Id INT,PRIMARY KEY(Id))");
        SqlExecutor.Execute(database, "CREATE TABLE Children(Id INT,ParentId INT,PRIMARY KEY(Id),FOREIGN KEY(ParentId) REFERENCES Parents(Id))");
        SqlExecutor.Execute(database, "INSERT INTO Parents(Id) VALUES(1),(2),(3)");
        SqlExecutor.Execute(database, "INSERT INTO Children(Id,ParentId) VALUES(1,2)");
        Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(database,
            "DELETE FROM Parents WHERE Id<3 LIMIT 2"));
        Assert.Equal(3L, Scalar(database, "SELECT COUNT(*) FROM Parents"));
        Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM Children"));
    }

    /// <summary>触发器只接收直接选中的行，回滚同时撤销目标行和触发器写入。</summary>
    [Fact]
    public void Delete_TriggerAndRollback_LimitsTransitionsAndRestoresRows()
    {
        using var database = Open();
        Create(database);
        SqlExecutor.Execute(database, "CREATE TABLE AuditRows(A INT,B INT,PRIMARY KEY(A,B))");
        SqlExecutor.Execute(database,
            "CREATE TRIGGER AuditDelete AFTER DELETE ON Items FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO AuditRows(A,B) VALUES(OLD.A,OLD.B); END");
        var results = SqlExecutor.ExecuteScript(database,
            "BEGIN; DELETE FROM Items WHERE TRUE LIMIT 1 RETURNING A,B; SELECT COUNT(*) FROM AuditRows; ROLLBACK;");
        Assert.Equal(1, Assert.IsType<DeleteExecutionResult>(results[1]).SeriesAffected);
        Assert.Equal(1L, Assert.Single(Assert.IsType<SelectExecutionResult>(results[2]).Rows)[0]);
        Assert.Equal(4, Rows(database).Rows.Count);
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM AuditRows"));
    }

    /// <summary>第二次触发器插入失败时整个有界删除保持语句原子性。</summary>
    [Fact]
    public void Delete_TriggerFailure_RollsBackAllSelectedRows()
    {
        using var database = Open();
        Create(database);
        SqlExecutor.Execute(database, "CREATE TABLE AuditRows(Id INT,PRIMARY KEY(Id))");
        SqlExecutor.Execute(database,
            "CREATE TRIGGER AuditDelete AFTER DELETE ON Items FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO AuditRows(Id) VALUES(1); END");
        Assert.Throws<RoutineExecutionException>(() => SqlExecutor.Execute(database,
            "DELETE FROM Items WHERE TRUE LIMIT 2"));
        Assert.Equal(4, Rows(database).Rows.Count);
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM AuditRows"));
    }

    /// <summary>持久 SQL 过程把声明的整型参数绑定到 LIMIT，并保持事务提交。</summary>
    [Fact]
    public void Delete_ProcedureParameter_BindsLimitBeforeExecution()
    {
        using var database = Open();
        Create(database);
        SqlExecutor.Execute(database,
            "CREATE PROCEDURE RemoveRows(IN maximum INT) LANGUAGE SQL AS BEGIN DELETE FROM Items WHERE TRUE LIMIT @maximum; END");
        var result = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(database, "CALL RemoveRows(2)"));
        Assert.Equal(2, result.SeriesAffected);
        Assert.Equal(2, Rows(database).Rows.Count);
    }

    /// <summary>预算与取消在提交前拒绝，不能被 LIMIT 绕过。</summary>
    [Fact]
    public void Delete_ResourceOrCancellationFailure_PreservesRows()
    {
        using var database = Open();
        Create(database);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, null,
            "DELETE FROM Items WHERE TRUE LIMIT 2", null, null, new SqlExecutionOptions { MaxMaterializedRows = 1 }));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<RoutineExecutionException>(() => SqlExecutor.Execute(database, null,
            "DELETE FROM Items WHERE TRUE LIMIT 2", null, null, new SqlExecutionOptions { CancellationToken = stop.Token }));
        Assert.Equal(4, Rows(database).Rows.Count);
    }

    /// <summary>并发原子删除不会重复选择同一已提交主键或超过各自上限。</summary>
    [Fact]
    public async Task Delete_ConcurrentStatements_DeleteDistinctRowsWithinEachLimit()
    {
        using var database = Open();
        Create(database);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(database,
                "DELETE FROM Items WHERE TRUE LIMIT 1 RETURNING A,B")))));
        Assert.All(results, result => Assert.Equal(1, result.SeriesAffected));
        Assert.Equal(4, results.Select(result => Assert.Single(result.Returning!.Rows))
            .Select(row => (row[0], row[1])).Distinct().Count());
        Assert.Empty(Rows(database).Rows);
    }

    /// <summary>其它模型必须显式拒绝 LIMIT，不能忽略上限执行无界删除。</summary>
    [Theory]
    [InlineData("CREATE MEASUREMENT Items(Value FIELD INT)")]
    [InlineData("CREATE DOCUMENT COLLECTION Items")]
    public void Delete_NonRelationalTarget_RejectsBoundedDelete(string create)
    {
        using var database = Open();
        SqlExecutor.Execute(database, create);
        Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(database,
            "DELETE FROM Items WHERE TRUE LIMIT 0"));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void Create(Tsdb database)
    {
        SqlExecutor.Execute(database, "CREATE TABLE Items(A INT,B INT,Value STRING,PRIMARY KEY(A,B))");
        SqlExecutor.Execute(database, "CREATE UNIQUE INDEX ItemValue ON Items(Value)");
        SqlExecutor.Execute(database, "INSERT INTO Items(A,B,Value) VALUES(1,1,'first'),(1,2,'second'),(2,1,'third'),(-1,1,'negative')");
    }

    private static SelectExecutionResult Rows(Tsdb database)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "SELECT * FROM Items ORDER BY A,B"));

    private static object? Scalar(Tsdb database, string sql)
        => Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql)).Rows)[0];

    /// <summary>只清理本夹具创建的唯一临时目录。</summary>
    public void Dispose()
    {
        string absolute = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(absolute) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
            throw new InvalidOperationException("删除夹具目录超出临时目录。");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
