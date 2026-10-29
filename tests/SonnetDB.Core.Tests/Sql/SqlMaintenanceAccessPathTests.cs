using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证现场维护查询的布尔和 NULL 前缀不会漏行，并减少实际扫描候选。</summary>
public sealed class SqlMaintenanceAccessPathTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sndb-maintenance-" + Guid.NewGuid().ToString("N")));

    /// <summary>创建本测试独占的数据库目录。</summary>
    public SqlMaintenanceAccessPathTests() => Directory.CreateDirectory(_root);

    /// <summary>仅删除已核对归属的独占测试目录。</summary>
    public void Dispose()
    {
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!_root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(_root).StartsWith("sndb-maintenance-", StringComparison.Ordinal)
            || (File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("测试目录归属不符。");
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>NOT 布尔前缀在大量不合格候选后按合格 LIMIT/OFFSET 早停，结果与全扫一致。</summary>
    [Fact]
    public void UploadQuery_NotBooleanWithNullOrResidual_UsesOrderedPrefixWithoutCandidateLimit()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        const string columns = "(Id INT NOT NULL, Upload BOOL NULL, DriveIn DATETIME NOT NULL, WeighingLaneId INT NULL, DriveAway DATETIME NULL, IsNotAllowUpload BOOL NULL, PRIMARY KEY (Id))";
        SqlExecutor.Execute(db, "CREATE TABLE uploads " + columns);
        SqlExecutor.Execute(db, "CREATE TABLE uploads_baseline " + columns);
        SqlExecutor.Execute(db, "CREATE INDEX ix_upload_drive ON uploads (Upload, DriveIn, Id)");
        var rows = new IReadOnlyList<object?>[220];
        var watch = Stopwatch.StartNew();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (int i = 0; i < rows.Length && i < 256; i++)
        {
            cancel.Token.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("测试数据生成超时。");
            rows[i] = [i, i < 180 ? false : i < 200 ? true : null, DateTimeOffset.UnixEpoch.AddMilliseconds(i),
                17L, DateTimeOffset.UnixEpoch, i < 80 ? true : i % 2 == 0 ? false : null];
        }
        db.Tables.Open("uploads").InsertMany(rows);
        db.Tables.Open("uploads_baseline").InsertMany(rows);
        const string suffix = " WHERE NOT Upload AND WeighingLaneId IS NOT NULL AND DriveAway IS NOT NULL AND (IsNotAllowUpload IS NULL OR NOT IsNotAllowUpload) AND DriveIn >= 0 ORDER BY DriveIn, Id LIMIT 5 OFFSET 2";
        var limits = new List<int>();
        db.Tables.Open("uploads").RangeScanLimitTestHook = limits.Add;

        var (actual, metrics) = Execute(db, "SELECT Id FROM uploads" + suffix);
        var (baseline, baselineMetrics) = Execute(db, "SELECT Id FROM uploads_baseline" + suffix);

        Assert.Equal(baseline.Rows.Select(static r => r[0]).ToArray(), actual.Rows.Select(static r => r[0]).ToArray());
        Assert.Equal(new object?[] { 82L, 83L, 84L, 85L, 86L }, actual.Rows.Select(static r => r[0]).ToArray());
        Assert.Equal("ix_upload_drive", metrics.IndexName);
        Assert.Equal("secondary_index_range", metrics.AccessPath);
        Assert.InRange(metrics.CandidateRows, 87, 90);
        Assert.Equal(220, baselineMetrics.CandidateRows);
        Assert.Equal([int.MaxValue], limits);
    }

    /// <summary>裸布尔列及 NOT 布尔列遵守 TRUE/FALSE/UNKNOWN，矛盾条件仍由完整 WHERE 排除。</summary>
    [Theory]
    [InlineData("Upload", 1)]
    [InlineData("NOT Upload", 2)]
    [InlineData("NOT Upload AND Upload = TRUE", -1)]
    [InlineData("Upload AND Upload = FALSE", -1)]
    public void BooleanPredicate_NullableAndContradictoryValues_PreservesTruth(string predicate, long expected)
    {
        using var db = CreateVideos();
        SqlExecutor.Execute(db, "CREATE TABLE bools (Id INT NOT NULL, Upload BOOL NULL, PRIMARY KEY (Id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_bool ON bools (Upload)");
        SqlExecutor.Execute(db, "INSERT INTO bools (Id, Upload) VALUES (1, TRUE), (2, FALSE), (3, NULL)");

        var (result, metrics) = Execute(db, "SELECT Id FROM bools WHERE " + predicate);

        Assert.Equal("ix_bool", metrics.IndexName);
        if (expected < 0)
            Assert.Empty(result.Rows);
        else
            Assert.Equal(expected, Assert.Single(result.Rows)[0]);
    }

    /// <summary>NULL/空串分支各自使用复合前缀，未来重试残余不能提前截断候选。</summary>
    [Theory]
    [InlineData("StorageId IS NULL")]
    [InlineData("StorageId = ''")]
    public void VideoQuery_NullOrEmptyPartition_UsesCompositeOrderedRange(string storagePredicate)
    {
        using var db = CreateVideos();
        SqlExecutor.Execute(db, "CREATE INDEX ix_video_storage ON videos (Status, StorageId, StartedAt, Id)");
        const string residual = " AND (NextAt IS NULL OR NextAt <= 10000) ORDER BY StartedAt, Id LIMIT 4 OFFSET 1";
        string suffix = " WHERE Status = 'completed' AND " + storagePredicate + residual;

        var (actual, metrics) = Execute(db, "SELECT Id FROM videos" + suffix);
        var (baseline, baselineMetrics) = Execute(db, "SELECT Id FROM videos_baseline" + suffix);

        Assert.Equal(baseline.Rows.Select(static r => r[0]).ToArray(), actual.Rows.Select(static r => r[0]).ToArray());
        Assert.Equal(4, actual.Rows.Count);
        Assert.Equal("secondary_index_range", metrics.AccessPath);
        Assert.Equal("ix_video_storage", metrics.IndexName);
        Assert.InRange(metrics.CandidateRows, 15, 18);
        Assert.Equal(80, baselineMetrics.CandidateRows);
    }

    /// <summary>非唯一 NULL 完整等值和部分前缀都必须枚举真实 NULL 行并复检残余条件。</summary>
    [Theory]
    [InlineData("Status, StorageId", "secondary_index")]
    [InlineData("Status, StorageId, StartedAt, Id", "secondary_index_prefix")]
    public void IsNullPredicate_NonUniquePointAndPrefix_ReturnsNullPartition(string columns, string accessPath)
    {
        using var db = CreateVideos();
        SqlExecutor.Execute(db, "CREATE INDEX ix_video_null ON videos (" + columns + ")");
        var (actual, metrics) = Execute(db, "SELECT Id FROM videos WHERE Status = 'completed' AND StorageId IS NULL AND NextAt IS NULL ORDER BY Id");
        var (baseline, _) = Execute(db, "SELECT Id FROM videos_baseline WHERE Status = 'completed' AND StorageId IS NULL AND NextAt IS NULL ORDER BY Id");

        Assert.Equal(baseline.Rows.Select(static r => r[0]).ToArray(), actual.Rows.Select(static r => r[0]).ToArray());
        Assert.Equal(20, actual.Rows.Count);
        Assert.Equal(30, metrics.CandidateRows);
        Assert.Equal(accessPath, metrics.AccessPath);
    }

    /// <summary>唯一索引漏存 NULL 键，完整与部分 NULL 前缀都必须明确拒绝并回退全扫。</summary>
    [Theory]
    [InlineData("Status, StorageId")]
    [InlineData("Status, StorageId, StartedAt, Id")]
    public void IsNullPredicate_UniqueIndexOmittingNulls_FallsBackWithoutLosingRows(string columns)
    {
        using var db = CreateVideos();
        // 只保留 NULL 分区，以免本测试数据中的正常重复空串违反唯一约束。
        SqlExecutor.Execute(db, "DELETE FROM videos WHERE StorageId IS NOT NULL");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ix_video_unique ON videos (" + columns + ")");
        const string query = "SELECT Id FROM videos WHERE Status = 'completed' AND StorageId IS NULL AND StartedAt >= 0 ORDER BY Id";
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(query));
        var schema = db.Tables.Catalog.TryGet("videos")!;

        Assert.Empty(TableSqlExecutor.CollectIndexAccessPlans(schema, statement.Where));
        var (actual, metrics) = Execute(db, query);
        Assert.Equal(30, actual.Rows.Count);
        Assert.Equal("table_scan", metrics.AccessPath);
        Assert.Equal(30, metrics.CandidateRows);
    }

    /// <summary>OR 中的 NULL 和非布尔 NOT 不得被提为强制等值前缀。</summary>
    [Theory]
    [InlineData("StorageId IS NULL OR StorageId = ''")]
    [InlineData("NOT Status")]
    [InlineData("NOT (StorageId IS NULL)")]
    public void IndexExtraction_OptionalOrOrNonBooleanLeaves_DoesNotRestrictCandidates(string predicate)
    {
        using var db = CreateVideos();
        SqlExecutor.Execute(db, "CREATE INDEX ix_video_optional ON videos (StorageId, Status)");
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse("SELECT Id FROM videos WHERE " + predicate));

        Assert.Empty(TableSqlExecutor.CollectIndexAccessPlans(db.Tables.Catalog.TryGet("videos")!, statement.Where));
    }

    /// <summary>未提交 NULL 行仍通过事务 overlay 可见，排序优化不绕开本事务写入。</summary>
    [Fact]
    public void IsNullPredicate_TransactionOverlay_PreservesUncommittedRow()
    {
        using var db = CreateVideos();
        SqlExecutor.Execute(db, "CREATE INDEX ix_video_storage ON videos (Status, StorageId, StartedAt, Id)");
        var transaction = new SqlTransactionContext();
        transaction.AddOrMergeTableMutation(db.Tables.Catalog.TryGet("videos")!,
            new TableRowMutation(null, [-1L, "completed", null, DateTimeOffset.UnixEpoch, null]));
        using var ambient = SqlTransactionContext.EnterScope(transaction);
        var schema = db.Tables.Catalog.TryGet("videos")!;
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT Id FROM videos WHERE Status = 'completed' AND StorageId IS NULL AND (NextAt IS NULL OR NextAt <= 10000) ORDER BY StartedAt, Id LIMIT 1"));
        Assert.False(TableSqlExecutor.TryChooseOrderedResidualRangeAccessPlan(schema, statement, out _, out _));
        var actual = TableSqlExecutor.ExecuteSelect(db, statement, schema);
        Assert.Equal(-1L, Assert.Single(actual.Rows)[0]);
    }

    /// <summary>创建有 NULL、空串、已存储及其他状态的有界视频样本和无索引基准。</summary>
    private Tsdb CreateVideos()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        const string columns = "(Id INT NOT NULL, Status STRING NOT NULL, StorageId STRING NULL, StartedAt DATETIME NOT NULL, NextAt DATETIME NULL, PRIMARY KEY (Id))";
        SqlExecutor.Execute(db, "CREATE TABLE videos " + columns);
        SqlExecutor.Execute(db, "CREATE TABLE videos_baseline " + columns);
        var rows = new IReadOnlyList<object?>[80];
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < rows.Length && i < 128; i++)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("视频样本生成超时。");
            rows[i] = [i, i < 70 ? "completed" : "recording", i < 30 ? null : i < 60 ? "" : "stored",
                DateTimeOffset.UnixEpoch.AddMilliseconds(i), i % 30 < 10 ? DateTimeOffset.UnixEpoch.AddMilliseconds(20000) : null];
        }
        db.Tables.Open("videos").InsertMany(rows);
        db.Tables.Open("videos_baseline").InsertMany(rows);
        return db;
    }

    /// <summary>在独立指标与十秒取消预算中执行查询，避免用耗时阈值代替候选数量证据。</summary>
    private static (SelectExecutionResult Result, SqlExecutionMetricsSnapshot Metrics) Execute(Tsdb db, string sql)
    {
        var metrics = new SqlExecutionMetrics();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SelectExecutionResult result;
        using (SqlExecutionTelemetry.Enter(metrics))
            result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, null, null,
                SqlExecutionOptions.Default with { CancellationToken = cancel.Token }));
        return (result, metrics.Complete());
    }
}
