using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>#338 提交临界区的函数及数据源边界。</summary>
public sealed class SqlDeferredTriggerSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-338-safety-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(45));

    /// <summary>回收本测试独占的数据库目录和取消源。</summary>
    public void Dispose()
    {
        _deadline.Cancel();
        _deadline.Dispose();
        string path = Path.GetFullPath(_root);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
            Path.GetDirectoryName(path));
        Assert.StartsWith("sndb-338-safety-", Path.GetFileName(path));
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    /// <summary>定义校验拒绝直接调用应用函数。</summary>
    [Fact]
    public void CreateConstraintTrigger_RegisteredScalar_RejectsWithoutInvokingCallback()
    {
        using var db = Open();
        int calls = 0;
        db.Functions.RegisterScalar("application_value", _ => { calls++; return 9L; });
        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db,
            DeferredSql("UPDATE target_rows SET value = application_value(value) WHERE id = 1")));
        Assert.Equal(RoutineErrorCodes.Dependency, error.Code);
        Assert.Equal(0, calls);
        Assert.Null(db.Routines.TryGetTrigger("deferred_source"));
    }

    /// <summary>创建之后覆盖内置函数仍在提交前被拒绝。</summary>
    [Fact]
    public void Commit_LateBuiltinOverride_RejectsWithoutInvokingCallback()
    {
        using var db = Open();
        Execute(db, DeferredSql("UPDATE target_rows SET value = abs(value) WHERE id = 1"));
        var transaction = new SqlTransactionContext();
        Execute(db, "INSERT INTO source_rows (id) VALUES (1)", transaction);
        int calls = 0;
        db.Functions.RegisterScalar("abs", _ => { calls++; return 9L; });

        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db, "COMMIT", transaction));

        Assert.Equal(RoutineErrorCodes.Dependency, error.Code);
        Assert.True(transaction.IsCompleted);
        Assert.Equal(0, calls);
        Assert.Empty(Select(db, "SELECT id FROM source_rows").Rows);
        Assert.Equal(1L, Select(db, "SELECT value FROM target_rows").Rows[0][0]);
    }

    /// <summary>延迟 DML 间接触发的普通触发器也受提交边界约束。</summary>
    [Fact]
    public void Commit_IndirectImmediateTriggerUdf_RejectsWithoutInvokingCallback()
    {
        using var db = Open();
        int calls = 0;
        db.Functions.RegisterScalar("application_value", _ => { calls++; return 9L; });
        Execute(db, """
            CREATE TRIGGER relay AFTER INSERT ON relay_rows FOR EACH ROW LANGUAGE SQL AS BEGIN
                UPDATE target_rows SET value = application_value(value) WHERE id = 1;
            END
            """);
        Execute(db, DeferredSql("INSERT INTO relay_rows (id) VALUES (NEW.id)"));
        var transaction = new SqlTransactionContext();
        Execute(db, "INSERT INTO source_rows (id) VALUES (1)", transaction);

        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db, "COMMIT", transaction));

        Assert.Equal(RoutineErrorCodes.Dependency, error.Code);
        Assert.Equal(0, calls);
        Assert.Empty(Select(db, "SELECT id FROM source_rows").Rows);
        Assert.Empty(Select(db, "SELECT id FROM relay_rows").Rows);
        Assert.Equal(1L, Select(db, "SELECT value FROM target_rows").Rows[0][0]);
    }

    /// <summary>普通立即触发器保留既有应用函数行为。</summary>
    [Fact]
    public void ImmediateTrigger_RegisteredScalar_StillExecutes()
    {
        using var db = Open();
        int calls = 0;
        db.Functions.RegisterScalar("application_value", arguments => { calls++; return (long)arguments[0]! + 1; });
        Execute(db, """
            CREATE TRIGGER immediate_source AFTER INSERT ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN
                UPDATE target_rows SET value = application_value(value) WHERE id = 1;
            END
            """);

        Execute(db, "INSERT INTO source_rows (id) VALUES (1)");

        Assert.Equal(1, calls);
        Assert.Equal(2L, Select(db, "SELECT value FROM target_rows").Rows[0][0]);
    }

    /// <summary>已存在的非关系基础数据源不能进入提交检查。</summary>
    [Theory]
    [InlineData("CREATE VIEW boundary_source AS SELECT id FROM source_rows")]
    [InlineData("CREATE MATERIALIZED VIEW boundary_source AS SELECT id FROM source_rows")]
    [InlineData("CREATE DOCUMENT COLLECTION boundary_source")]
    [InlineData("CREATE MEASUREMENT boundary_source (id FIELD INT)")]
    [InlineData("CREATE GRAPH boundary_source")]
    public void Validate_NonBaseTableSource_Rejects(string createSource)
    {
        using var db = Open();
        Execute(db, createSource);
        var definition = Definition("INSERT INTO relay_rows (id) SELECT id FROM boundary_source");

        var error = Assert.Throws<RoutineExecutionException>(() => DeferredTriggerSafety.Validate(db, definition));

        Assert.Equal(RoutineErrorCodes.Dependency, error.Code);
    }

    /// <summary>无论名称或是否已注册，表值函数均不能进入提交阶段。</summary>
    [Theory]
    [InlineData("json_file")]
    [InlineData("application_rows")]
    public void Validate_TableValuedSource_RejectsWithoutInvokingCallback(string functionName)
    {
        using var db = Open();
        int calls = 0;
        db.Functions.RegisterTableValuedFunction("application_rows", (_, _) =>
        {
            calls++;
            return new SelectExecutionResult(["id"], []);
        });
        var statement = Assert.IsType<CreateTriggerStatement>(SqlParser.Parse(
            DeferredSql("INSERT INTO relay_rows (id) SELECT id FROM source_rows")));
        var insert = Assert.IsType<InsertStatement>(statement.Body[0]);
        var query = insert.Query! with { TableValuedFunction = new FunctionCallExpression(functionName, []) };
        var definition = TriggerDefinition.Create(statement with { Body = [insert with { Query = query }] });

        var error = Assert.Throws<RoutineExecutionException>(() => DeferredTriggerSafety.Validate(db, definition));

        Assert.Equal(RoutineErrorCodes.Dependency, error.Code);
        Assert.Equal(0, calls);
    }

    /// <summary>关系形状的 GRAPH_TABLE 包装不能绕过图源限制。</summary>
    [Fact]
    public void Validate_GraphTableSource_Rejects()
    {
        using var db = Open();
        Execute(db, "CREATE GRAPH boundary_graph");
        var definition = Definition("""
            INSERT INTO relay_rows (id)
            SELECT source_id FROM GRAPH_TABLE (
                boundary_graph MATCH (a IS 1)-[e IS 2]->(b IS 3)
                COLUMNS (a.id AS source_id)
            )
            """);

        Assert.Equal(RoutineErrorCodes.Dependency,
            Assert.Throws<RoutineExecutionException>(() => DeferredTriggerSafety.Validate(db, definition)).Code);
    }

    /// <summary>本地 transition alias 及关系聚合保留合法路径。</summary>
    [Fact]
    public void Validate_StatementTransitionAliasAndPureAggregate_Accepts()
    {
        using var db = Open();
        var statement = Assert.IsType<CreateTriggerStatement>(SqlParser.Parse("""
            CREATE TRIGGER summarize_relay AFTER INSERT ON relay_rows
            REFERENCING NEW TABLE AS inserted_rows FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                INSERT INTO target_rows (id, value) SELECT id, count(*) FROM inserted_rows GROUP BY id;
            END
            """));

        DeferredTriggerSafety.Validate(db, TriggerDefinition.Create(statement));
    }

    /// <summary>嵌套子查询各位置的函数必须全部校验。</summary>
    [Theory]
    [InlineData("INSERT INTO relay_rows (id) SELECT id FROM source_rows WHERE id IN (SELECT application_value(id) FROM source_rows)")]
    [InlineData("INSERT INTO relay_rows (id) SELECT id FROM source_rows UNION SELECT application_value(id) FROM source_rows")]
    [InlineData("INSERT INTO relay_rows (id) SELECT id FROM source_rows ORDER BY application_value(id)")]
    [InlineData("UPDATE target_rows SET value = CASE WHEN EXISTS (SELECT id FROM source_rows WHERE application_value(id) = 1) THEN 2 ELSE 1 END WHERE id = 1")]
    public void Validate_NestedApplicationFunction_Rejects(string body)
    {
        using var db = Open();
        int calls = 0;
        db.Functions.RegisterScalar("application_value", _ => { calls++; return 1L; });

        var error = Assert.Throws<RoutineExecutionException>(() => DeferredTriggerSafety.Validate(db, Definition(body)));

        Assert.Equal(RoutineErrorCodes.Dependency, error.Code);
        Assert.Equal(0, calls);
    }

    /// <summary>新增而未审核的 AST 节点默认拒绝。</summary>
    [Fact]
    public void Validate_UnknownExpression_Rejects()
    {
        using var db = Open();
        var statement = Assert.IsType<CreateTriggerStatement>(SqlParser.Parse(
            DeferredSql("UPDATE target_rows SET value = 1 WHERE id = 1")));
        var update = Assert.IsType<UpdateStatement>(statement.Body[0]);
        var definition = TriggerDefinition.Create(statement with
        {
            Body = [update with { Where = new UnreviewedExpression() }],
        });

        Assert.Equal(RoutineErrorCodes.Dependency,
            Assert.Throws<RoutineExecutionException>(() => DeferredTriggerSafety.Validate(db, definition)).Code);
    }

    private sealed record UnreviewedExpression : SqlExpression;

    private Tsdb Open()
    {
        var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            AllowUserFunctions = true,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        });
        try
        {
            Execute(db, "CREATE TABLE source_rows (id INT, PRIMARY KEY (id))");
            Execute(db, "CREATE TABLE relay_rows (id INT, PRIMARY KEY (id))");
            Execute(db, "CREATE TABLE target_rows (id INT, value INT, PRIMARY KEY (id))");
            Execute(db, "INSERT INTO target_rows (id, value) VALUES (1, 1)");
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private object? Execute(Tsdb db, string sql, SqlTransactionContext? transaction = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction,
            new SqlExecutionOptions { CancellationToken = _deadline.Token });

    private SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(Execute(db, sql));

    private static TriggerDefinition Definition(string body)
        => TriggerDefinition.Create(Assert.IsType<CreateTriggerStatement>(SqlParser.Parse(DeferredSql(body))));

    private static string DeferredSql(string body)
        => "CREATE CONSTRAINT TRIGGER deferred_source AFTER INSERT ON source_rows "
            + "DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN " + body + "; END";
}
