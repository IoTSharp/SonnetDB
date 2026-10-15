using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证关系表省略 WHERE 的更新沿用原子性、事务、预算和名称合同。</summary>
public sealed class SqlUnfilteredUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-update257-" + Guid.NewGuid().ToString("N"));

    /// <summary>省略 WHERE 生成显式全匹配条件，并与调用者的 WHERE TRUE 区分。</summary>
    [Fact]
    public void ParseUpdate_OmittedWhere_PreservesIntentAndReturning()
    {
        var update = Assert.IsType<UpdateStatement>(SqlParser.Parse(
            "UPDATE \"Items\" SET \"Value\" = @amount RETURNING \"Value\""));
        Assert.True(update.WhereClauseIsOmitted);
        Assert.True(Assert.IsType<LiteralExpression>(update.Where).BooleanValue);
        Assert.Equal(["Value"], update.ReturningColumns);
        Assert.True(update.ReturningColumnIsQuoted[0]);
        Assert.False(Assert.IsType<UpdateStatement>(SqlParser.Parse(
            "UPDATE Items SET Value=1 WHERE TRUE")).WhereClauseIsOmitted);
        Assert.Throws<SqlParseException>(() => SqlParser.Parse("UPDATE Items SET Value=1 WHERE"));
    }

    /// <summary>空表、单行和多行的自引用参数更新返回实际影响数与最终行，正常重开后保留。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Update_AllRows_ReturnsFinalValuesAndPersists(int rowCount)
    {
        using (var database = Open())
        {
            Create(database, rowCount);
            var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(database, null,
                "UPDATE \"Items\" AS i SET \"Value\" = i.\"Value\" + @amount RETURNING \"Id\", \"Value\", \"Version\"",
                new SqlParameters().AddNamed("amount", 7L)));
            Assert.Equal(rowCount, result.RowsAffected);
            Assert.Equal(["Id", "Value", "Version"], result.Returning!.Columns);
            Assert.Equal(rowCount, result.Returning.Rows.Count);
            foreach (IReadOnlyList<object?> row in result.Returning.Rows)
            {
                long id = Assert.IsType<long>(row[0]);
                Assert.Equal(id * 10 + 7, Assert.IsType<long>(row[1]));
                Assert.Equal(2L, Assert.IsType<long>(row[2]));
            }
        }
        using var reopened = Open();
        var rows = Select(reopened);
        Assert.Equal(rowCount, rows.Rows.Count);
        for (int i = 0; i < rowCount; i++)
            Assert.Equal((i + 1) * 10L + 7, rows.Rows[i][1]);
    }

    /// <summary>后续行的表达式失败不得发布已计算的前一行。</summary>
    [Fact]
    public void Update_LaterRowFails_PreservesAllRowsAndVersions()
    {
        using var database = Open();
        Create(database, 2);
        SqlExecutor.Execute(database, "UPDATE Items SET divisor=0 WHERE Id=2");
        object?[][] before = Select(database).Rows.Select(row => row.ToArray()).ToArray();
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database,
            "UPDATE Items SET Value=100/divisor RETURNING Id,Value"));
        Assert.Equal(before, Select(database).Rows);
    }

    /// <summary>唯一约束失败整句回滚，并保持索引可用。</summary>
    [Fact]
    public void Update_UniqueConstraintFails_PreservesIndexAndRows()
    {
        using var database = Open();
        Create(database, 2);
        SqlExecutor.Execute(database, "CREATE UNIQUE INDEX item_value ON Items(Value)");
        Assert.Throws<SonnetDB.Tables.TableConstraintException>(() => SqlExecutor.Execute(database,
            "UPDATE Items SET Value=100"));
        Assert.Equal(10L, Assert.Single(Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(database, "SELECT Value FROM Items WHERE Value=10")).Rows)[0]);
        Assert.Equal(20L, Select(database).Rows[1][1]);
    }

    /// <summary>省略 WHERE 时联接 ON 仍只更新匹配行；无条件 FROM 沿用既有首个来源语义。</summary>
    [Theory]
    [InlineData("UPDATE Items AS i JOIN (VALUES(2,99)) AS s(Id,Value) ON i.Id=s.Id SET Value=s.Value", 1, 10L, 99L)]
    [InlineData("UPDATE Items AS i SET Value=s.Value FROM (VALUES(99)) AS s(Value)", 2, 99L, 99L)]
    public void Update_JoinWithoutWhere_PreservesSourceMatching(string sql, int affected, long first, long second)
    {
        using var database = Open();
        Create(database, 2);
        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(database, sql));
        Assert.Equal(affected, result.RowsAffected);
        var rows = Select(database);
        Assert.Equal(first, rows.Rows[0][1]);
        Assert.Equal(second, rows.Rows[1][1]);
    }

    /// <summary>事务内连续更新读取缓冲值；回滚保持旧行，提交后的值正常重开可读。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_ExplicitTransaction_PreservesCommitAndRollback(bool commit)
    {
        using (var connection = new SndbConnection($"Data Source={_root}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Items(Id INT,Value INT,PRIMARY KEY(Id))";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO Items(Id,Value) VALUES(1,10),(2,20)";
            command.ExecuteNonQuery();
            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Items SET Value=Value+@step";
            command.Parameters.Add(new SndbParameter("step", 2L));
            Assert.Equal(2, command.ExecuteNonQuery());
            Assert.Equal(2, command.ExecuteNonQuery());
            command.CommandText = "SELECT SUM(Value) FROM Items";
            Assert.Equal(38L, command.ExecuteScalar());
            if (commit) transaction.Commit();
            else transaction.Rollback();
        }
        using var reopened = new SndbConnection($"Data Source={_root}");
        reopened.Open();
        using var query = reopened.CreateCommand();
        query.CommandText = "SELECT SUM(Value) FROM Items";
        Assert.Equal(commit ? 38L : 30L, query.ExecuteScalar());
    }

    /// <summary>已有物化预算和预取消仍拒绝执行，不能通过省略 WHERE 绕过。</summary>
    [Fact]
    public void Update_ResourceOrCancellationLimit_PreservesRows()
    {
        using var database = Open();
        Create(database, 2);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, null,
            "UPDATE Items SET Value=99", null, null, new SqlExecutionOptions { MaxMaterializedRows = 1 }));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var cancellation = Assert.Throws<RoutineExecutionException>(() => SqlExecutor.Execute(database, null,
            "UPDATE Items SET Value=99", null, null, new SqlExecutionOptions { CancellationToken = canceled.Token }));
        Assert.Equal(RoutineErrorCodes.Cancelled, cancellation.Code);
        Assert.Equal(canceled.Token, Assert.IsType<OperationCanceledException>(cancellation.InnerException).CancellationToken);
        Assert.Equal(10L, Select(database).Rows[0][1]);
        Assert.Equal(20L, Select(database).Rows[1][1]);
    }

    /// <summary>本批只开放关系表；文档集合省略 WHERE 仍明确拒绝。</summary>
    [Fact]
    public void Update_DocumentWithoutWhere_RejectsBeforeMutation()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE DOCUMENT COLLECTION docs");
        Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(database, "UPDATE docs SET value=1"));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void Create(Tsdb database, int rowCount)
    {
        SqlExecutor.Execute(database,
            "CREATE TABLE Items(Id INT,Value INT,divisor INT,Version INT ROWVERSION,PRIMARY KEY(Id))");
        for (int i = 1; i <= rowCount; i++)
            SqlExecutor.Execute(database, null, "INSERT INTO Items(Id,Value,divisor) VALUES(@id,@value,2)",
                new SqlParameters().AddNamed("id", (long)i).AddNamed("value", i * 10L));
    }

    private static SelectExecutionResult Select(Tsdb database)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "SELECT Id,Value,Version FROM Items ORDER BY Id"));

    /// <summary>只回收本夹具创建的唯一目录。</summary>
    public void Dispose()
    {
        string absolute = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(absolute) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
            throw new InvalidOperationException("更新夹具目录超出临时目录。");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
