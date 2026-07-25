using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证跨执行路径的标量算术、UPDATE 表达式与 Modbus 字节序函数契约。
/// </summary>
public sealed class SqlExpressionExecutionTests : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// 为每个测试创建独立数据库目录，避免并发测试互相污染。
    /// </summary>
    public SqlExpressionExecutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-sql-expression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// 测试结束后尽力清理临时数据库目录。
    /// </summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理失败不覆盖原始断言结果。 */ }
    }

    /// <summary>
    /// 构造指向当前测试独立目录的数据库选项。
    /// </summary>
    private TsdbOptions Options() => new() { RootDirectory = _root };

    /// <summary>
    /// 执行 SQL 并断言返回 SELECT 结果集。
    /// </summary>
    private static SelectExecutionResult Select(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));

    /// <summary>
    /// 验证无 FROM 算术遵循优先级、整数保型、浮点除法与 NULL 传播规则。
    /// </summary>
    [Fact]
    public void Select_ConstantArithmetic_ReturnsTypedResultsAndPropagatesNull()
    {
        using var database = Tsdb.Open(Options());

        var result = Select(database, "SELECT 2 * 5 + 1 AS total, 5 / 2 AS quotient, NULL + 1 AS missing, -7 AS negative");

        Assert.Equal(11L, result.Rows.Single()[0]);
        Assert.Equal(2.5d, result.Rows.Single()[1]);
        Assert.Null(result.Rows.Single()[2]);
        Assert.Equal(-7L, result.Rows.Single()[3]);
    }

    /// <summary>
    /// 验证除零、模零和字符串加法都返回明确执行错误，而不是 Infinity、NaN 或隐式拼接。
    /// </summary>
    [Theory]
    [InlineData("SELECT 1 / 0")]
    [InlineData("SELECT 1 % 0")]
    [InlineData("SELECT 1 + '2'")]
    [InlineData("SELECT 9223372036854775807 + 1")]
    public void Select_InvalidArithmetic_ThrowsExecutionError(string sql)
    {
        using var database = Tsdb.Open(Options());

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, sql));
    }

    /// <summary>
    /// 验证显式 concat 可用于字符串连接，避免依赖加号的非标准隐式转换。
    /// </summary>
    [Fact]
    public void Select_Concat_ConnectsValuesAndTreatsNullAsEmpty()
    {
        using var database = Tsdb.Open(Options());

        var result = Select(database, "SELECT concat('a', NULL, 2) AS text");

        Assert.Equal("a2", result.Rows.Single()[0]);
    }

    /// <summary>
    /// 验证关系表投影和 UPDATE 右值都支持列算术，并按原行快照执行多列赋值。
    /// </summary>
    [Fact]
    public void TableExpressions_SelectAndUpdate_UseOriginalRowSnapshot()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE counters (id INT, a INT, b INT, version INT ROWVERSION, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO counters (id, a, b) VALUES (1, 5, 9)");

        var projected = Select(database, "SELECT a + 1 AS next_a, 2 * b + 1 AS computed FROM counters WHERE id = 1");
        Assert.Equal(new object?[] { 6L, 19L }, projected.Rows.Single());

        SqlExecutor.Execute(database, "UPDATE counters SET a = b, b = a WHERE id = 1");
        var swapped = Select(database, "SELECT a, b, version FROM counters WHERE id = 1");
        Assert.Equal(new object?[] { 9L, 5L, 2L }, swapped.Rows.Single());

        SqlExecutor.Execute(database, "UPDATE counters SET a = a + 1 WHERE id = 1");
        var incremented = Select(database, "SELECT a, version FROM counters WHERE id = 1");
        Assert.Equal(new object?[] { 10L, 3L }, incremented.Rows.Single());

        SqlExecutor.Execute(database,
            "UPDATE counters SET a = modbus_uint32(0, 42, 'ABCD') WHERE id = 1");
        var converted = Select(database, "SELECT a, version FROM counters WHERE id = 1");
        Assert.Equal(new object?[] { 42L, 4L }, converted.Rows.Single());
    }

    /// <summary>
    /// 验证关系表投影在扫描前绑定列并校验常量算术，使错误不依赖表中是否存在数据。
    /// </summary>
    [Fact]
    public void TableSelect_EmptyTable_StillValidatesProjectionExpressions()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE empty_values (id INT, value INT, PRIMARY KEY (id))");

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "SELECT missing + 1 FROM empty_values"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "SELECT 1 + 'x' FROM empty_values"));
    }

    /// <summary>
    /// 验证关系表投影和 UPDATE 右值支持比较、IS NULL、IN、NOT 与 searched CASE 的三值语义。
    /// </summary>
    [Fact]
    public void TableExpressions_PredicateProjectionAndAssignment_UseThreeValuedLogic()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE conditions (id INT, value INT, flag BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, """
            INSERT INTO conditions (id, value, flag)
            VALUES (1, 1, false), (2, NULL, false), (3, 3, false)
            """);

        var projected = Select(database, """
            SELECT id,
                   value > 1 AS greater,
                   value IS NULL AS missing,
                   value IN (1, 3) AS listed,
                   CASE
                       WHEN value IS NULL THEN 'missing'
                       WHEN NOT (value > 1) THEN 'low'
                       ELSE 'high'
                   END AS label
            FROM conditions
            ORDER BY id
            """);

        Assert.Equal(new object?[] { 1L, false, false, true, "low" }, projected.Rows[0]);
        Assert.Equal(new object?[] { 2L, null, true, null, "missing" }, projected.Rows[1]);
        Assert.Equal(new object?[] { 3L, true, false, true, "high" }, projected.Rows[2]);

        SqlExecutor.Execute(database,
            "UPDATE conditions SET flag = value > 1 WHERE id IN (1, 3)");
        var updated = Select(database, "SELECT id, flag FROM conditions ORDER BY id");
        Assert.Equal(new object?[] { 1L, false }, updated.Rows[0]);
        Assert.Equal(new object?[] { 2L, false }, updated.Rows[1]);
        Assert.Equal(new object?[] { 3L, true }, updated.Rows[2]);
    }

    /// <summary>
    /// 验证 UPDATE 预校验接受 json_value 与 CASE IN，并在零命中时仍拒绝错误函数参数个数。
    /// </summary>
    [Fact]
    public void Update_ValidationMatchesRuntimeScalarCapabilities()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE configs (id INT, document JSON, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO configs (id, document, value) VALUES (1, '{\"value\":7}', 0)");

        SqlExecutor.Execute(database, """
            UPDATE configs
            SET value = CASE
                WHEN id IN (1, 2) THEN json_value(document, '$.value') + 1
                ELSE value
            END
            WHERE id = 1
            """);

        Assert.Equal(8L, Select(database, "SELECT value FROM configs WHERE id = 1").Rows.Single()[0]);
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "UPDATE configs SET value = abs(1, 2) WHERE id = 999"));
    }

    /// <summary>
    /// 验证 ROWVERSION 不能手工赋值，且一元正号仍表示普通正数字面量。
    /// </summary>
    [Fact]
    public void Update_RowVersionAndUnaryPlus_ApplyDocumentedRules()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE counters (id INT, value INT, version INT ROWVERSION, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO counters (id, value) VALUES (1, 5)");

        SqlExecutor.Execute(database, "UPDATE counters SET value = +2 WHERE id = 1");
        Assert.Equal(2L, Select(database, "SELECT value FROM counters WHERE id = 1").Rows.Single()[0]);

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "UPDATE counters SET version = version + 1 WHERE id = 1"));
        Assert.Throws<SqlParseException>(() => SqlParser.Parse("UPDATE counters SET value++ WHERE id = 1"));
    }

    /// <summary>
    /// 验证任一行表达式求值失败时整条 UPDATE 不会提交之前已计算的行。
    /// </summary>
    [Fact]
    public void Update_ExpressionFailure_RollsBackWholeStatement()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE calculations (id INT, value INT, divisor INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO calculations (id, value, divisor) VALUES (1, 10, 2), (2, 20, 0)");

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "UPDATE calculations SET value = 100 / divisor WHERE id > 0"));

        var unchanged = Select(database, "SELECT id, value FROM calculations ORDER BY id");
        Assert.Equal(new object?[] { 1L, 10L }, unchanged.Rows[0]);
        Assert.Equal(new object?[] { 2L, 20L }, unchanged.Rows[1]);
    }

    /// <summary>
    /// 验证显式轻事务中的失败 UPDATE 不残留部分 mutation，连续自增则读取并合并事务内前值。
    /// </summary>
    [Fact]
    public void Update_ExplicitTransaction_IsStatementAtomicAndReadsBufferedValue()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE tx_counters (id INT, value INT, divisor INT, version INT ROWVERSION, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO tx_counters (id, value, divisor) VALUES (1, 0, 2), (2, 20, 0)");

        var failedTransaction = new SqlTransactionContext();
        var failing = Assert.IsType<UpdateStatement>(SqlParser.Parse(
            "UPDATE tx_counters SET value = 100 / divisor WHERE id > 0"));
        Assert.Throws<InvalidOperationException>(() =>
            TableSqlExecutor.QueueUpdate(failedTransaction, database, failing));
        Assert.Empty(failedTransaction.SnapshotTableMutations());

        var transaction = new SqlTransactionContext();
        var increment = Assert.IsType<UpdateStatement>(SqlParser.Parse(
            "UPDATE tx_counters SET value = value + 1 WHERE id = 1"));
        TableSqlExecutor.QueueUpdate(transaction, database, increment);
        TableSqlExecutor.QueueUpdate(transaction, database, increment);

        var pending = transaction.SnapshotTableMutations();
        Assert.Single(pending["tx_counters"]);
        Assert.Equal(1, database.Tables.ApplyTransaction(pending));

        var result = Select(database, "SELECT value, version FROM tx_counters WHERE id = 1");
        Assert.Equal(new object?[] { 2L, 3L }, result.Rows.Single());
    }

    /// <summary>
    /// 验证并发直接 UPDATE 的读改写处于同一锁内，不会丢失自增次数。
    /// </summary>
    [Fact]
    public async Task Update_ConcurrentIncrement_IsAtomic()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE TABLE counters (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO counters (id, value) VALUES (1, 0)");

        const int workers = 4;
        const int updatesPerWorker = 20;
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < updatesPerWorker; i++)
                SqlExecutor.Execute(database, "UPDATE counters SET value = value + 1 WHERE id = 1");
        }));

        await Task.WhenAll(tasks);

        Assert.Equal((long)(workers * updatesPerWorker),
            Select(database, "SELECT value FROM counters WHERE id = 1").Rows.Single()[0]);
    }

    /// <summary>
    /// 验证 UPDATE 中的用户标量函数在表管理锁外执行，可等待另一个线程完成关系表 SQL。
    /// </summary>
    [Fact]
    public void Update_UserScalarFunction_DoesNotHoldTableManagerLock()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE udf_updates (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO udf_updates (id, value) VALUES (1, 10), (2, 20)");
        database.Functions.RegisterScalar(
            "increment_after_nested_sql",
            args =>
            {
                var nested = Task.Run(() => SqlExecutor.Execute(database,
                    "UPDATE udf_updates SET value = value + 1 WHERE id = 2"));
                if (!nested.Wait(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("嵌套 SQL 等待表管理锁超时。");
                return (long)args[0]! + 1L;
            },
            minArgumentCount: 1,
            maxArgumentCount: 1);

        SqlExecutor.Execute(database, """
            UPDATE udf_updates
            SET value = increment_after_nested_sql(value)
            WHERE id = 1
            """);

        var result = Select(database, "SELECT id, value FROM udf_updates ORDER BY id");
        Assert.Equal(new object?[] { 1L, 11L }, result.Rows[0]);
        Assert.Equal(new object?[] { 2L, 21L }, result.Rows[1]);
    }

    /// <summary>
    /// 验证时序 measurement 的顶层投影支持列算术和常量复合表达式。
    /// </summary>
    [Fact]
    public void MeasurementSelect_ArithmeticProjection_ReturnsComputedValues()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE MEASUREMENT metrics (host TAG, value FIELD INT)");
        SqlExecutor.Execute(database,
            "INSERT INTO metrics (time, host, value) VALUES (1000, 'h1', 4), (2000, 'h1', 7)");

        var result = Select(database,
            "SELECT time, value + 1 AS next_value, 2 * 5 + 1 AS constant FROM metrics WHERE host = 'h1' ORDER BY time");

        Assert.Equal(new object?[] { 1000L, 5L, 11L }, result.Rows[0]);
        Assert.Equal(new object?[] { 2000L, 8L, 11L }, result.Rows[1]);
    }

    /// <summary>
    /// 验证 measurement TAG 可参与标量函数但不会被误当 FIELD，未知列则在扫描前报错。
    /// </summary>
    [Fact]
    public void MeasurementSelect_TagExpression_ReturnsRowsAndRejectsUnknownColumn()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE MEASUREMENT metrics (host TAG, value FIELD INT)");
        SqlExecutor.Execute(database,
            "INSERT INTO metrics (time, host, value) VALUES (1000, 'h1', 4), (2000, 'h1', 7)");

        var result = Select(database,
            "SELECT time, concat(host, '-x') AS label, value + 1 AS next_value FROM metrics WHERE host = 'h1' ORDER BY time");

        Assert.Equal(new object?[] { 1000L, "h1-x", 5L }, result.Rows[0]);
        Assert.Equal(new object?[] { 2000L, "h1-x", 8L }, result.Rows[1]);
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "SELECT unknown_value + 1 FROM metrics"));
    }

    /// <summary>
    /// 验证 measurement 普通投影支持谓词与 CASE，并在稀疏 FIELD 缺值时保留 UNKNOWN。
    /// </summary>
    [Fact]
    public void MeasurementSelect_PredicateAndCaseProjection_UsesThreeValuedLogic()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE MEASUREMENT predicate_metrics (host TAG, value FIELD INT, other FIELD INT)");
        SqlExecutor.Execute(database, """
            INSERT INTO predicate_metrics (time, host, value, other)
            VALUES (1000, 'h1', 4, 1), (3000, 'h1', 7, 3)
            """);
        SqlExecutor.Execute(database,
            "INSERT INTO predicate_metrics (time, host, other) VALUES (2000, 'h1', 2)");

        var result = Select(database, """
            SELECT time,
                   other,
                   value > 5 AS high,
                   value IS NULL AS missing,
                   value IN (4, NULL) AS selected,
                   CASE
                       WHEN value IS NULL THEN 'missing'
                       WHEN NOT (value > 5) THEN 'low'
                       ELSE 'high'
                   END AS label
            FROM predicate_metrics
            WHERE host = 'h1'
            ORDER BY time
            """);

        Assert.Equal(new object?[] { 1000L, 1L, false, false, true, "low" }, result.Rows[0]);
        Assert.Equal(new object?[] { 2000L, 2L, null, true, null, "missing" }, result.Rows[1]);
        Assert.Equal(new object?[] { 3000L, 3L, true, false, null, "high" }, result.Rows[2]);
    }

    /// <summary>
    /// 验证 JOIN 表达式中的限定维表列不会因与 measurement FIELD 同名而改变稀疏时间轴。
    /// </summary>
    [Fact]
    public void JoinSelect_QualifiedTableColumnCollision_PreservesMeasurementTimeline()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE MEASUREMENT join_metrics (device TAG, first_field FIELD INT, value FIELD INT)");
        SqlExecutor.Execute(database,
            "CREATE TABLE join_devices (id STRING, other INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO join_devices (id, other, value) VALUES ('d1', 7, 8)");
        SqlExecutor.Execute(database, """
            INSERT INTO join_metrics (time, device, first_field)
            VALUES (1000, 'd1', 1), (1500, 'd1', 2)
            """);
        SqlExecutor.Execute(database,
            "INSERT INTO join_metrics (time, device, value) VALUES (2000, 'd1', 9)");

        var otherColumn = Select(database, """
            SELECT m.time, d.other + 1 AS projected
            FROM join_metrics m
            JOIN join_devices d ON m.device = d.id
            ORDER BY m.time
            """);
        var collidingColumn = Select(database, """
            SELECT m.time, d.value + 1 AS projected
            FROM join_metrics m
            JOIN join_devices d ON m.device = d.id
            ORDER BY m.time
            """);

        Assert.Equal([1000L, 1500L], otherColumn.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal([1000L, 1500L], collidingColumn.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.All(collidingColumn.Rows, static row => Assert.Equal(9L, row[1]));
    }

    /// <summary>
    /// 验证时序聚合函数可嵌入算术及外层标量函数，而不要求位于投影根节点。
    /// </summary>
    [Fact]
    public void MeasurementSelect_AggregateExpression_ReturnsComputedValue()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE MEASUREMENT metrics (host TAG, value FIELD INT)");
        SqlExecutor.Execute(database,
            "INSERT INTO metrics (time, host, value) VALUES (1000, 'h1', 4), (2000, 'h1', 7)");

        var result = Select(database, """
            SELECT count(*) + 1 AS row_count,
                   round(avg(value) + 0.25, 2) AS adjusted_average
            FROM metrics
            WHERE host = 'h1'
            """);

        Assert.Equal(3L, result.Rows.Single()[0]);
        Assert.Equal(5.75d, result.Rows.Single()[1]);
    }

    /// <summary>
    /// 验证 measurement 聚合结果可用于 CASE、比较、IS NULL 与 IN 等外层表达式。
    /// </summary>
    [Fact]
    public void MeasurementSelect_AggregatePredicatesAndCase_ReturnComputedValues()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE MEASUREMENT metrics (host TAG, value FIELD INT)");
        SqlExecutor.Execute(database,
            "INSERT INTO metrics (time, host, value) VALUES (1000, 'h1', 4), (2000, 'h1', 7)");

        var result = Select(database, """
            SELECT CASE WHEN count(*) > 0 THEN sum(value) + 1 ELSE 0 END AS total,
                   sum(value) IS NOT NULL AS has_total,
                   count(*) IN (2, 3) AS expected_count
            FROM metrics
            WHERE host = 'h1'
            """);

        Assert.Equal(12d, result.Rows.Single()[0]);
        Assert.Equal(true, result.Rows.Single()[1]);
        Assert.Equal(true, result.Rows.Single()[2]);
    }

    /// <summary>
    /// 验证关系聚合结果可继续参与标量算术，而不要求聚合函数位于投影根节点。
    /// </summary>
    [Fact]
    public void RelationalSelect_AggregateArithmetic_ReturnsComputedValues()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE TABLE sales (id INT, amount INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO sales (id, amount) VALUES (1, 5), (2, 7)");

        var result = Select(database,
            "SELECT sum(amount) + 1 AS total, count(*) + 1 AS row_count FROM sales");

        Assert.Equal(13L, result.Rows.Single()[0]);
        Assert.Equal(3L, result.Rows.Single()[1]);
    }

    /// <summary>
    /// 验证关系聚合外包表达式保持 Int64 精度，并可嵌入 CASE 条件与结果分支。
    /// </summary>
    [Fact]
    public void RelationalSelect_NestedAggregate_PreservesInt64AndSupportsCase()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE TABLE totals (id INT, amount INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO totals (id, amount) VALUES (1, 9007199254740993), (2, 2)");

        var result = Select(database, """
            SELECT sum(amount) + 0 AS exact_total,
                   CASE WHEN count(*) > 0 THEN max(amount) + 1 ELSE 0 END AS next_max
            FROM totals
            """);

        Assert.Equal(9007199254740995L, result.Rows.Single()[0]);
        Assert.Equal(9007199254740994L, result.Rows.Single()[1]);

        var having = Select(database, """
            SELECT sum(amount) + 0 AS exact_total
            FROM totals
            HAVING sum(amount) = 9007199254740995
               AND sum(amount) IN (9007199254740995, 0)
            """);
        Assert.Equal(9007199254740995L, having.Rows.Single()[0]);
    }

    /// <summary>
    /// 验证文档字段提取结果可以继续参与共享算术求值。
    /// </summary>
    [Fact]
    public void DocumentSelect_ArithmeticProjection_ReturnsComputedValue()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE DOCUMENT COLLECTION readings");
        SqlExecutor.Execute(database,
            "INSERT INTO readings (id, document) VALUES ('r1', '{\"value\":7}')");

        var result = Select(database,
            "SELECT json_value(document, '$.value') + 1 AS next_value FROM readings WHERE id = 'r1'");

        Assert.Equal(8d, result.Rows.Single()[0]);
    }

    /// <summary>
    /// 验证文档聚合调用嵌入算术表达式时会先按分组聚合，再计算外层表达式。
    /// </summary>
    [Fact]
    public void DocumentSelect_AggregateExpression_ReturnsComputedValue()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE DOCUMENT COLLECTION readings");
        SqlExecutor.Execute(database, """
            INSERT INTO readings (id, document)
            VALUES ('r1', '{"value":7}'), ('r2', '{"value":3}')
            """);

        var result = Select(database, """
            SELECT sum(json_value(document, '$.value')) + 1 AS total,
                   count(*) + 1 AS row_count
            FROM readings
            """);

        Assert.Equal(11d, result.Rows.Single()[0]);
        Assert.Equal(3L, result.Rows.Single()[1]);
    }

    /// <summary>
    /// 验证文档聚合可位于 CASE 条件和结果中，并继续使用共享整数算术语义。
    /// </summary>
    [Fact]
    public void DocumentSelect_AggregateCase_ReturnsComputedValue()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE DOCUMENT COLLECTION readings");
        SqlExecutor.Execute(database, """
            INSERT INTO readings (id, document)
            VALUES ('r1', '{"value":7}'), ('r2', '{"value":3}')
            """);

        var result = Select(database, """
            SELECT CASE WHEN count(*) > 0
                        THEN sum(json_value(document, '$.value')) + 1
                        ELSE 0
                   END AS total
            FROM readings
            """);

        Assert.Equal(11d, result.Rows.Single()[0]);
    }

    /// <summary>
    /// 验证 INFORMATION_SCHEMA、forecast 与 knn 表值函数都能计算通用投影表达式。
    /// </summary>
    [Fact]
    public void SpecializedSelectSources_ExpressionProjection_ReturnsComputedValues()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, "CREATE TABLE metadata_target (id INT, PRIMARY KEY (id))");

        var metadata = Select(database, """
            SELECT ordinal_position + 1 AS next_ordinal,
                   concat(column_name, '-column') AS label
            FROM information_schema.columns
            WHERE table_name = 'metadata_target'
            ORDER BY ordinal_position
            """);
        Assert.Equal(new object?[] { 2L, "id-column" }, metadata.Rows.Single());

        SqlExecutor.Execute(database, "CREATE MEASUREMENT meter (device TAG, value FIELD FLOAT)");
        SqlExecutor.Execute(database,
            "INSERT INTO meter (time, device, value) VALUES (1000, 'm1', 1), (2000, 'm1', 2)");
        var forecast = Select(database, """
            SELECT value + 1 AS adjusted, concat(device, '-forecast') AS label
            FROM forecast(meter, value, 1, 'linear')
            WHERE device = 'm1'
            """);
        Assert.Equal(4d, forecast.Rows.Single()[0]);
        Assert.Equal("m1-forecast", forecast.Rows.Single()[1]);

        SqlExecutor.Execute(database,
            "CREATE MEASUREMENT vectors (source TAG, embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(database,
            "INSERT INTO vectors (time, source, embedding) VALUES (1000, 'a', [1, 0, 0])");
        var nearest = Select(database, """
            SELECT distance + 1 AS adjusted_distance, concat(source, '-hit') AS label
            FROM knn(vectors, embedding, [1, 0, 0], 1)
            """);
        Assert.Equal(1d, nearest.Rows.Single()[0]);
        Assert.Equal("a-hit", nearest.Rows.Single()[1]);
    }

    /// <summary>
    /// 验证 JSON 文件虚拟表可在 json_value 结果外继续执行共享数值表达式。
    /// </summary>
    [Fact]
    public void JsonVirtualTable_ExpressionProjection_ReturnsComputedValue()
    {
        string path = Path.Combine(_root, "expression-readings.json");
        File.WriteAllText(path, """
            [{"id":"r1","value":7},{"id":"r2","value":3}]
            """);

        using var database = Tsdb.Open(Options());
        string escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
        var result = Select(database, $"""
            SELECT json_value(document, '$.value') + 1 AS next_value
            FROM json_each('{escapedPath}')
            WHERE id = 'r1'
            """);

        Assert.Equal(8d, result.Rows.Single()[0]);
    }

    /// <summary>
    /// 验证四种常见 Modbus 源字节序都能恢复同一个无符号 32 位位模式。
    /// </summary>
    [Theory]
    [InlineData(4660, 22136, "ABCD")]
    [InlineData(13330, 30806, "BADC")]
    [InlineData(22136, 4660, "CDAB")]
    [InlineData(30806, 13330, "DCBA")]
    public void ModbusUInt32_AllByteOrders_ReturnCanonicalValue(long first, long second, string order)
    {
        using var database = Tsdb.Open(Options());

        var result = Select(database,
            $"SELECT modbus_uint32({first}, {second}, '{order}') AS value");

        Assert.Equal(305419896L, result.Rows.Single()[0]);
    }

    /// <summary>
    /// 验证 Modbus 有符号整数与 IEEE-754 单精度解码返回正确 SQL 类型和值。
    /// </summary>
    [Fact]
    public void ModbusTypedDecoders_ReturnSignedAndFloatValues()
    {
        using var database = Tsdb.Open(Options());

        var result = Select(database, """
            SELECT modbus_int32(65535, 65534, 'ABCD') AS signed_value,
                   modbus_float32(16256, 0, 'ABCD') AS float_abcd,
                   modbus_float32(0, 32831, 'DCBA') AS float_dcba,
                   modbus_uint32(NULL, 0, 'ABCD') AS missing
            """);

        Assert.Equal(-2L, result.Rows.Single()[0]);
        Assert.Equal(1d, result.Rows.Single()[1]);
        Assert.Equal(1d, result.Rows.Single()[2]);
        Assert.Null(result.Rows.Single()[3]);
    }

    /// <summary>
    /// 验证 Modbus 函数拒绝越界寄存器、小数寄存器和未知字节序。
    /// </summary>
    [Theory]
    [InlineData("SELECT modbus_uint32(65536, 0, 'ABCD')")]
    [InlineData("SELECT modbus_uint32(1.5, 0, 'ABCD')")]
    [InlineData("SELECT modbus_uint32(1.0000000000000002, 0, 'ABCD')")]
    [InlineData("SELECT modbus_uint32(65535.00000000001, 0, 'ABCD')")]
    [InlineData("SELECT modbus_uint32(0, 0, 'AABB')")]
    public void ModbusDecoder_InvalidInput_ThrowsExecutionError(string sql)
    {
        using var database = Tsdb.Open(Options());

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, sql));
    }

    /// <summary>
    /// 验证多行 INSERT 的后续行校验失败时，不会把前面已转换成功的行写入事务缓冲。
    /// </summary>
    [Fact]
    public void Insert_ExplicitTransaction_LaterRowFailureLeavesNoMutation()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE tx_insert_atomicity (id INT, value INT NOT NULL, PRIMARY KEY (id))");
        var schema = database.Tables.Catalog.TryGet("tx_insert_atomicity")!;
        var statement = Assert.IsType<InsertStatement>(SqlParser.Parse(
            "INSERT INTO tx_insert_atomicity (id, value) VALUES (1, 10), (2, NULL)"));
        var transaction = new SqlTransactionContext();

        Assert.Throws<InvalidOperationException>(() =>
            TableSqlExecutor.QueueInsert(transaction, statement, schema));

        Assert.Empty(transaction.SnapshotTableMutations());
    }

    /// <summary>
    /// 验证 DELETE 的后续行表达式失败时，前面匹配行不会残留为待提交删除。
    /// </summary>
    [Fact]
    public void Delete_ExplicitTransaction_LaterRowFailureLeavesNoMutation()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE tx_delete_atomicity (id INT, divisor INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO tx_delete_atomicity (id, divisor) VALUES (1, 2), (2, 0)");
        var schema = database.Tables.Catalog.TryGet("tx_delete_atomicity")!;
        var statement = Assert.IsType<DeleteStatement>(SqlParser.Parse(
            "DELETE FROM tx_delete_atomicity WHERE 100 / divisor > 0"));
        var transaction = new SqlTransactionContext();

        Assert.Throws<InvalidOperationException>(() =>
            TableSqlExecutor.QueueDelete(transaction, database, statement, schema));

        Assert.Empty(transaction.SnapshotTableMutations());
    }

    /// <summary>
    /// 验证事务内 INSERT→DELETE、UPDATE→DELETE、DELETE→INSERT 都按主键归并为最终净状态。
    /// </summary>
    [Fact]
    public void TransactionMutationChains_CommitFinalNetRowState()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database, """
            CREATE TABLE tx_mutation_chains (
                id INT,
                value INT,
                version INT ROWVERSION,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(database,
            "INSERT INTO tx_mutation_chains (id, value) VALUES (2, 20), (3, 30)");
        var schema = database.Tables.Catalog.TryGet("tx_mutation_chains")!;
        var transaction = new SqlTransactionContext();

        TableSqlExecutor.QueueInsert(transaction, Assert.IsType<InsertStatement>(SqlParser.Parse(
            "INSERT INTO tx_mutation_chains (id, value) VALUES (1, 10)")), schema);
        TableSqlExecutor.QueueDelete(transaction, database, Assert.IsType<DeleteStatement>(SqlParser.Parse(
            "DELETE FROM tx_mutation_chains WHERE id = 1")), schema);

        TableSqlExecutor.QueueUpdate(transaction, database, Assert.IsType<UpdateStatement>(SqlParser.Parse(
            "UPDATE tx_mutation_chains SET value = value + 1 WHERE id = 2")));
        TableSqlExecutor.QueueDelete(transaction, database, Assert.IsType<DeleteStatement>(SqlParser.Parse(
            "DELETE FROM tx_mutation_chains WHERE id = 2")), schema);

        TableSqlExecutor.QueueDelete(transaction, database, Assert.IsType<DeleteStatement>(SqlParser.Parse(
            "DELETE FROM tx_mutation_chains WHERE id = 3")), schema);
        TableSqlExecutor.QueueInsert(transaction, Assert.IsType<InsertStatement>(SqlParser.Parse(
            "INSERT INTO tx_mutation_chains (id, value) VALUES (3, 300)")), schema);

        Assert.Equal(2, transaction.SnapshotTableMutations()[schema.Name].Count);
        TableSqlExecutor.CommitTransaction(database, transaction);

        var result = Select(database,
            "SELECT id, value, version FROM tx_mutation_chains ORDER BY id");
        Assert.Equal(new object?[] { 3L, 300L, 1L }, result.Rows.Single());
    }

    /// <summary>
    /// 验证 INSERT 接在同主键 INSERT 或 UPDATE 后不会被静默归并，COMMIT 仍报告重复修改冲突。
    /// </summary>
    [Fact]
    public void InsertAfterInsertOrUpdate_CommitStillReportsDuplicateMutation()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE tx_duplicate_mutations (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO tx_duplicate_mutations (id, value) VALUES (1, 10)");
        var schema = database.Tables.Catalog.TryGet("tx_duplicate_mutations")!;

        var insertThenInsert = new SqlTransactionContext();
        TableSqlExecutor.QueueInsert(insertThenInsert, Assert.IsType<InsertStatement>(SqlParser.Parse(
            "INSERT INTO tx_duplicate_mutations (id, value) VALUES (2, 20)")), schema);
        TableSqlExecutor.QueueInsert(insertThenInsert, Assert.IsType<InsertStatement>(SqlParser.Parse(
            "INSERT INTO tx_duplicate_mutations (id, value) VALUES (2, 21)")), schema);
        Assert.Equal(2, insertThenInsert.SnapshotTableMutations()[schema.Name].Count);
        Assert.Throws<InvalidOperationException>(() =>
            TableSqlExecutor.CommitTransaction(database, insertThenInsert));

        var updateThenInsert = new SqlTransactionContext();
        TableSqlExecutor.QueueUpdate(updateThenInsert, database, Assert.IsType<UpdateStatement>(SqlParser.Parse(
            "UPDATE tx_duplicate_mutations SET value = 11 WHERE id = 1")));
        TableSqlExecutor.QueueInsert(updateThenInsert, Assert.IsType<InsertStatement>(SqlParser.Parse(
            "INSERT INTO tx_duplicate_mutations (id, value) VALUES (1, 12)")), schema);
        Assert.Equal(2, updateThenInsert.SnapshotTableMutations()[schema.Name].Count);
        Assert.Throws<InvalidOperationException>(() =>
            TableSqlExecutor.CommitTransaction(database, updateThenInsert));

        var rows = Select(database,
            "SELECT id, value FROM tx_duplicate_mutations ORDER BY id").Rows;
        Assert.Equal(new object?[] { 1L, 10L }, rows.Single());
    }
}
