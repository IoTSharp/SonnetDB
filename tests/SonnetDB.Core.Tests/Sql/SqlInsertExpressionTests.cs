using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 #244 的无行 VALUES 标量表达式、精确 Decimal 与语句原子性。</summary>
public sealed class SqlInsertExpressionTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "i244c-" + Guid.NewGuid().ToString("N")));

    /// <summary>原问题的参数 CAST、UTC 函数、多行 RETURNING 与默认列共同工作。</summary>
    [Fact]
    public void Insert_ParameterCastAndUtcFunction_ReturnsGeneratedRows()
    {
        using var db = Open();
        CreateItems(db);
        const decimal amount = 123456789.1234567890123456789m;
        DateTime before = DateTime.UtcNow.AddSeconds(-1);
        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db, null,
            "INSERT INTO items (amount, created) VALUES (CAST(@amount AS DECIMAL), CURRENT_UTC_DATETIME()), "
            + "(CAST(@amount AS DECIMAL) + 0.1, CURRENT_UTC_DATETIME()) RETURNING id, amount, label, created",
            new SqlParameters().AddNamed("amount", amount)));
        Assert.Equal(2, result.RowsInserted);
        Assert.Equal(2, result.Returning!.Rows.Count);
        Assert.Equal(new object?[] { 1L, amount, "fallback" }, result.Returning.Rows[0].Take(3).ToArray());
        Assert.Equal(new object?[] { 2L, amount + 0.1m, "fallback" }, result.Returning.Rows[1].Take(3).ToArray());
        Assert.InRange(Assert.IsType<DateTime>(result.Returning.Rows[0][3]), before, DateTime.UtcNow.AddSeconds(1));
    }

    /// <summary>已有标量运算、函数及 CASE 均可成为单个 VALUES 单元格。</summary>
    [Theory]
    [InlineData("1 + 2 * 3", 7L)]
    [InlineData("CAST('8' AS INT)", 8L)]
    [InlineData("ABS(-9)", 9L)]
    [InlineData("CASE WHEN 2 > 1 THEN 4 ELSE 5 END", 4L)]
    [InlineData("COALESCE(NULL, 6)", 6L)]
    [InlineData("CASE WHEN NOT FALSE AND NULL IS NULL THEN 3 ELSE 0 END", 3L)]
    [InlineData("CAST(json_value('{\"n\":7}', '$.n') AS INT)", 7L)]
    public void Insert_ExistingScalarExpression_StoresExpectedValue(string expression, long expected)
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE numbers (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, $"INSERT INTO numbers (id, value) VALUES (1, {expression})");
        Assert.Equal(expected, Assert.Single(Select(db, "SELECT value FROM numbers").Rows)[0]);
    }

    /// <summary>Decimal 上下文到达 CASE/COALESCE 的所选值，未选数值不提前精确解析。</summary>
    [Fact]
    public void Insert_DecimalBranches_PreservesTokensAndSkipsUnreachedConversion()
    {
        using var db = Open();
        CreateItems(db);
        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db, """
            INSERT INTO items (amount) VALUES
                (CASE WHEN TRUE THEN 0.1234567890123456789012345678 ELSE 1e-29 END),
                (COALESCE(NULL, 9007199254740993.125, 1e-29)),
                (CAST(CASE WHEN TRUE THEN 123456789.1234567890123456789 ELSE CAST('bad' AS DECIMAL) END AS DECIMAL)),
                ((CASE WHEN TRUE THEN 0.1 ELSE 1e-29 END) + COALESCE(NULL, 0.2, 1e-29))
                RETURNING amount
            """));
        Assert.Equal(new object?[] { 0.1234567890123456789012345678m, 9007199254740993.125m,
            123456789.1234567890123456789m, 0.3m }, result.Returning!.Rows.Select(static row => row[0]).ToArray());
    }

    /// <summary>NULL 的 CAST、CASE 和 COALESCE 保留可空列语义。</summary>
    [Fact]
    public void Insert_NullScalarResults_PreservesNull()
    {
        using var db = Open();
        CreateItems(db);
        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO items (amount) VALUES (CAST(NULL AS DECIMAL)), (CASE WHEN TRUE THEN NULL END), "
            + "(COALESCE(NULL, NULL)) RETURNING amount"));
        Assert.Equal(3, result.RowsInserted);
        Assert.All(result.Returning!.Rows, row => Assert.Null(row[0]));
    }

    /// <summary>后行转换、算术或函数失败时，不写前行，也不预留自增号。</summary>
    [Theory]
    [InlineData("CAST('bad' AS DECIMAL)")]
    [InlineData("1 / 0")]
    [InlineData("missing_i244_function()")]
    [InlineData("1e-29")]
    public void Insert_LaterRowFailure_LeavesNoRowsOrGeneratedReservation(string failingExpression)
    {
        using var db = Open();
        CreateItems(db);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            $"INSERT INTO items (amount) VALUES (CAST('0.1' AS DECIMAL)), ({failingExpression})"));
        Assert.Empty(Select(db, "SELECT id FROM items").Rows);
        var next = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO items (amount) VALUES (1 + 2) RETURNING id"));
        Assert.Equal(1L, Assert.Single(next.Returning!.Rows)[0]);
    }

    /// <summary>结构校验不调用 UDF，求值仅调用各已到达单元格一次并保持短路。</summary>
    [Fact]
    public void Insert_UdfAndLazyBranches_InvokesOnlyReachedOperandsOnce()
    {
        using var db = Open();
        CreateItems(db);
        int calls = 0;
        int unreached = 0;
        db.Functions.RegisterScalar("i244_probe", args => { calls++; return args[0]; }, 1, 1);
        db.Functions.RegisterScalar("i244_unreached", _ => { unreached++; throw new InvalidOperationException("unreached"); }, 0, 0);
        const decimal amount = 123456789.1234567890123456789m;
        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db, null,
            "INSERT INTO items (amount) VALUES (CAST(i244_probe(@value) AS DECIMAL) + 0.1), "
            + "(CASE WHEN TRUE THEN i244_probe(@value) ELSE i244_unreached() END), "
            + "(COALESCE(i244_probe(@value), i244_unreached())) RETURNING amount",
            new SqlParameters().AddNamed("value", amount)));
        Assert.Equal(3, calls);
        Assert.Equal(0, unreached);
        Assert.Equal(new object?[] { amount + 0.1m, amount, amount }, result.Returning!.Rows.Select(static row => row[0]).ToArray());
    }

    /// <summary>即使禁止结构位于未选分支，也在任何函数求值或写入前拒绝。</summary>
    [Theory]
    [InlineData("amount")]
    [InlineData("items.amount")]
    [InlineData("CASE WHEN TRUE THEN i244_probe(1) ELSE amount END")]
    [InlineData("COALESCE(i244_probe(1), amount)")]
    [InlineData("CASE WHEN TRUE THEN i244_probe(1) ELSE SUM(1) END")]
    [InlineData("CASE WHEN TRUE THEN i244_probe(1) ELSE COUNT(*) END")]
    [InlineData("CASE WHEN TRUE THEN i244_probe(1) ELSE ROW_NUMBER() OVER () END")]
    [InlineData("CASE WHEN TRUE THEN i244_probe(1) ELSE ABS(1) OVER () END")]
    [InlineData("CASE WHEN TRUE THEN i244_probe(1) ELSE (SELECT 1) END")]
    [InlineData("CASE WHEN TRUE THEN i244_probe(1) ELSE 1 IN (SELECT 1) END")]
    [InlineData("CASE WHEN TRUE THEN i244_probe(1) ELSE ABS() END")]
    public void Insert_ForbiddenNestedStructure_RejectsBeforeEvaluation(string expression)
    {
        using var db = Open();
        CreateItems(db);
        int calls = 0;
        db.Functions.RegisterScalar("i244_probe", args => { calls++; return args[0]; }, 1, 1);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            $"INSERT INTO items (amount) VALUES ({expression})"));
        Assert.Equal(0, calls);
        Assert.Empty(Select(db, "SELECT id FROM items").Rows);
    }

    /// <summary>AST 校验具有独立的深度预算，超限前不加入事务 mutation。</summary>
    [Fact]
    public void QueueInsert_OverDepthExpression_RejectsWithinBudget()
    {
        using var db = Open();
        CreateItems(db);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SqlExpression expression = LiteralExpression.Integer(1);
        for (int index = 0; index < 66; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            expression = new UnaryExpression(SqlUnaryOperator.Negate, expression);
        }
        var statement = new InsertStatement("items", ["amount"], [new SqlExpression[] { expression }]);
        var transaction = new SqlTransactionContext();
        var error = Assert.Throws<InvalidOperationException>(() => TableSqlExecutor.QueueInsert(
            transaction, statement, db.Tables.Catalog.TryGet("items")!));
        Assert.Contains("校验预算", error.Message, StringComparison.Ordinal);
        Assert.Empty(transaction.SnapshotTableMutations());
    }

    /// <summary>失败语句的保存点保留已缓冲数据与触发器结果，RETURNING 和自增顺序保持。</summary>
    [Fact]
    public void Insert_TriggeredTransactionFailure_PreservesExistingBuffer()
    {
        using var db = Open();
        CreateItems(db);
        SqlExecutor.Execute(db, "CREATE TABLE audit_rows (id INT, amount DECIMAL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, """
            CREATE TRIGGER normalize BEFORE INSERT ON items FOR EACH ROW LANGUAGE SQL AS BEGIN
                SET NEW.label = UPPER(NEW.label);
            END
            """);
        SqlExecutor.Execute(db, """
            CREATE TRIGGER audit_items AFTER INSERT ON items FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit_rows (id, amount) VALUES (NEW.id, NEW.amount);
            END
            """);
        var transaction = new SqlTransactionContext();
        var first = Assert.IsType<InsertExecutionResult>(Execute(db,
            "INSERT INTO items (amount, label) VALUES (CAST('1.25' AS DECIMAL), LOWER('KEEP')) RETURNING id, label", transaction));
        Assert.Equal(new object?[] { 1L, "KEEP" }, Assert.Single(first.Returning!.Rows));
        var failure = Assert.ThrowsAny<Exception>(() => Execute(db,
            "INSERT INTO items (amount) VALUES (2 + 0.1), (CAST('bad' AS DECIMAL))", transaction));
        Assert.Contains("INSERT", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, Assert.Single(Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT id FROM items", transaction)).Rows)[0]);
        Assert.Equal(1L, Assert.Single(Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT id FROM audit_rows", transaction)).Rows)[0]);
        var next = Assert.IsType<InsertExecutionResult>(Execute(db,
            "INSERT INTO items (amount) VALUES (COALESCE(NULL, 3.5)) RETURNING id, label", transaction));
        Assert.Equal(new object?[] { 2L, "FALLBACK" }, Assert.Single(next.Returning!.Rows));
        Execute(db, "COMMIT", transaction);
        Assert.Equal(2, Select(db, "SELECT id FROM items").Rows.Count);
        Assert.Equal(2, Select(db, "SELECT id FROM audit_rows").Rows.Count);
    }

    /// <summary>直接与队列 ON CONFLICT 都使用已经求值的候选行并保持 RETURNING。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Insert_OnConflictExpression_UsesEvaluatedCandidate(bool queued)
    {
        using var db = Open();
        CreateItems(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, amount) VALUES (1, 1.25)");
        var transaction = queued ? new SqlTransactionContext() : null;
        var result = Assert.IsType<InsertExecutionResult>(Execute(db,
            "INSERT INTO items (id, amount) VALUES (1 + 0, CAST('2.5' AS DECIMAL)), "
            + "(1 + 1, CASE WHEN TRUE THEN 3.5 ELSE 0 END) ON CONFLICT (id) "
            + "DO UPDATE SET amount = excluded.amount RETURNING id, amount", transaction));
        Assert.Equal(new object?[] { 1L, 2.5m }, result.Returning!.Rows[0]);
        Assert.Equal(new object?[] { 2L, 3.5m }, result.Returning.Rows[1]);
        if (transaction is not null)
            Execute(db, "COMMIT", transaction);
        Assert.Equal(2, Select(db, "SELECT id FROM items").Rows.Count);
    }

    /// <summary>UUID 在 INSERT VALUES 中逐行求值，RETURNING 使用已写值且真实回滚不持久化。</summary>
    [Fact]
    public void Insert_UuidValuesReturningAndRollback_PreservesCapturedValuesAndTransactionBoundary()
    {
        using var db = Open();
        CreateItems(db);
        var transaction = new SqlTransactionContext();
        var inserted = Assert.IsType<InsertExecutionResult>(Execute(db,
            "INSERT INTO items(amount,label) VALUES(CAST('1.25' AS DECIMAL),uuid()),(2+0.5,UUID()) RETURNING id,label", transaction));
        Assert.Equal(2, inserted.RowsInserted);
        var returned = inserted.Returning!.Rows;
        Assert.Equal(2, returned.Count);
        string[] identifiers = returned.Select(row => Assert.IsType<string>(row[1])).ToArray();
        Assert.All(identifiers, value => Assert.True(Guid.TryParseExact(value, "D", out _)));
        Assert.Equal(2, identifiers.Distinct(StringComparer.Ordinal).Count());
        var visible = Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT id,label FROM items ORDER BY id", transaction));
        Assert.Equal(returned.Select(row => row.ToArray()).ToArray(), visible.Rows.Select(row => row.ToArray()).ToArray());
        Assert.Empty(Select(db, "SELECT id FROM items").Rows);
        Execute(db, "ROLLBACK", transaction);
        Assert.True(transaction.IsCompleted);
        Assert.False(transaction.WasCommitted);
        Assert.Empty(Select(db, "SELECT id,label FROM items").Rows);

        var committed = new SqlTransactionContext();
        var next = Assert.IsType<InsertExecutionResult>(Execute(db,
            "INSERT INTO items(amount,label) VALUES(3.5,uuid()) RETURNING id,label", committed));
        var nextRow = Assert.Single(next.Returning!.Rows);
        string nextIdentifier = Assert.IsType<string>(nextRow[1]);
        Assert.True(Guid.TryParseExact(nextIdentifier, "D", out _));
        Assert.DoesNotContain(nextIdentifier, identifiers);
        Execute(db, "COMMIT", committed);
        Assert.True(committed.WasCommitted);
        Assert.Equal(nextRow, Assert.Single(Select(db, "SELECT id,label FROM items").Rows));
    }
    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private static void CreateItems(Tsdb db) => SqlExecutor.Execute(db,
        "CREATE TABLE items (id INT AUTO_INCREMENT, amount DECIMAL NULL, label STRING DEFAULT 'fallback', created DATETIME NULL, PRIMARY KEY (id))");
    private static SelectExecutionResult Select(Tsdb db, string sql) => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
    private static object? Execute(Tsdb db, string sql, SqlTransactionContext? transaction)
        => SqlExecutor.ExecuteStatement(db, databaseName: null, statement: SqlParser.Parse(sql), controlPlane: null, transaction: transaction);

    /// <summary>只回收本用例创建的唯一绝对路径。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
