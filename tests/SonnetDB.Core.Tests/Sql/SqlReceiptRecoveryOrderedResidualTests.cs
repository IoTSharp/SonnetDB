using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>复现现场恢复回执的状态前缀、空值租约与重试残余过滤，验证排序游标安全早停。</summary>
public sealed class SqlReceiptRecoveryOrderedResidualTests : IDisposable
{
    private const int RejectedPrefixRows = 400;
    private const int RecoveryLimit = 100;
    private readonly string _root;

    /// <summary>为单个测试创建独占数据库目录，不复用现场或共享缓存。</summary>
    public SqlReceiptRecoveryOrderedResidualTests()
    {
        _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sndb-receipt-ordered-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_root);
    }

    /// <summary>核对绝对路径与独占目录归属后回收本测试数据库。</summary>
    public void Dispose()
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!_root.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(_root).StartsWith("sndb-receipt-ordered-", StringComparison.Ordinal)
            || (File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("测试清理目录归属不符。");
        }
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>两个状态索引并存时，跳过大量未就绪回执后取得一百条，尾部增长不扩大实际检查量。</summary>
    [Theory]
    [InlineData(false, 100)]
    [InlineData(false, 400)]
    [InlineData(true, 400)]
    public void RecoveryQuery_NullOrResidualAfterRejectedPrefix_StopsAtQualifyingLimit(bool descending, int tailRows)
    {
        using var db = CreateDatabase();
        int stateRows = RejectedPrefixRows + RecoveryLimit + tailRows;
        var rows = CreateRecoveryRows(stateRows, descending);
        db.Tables.Open("receipts").InsertMany(rows);
        db.Tables.Open("receipts_baseline").InsertMany(rows);
        var observedLimits = new List<int>();
        db.Tables.Open("receipts").RangeScanLimitTestHook = observedLimits.Add;

        string query = RecoveryQuery("receipts", descending);
        var (actual, metrics) = ExecuteWithMetrics(db, query);
        var (baseline, baselineMetrics) = ExecuteWithMetrics(db, RecoveryQuery("receipts_baseline", descending));

        Assert.Equal(baseline.Rows.Select(static row => row[0]).ToArray(), actual.Rows.Select(static row => row[0]).ToArray());
        Assert.Equal(RecoveryLimit, actual.Rows.Count);
        Assert.Equal("secondary_index_range", metrics.AccessPath);
        Assert.Equal("ix_receipts_state_updated", metrics.IndexName);
        Assert.Equal(RejectedPrefixRows + RecoveryLimit, metrics.CandidateRows);
        Assert.Equal(RejectedPrefixRows + RecoveryLimit, metrics.ExaminedRows);
        Assert.Equal("table_scan", baselineMetrics.AccessPath);
        Assert.Equal(stateRows + 20, baselineMetrics.ExaminedRows);
        // LIMIT 约束合格结果，不能截断前一百个均未就绪的候选。
        Assert.Equal([int.MaxValue], observedLimits);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN " + query));
        var planValues = explain.Rows.ToDictionary(static row => (string)row[0]!, static row => row[1], StringComparer.Ordinal);
        Assert.Equal("ix_receipts_state_updated", planValues["index_name"]);
        Assert.Contains("candidate_limit=none", Assert.IsType<string>(planValues["candidate_plans"]));
    }

    /// <summary>可恢复记录不足上限时必须读完分区，不能把早停误当成完整分区的固定扫描预算。</summary>
    [Fact]
    public void RecoveryQuery_FewerQualifyingRows_ReturnsEveryReadyReceipt()
    {
        using var db = CreateDatabase();
        var rows = CreateRecoveryRows(800, descending: false);
        var watch = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // 原合格窗口中的后六十条也延迟重试，仅保留四十条就绪回执。
        for (int index = 440; index < 800; index++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("测试数据修改超时。");
            rows[index] = [rows[index][0], rows[index][1], rows[index][2], null, DateTimeOffset.UnixEpoch.AddMilliseconds(20_000)];
        }
        db.Tables.Open("receipts").InsertMany(rows);
        db.Tables.Open("receipts_baseline").InsertMany(rows);

        var (actual, metrics) = ExecuteWithMetrics(db, RecoveryQuery("receipts", descending: false));
        var (baseline, _) = ExecuteWithMetrics(db, RecoveryQuery("receipts_baseline", descending: false));

        Assert.Equal(40, actual.Rows.Count);
        Assert.Equal(baseline.Rows.Select(static row => row[0]).ToArray(), actual.Rows.Select(static row => row[0]).ToArray());
        Assert.Equal(800, metrics.ExaminedRows);
    }

    /// <summary>无显式范围时可空排序列必须回退保留 NULL；显式比较排除 NULL 后可使用范围游标。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RecoveryQuery_NullableOrderColumn_PreservesNullAndRangeSemantics(bool descending, bool explicitRange)
    {
        using var db = CreateDatabase(nullableUpdatedAt: true);
        IReadOnlyList<object?>[] rows =
        [
            ["null-time", "pending", null, null, null],
            ["zero-time", "pending", DateTimeOffset.UnixEpoch, null, null],
            ["later-time", "pending", DateTimeOffset.UnixEpoch.AddMilliseconds(1), null, null],
        ];
        db.Tables.Open("receipts").InsertMany(rows);
        db.Tables.Open("receipts_baseline").InsertMany(rows);
        string range = explicitRange ? " AND UpdatedAt >= 0" : string.Empty;
        string query = RecoveryQuery("receipts", descending, range);
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(query));
        var schema = db.Tables.Catalog.TryGet("receipts")!;
        Assert.Equal(explicitRange, TableSqlExecutor.TryChooseOrderedResidualRangeAccessPlan(schema, statement, out _, out _));

        var (actual, metrics) = ExecuteWithMetrics(db, query);
        var (baseline, _) = ExecuteWithMetrics(db, RecoveryQuery("receipts_baseline", descending, range));

        Assert.Equal(baseline.Rows.Select(static row => row[0]).ToArray(), actual.Rows.Select(static row => row[0]).ToArray());
        Assert.Equal(explicitRange ? 2 : 3, actual.Rows.Count);
        Assert.Equal(explicitRange ? "secondary_index_range" : "secondary_index_prefix", metrics.AccessPath);
    }

    /// <summary>新增 OR 优化仅覆盖已绑定顶层 AND 等值前缀，其他形状保持既有回退。</summary>
    [Theory]
    [InlineData("SELECT Guid FROM receipts WHERE UpdatedAt >= 0 AND (LeaseUntil IS NULL OR LeaseUntil <= 10000) ORDER BY UpdatedAt LIMIT 1")]
    [InlineData("SELECT Guid FROM receipts WHERE State = 'pending' OR LeaseUntil IS NULL ORDER BY UpdatedAt LIMIT 1")]
    [InlineData("SELECT Guid FROM receipts WHERE State = 'pending' AND NextAttemptAt IN (0, 1) ORDER BY UpdatedAt LIMIT 1")]
    [InlineData("SELECT Guid FROM receipts WHERE State = 'pending' AND EXISTS (SELECT Guid FROM receipts_baseline) ORDER BY UpdatedAt LIMIT 1")]
    [InlineData("SELECT Guid FROM receipts WHERE State = 'pending' AND (LeaseUntil IS NULL OR ABS(1) = 1) ORDER BY UpdatedAt LIMIT 1")]
    [InlineData("SELECT DISTINCT Guid FROM receipts WHERE State = 'pending' AND (LeaseUntil IS NULL OR LeaseUntil <= 10000) ORDER BY UpdatedAt LIMIT 1")]
    [InlineData("SELECT Guid FROM receipts WHERE State = 'pending' AND (LeaseUntil IS NULL OR LeaseUntil <= 10000) ORDER BY UpdatedAt ASC, Guid DESC LIMIT 1")]
    public void RecoveryQuery_UnsupportedResidualShape_DoesNotChooseOrderedPlan(string query)
    {
        using var db = CreateDatabase();
        var schema = db.Tables.Catalog.TryGet("receipts")!;
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(query));

        Assert.False(TableSqlExecutor.TryChooseOrderedResidualRangeAccessPlan(schema, statement, out _, out _));
    }

    /// <summary>事务有未提交变更时仍合并 overlay，不能用排序索引漏掉本事务新回执。</summary>
    [Fact]
    public void RecoveryQuery_TransactionOverlay_SeesUncommittedReceiptAndFallsBack()
    {
        using var db = CreateDatabase();
        var schema = db.Tables.Catalog.TryGet("receipts")!;
        var transaction = new SqlTransactionContext();
        transaction.AddOrMergeTableMutation(schema, new TableRowMutation(null,
            ["uncommitted", "pending", DateTimeOffset.UnixEpoch, null, null]));
        using var ambient = SqlTransactionContext.EnterScope(transaction);
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(RecoveryQuery("receipts", descending: false)));

        Assert.False(TableSqlExecutor.TryChooseOrderedResidualRangeAccessPlan(schema, statement, out _, out _));
        var actual = TableSqlExecutor.ExecuteSelect(db, statement, schema);
        Assert.Equal("uncommitted", Assert.Single(actual.Rows)[0]);
    }

    /// <summary>创建带两个状态索引的恢复表及完全相同但无索引的全扫基准表。</summary>
    private Tsdb CreateDatabase(bool nullableUpdatedAt = false)
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        string nullability = nullableUpdatedAt ? "NULL" : "NOT NULL";
        string columns = $"(Guid STRING NOT NULL, State STRING NOT NULL, UpdatedAt DATETIME {nullability}, LeaseUntil DATETIME NULL, NextAttemptAt DATETIME NULL, PRIMARY KEY (Guid))";
        SqlExecutor.Execute(db, "CREATE TABLE receipts " + columns);
        SqlExecutor.Execute(db, "CREATE TABLE receipts_baseline " + columns);
        // 保持与现场目录顺序一致：先建立无法满足 UpdatedAt 排序的重试索引。
        SqlExecutor.Execute(db, "CREATE INDEX ix_receipts_state_next ON receipts (State, NextAttemptAt)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_receipts_state_updated ON receipts (State, UpdatedAt)");
        return db;
    }

    /// <summary>生成有界数据集，覆盖未来租约、未来重试及两字段的 NULL/到期组合。</summary>
    private static IReadOnlyList<object?>[] CreateRecoveryRows(int stateRows, bool descending)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stateRows, 1_000);
        var rows = new IReadOnlyList<object?>[stateRows + 20];
        var watch = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (int index = 0; index < rows.Length && index < 1_024; index++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("测试数据生成超时。");
            bool otherState = index >= stateRows;
            bool rejected = index < RejectedPrefixRows;
            long updated = descending ? stateRows - index : index + 1;
            var due = DateTimeOffset.UnixEpoch.AddMilliseconds(index % 3 == 0 ? 10_000 : 9_000);
            object? lease = rejected
                ? index % 2 == 0 ? null : DateTimeOffset.UnixEpoch.AddMilliseconds(20_000)
                : index % 4 < 2 ? null : due;
            object? retry = rejected
                ? index % 2 != 0 ? null : DateTimeOffset.UnixEpoch.AddMilliseconds(20_000)
                : index % 2 == 0 ? null : due;
            rows[index] = [$"receipt-{index:D4}", otherState ? "persisted" : "pending", DateTimeOffset.UnixEpoch.AddMilliseconds(updated), lease, retry];
        }
        return rows;
    }

    /// <summary>构造与现场同形的状态、租约及重试查询，排序方向与显式范围由测试固定输入选择。</summary>
    private static string RecoveryQuery(string table, bool descending, string extraPredicate = "")
        => $"SELECT Guid, UpdatedAt FROM {table} WHERE State = 'pending' AND (LeaseUntil IS NULL OR LeaseUntil <= 10000) AND (NextAttemptAt IS NULL OR NextAttemptAt <= 10000){extraPredicate} ORDER BY UpdatedAt {(descending ? "DESC" : "ASC")} LIMIT {RecoveryLimit}";

    /// <summary>在十秒取消预算和独立遥测作用域内执行查询，保留实际候选数量。</summary>
    private static (SelectExecutionResult Result, SqlExecutionMetricsSnapshot Metrics) ExecuteWithMetrics(Tsdb db, string query)
    {
        var metrics = new SqlExecutionMetrics();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SelectExecutionResult result;
        using (SqlExecutionTelemetry.Enter(metrics))
            result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, query, null, null,
                SqlExecutionOptions.Default with { CancellationToken = cancellation.Token }));
        return (result, metrics.Complete());
    }
}
