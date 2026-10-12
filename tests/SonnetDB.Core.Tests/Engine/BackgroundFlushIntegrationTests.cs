using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// 后台 Flush 集成测试：验证连续写入 5000 点后，后台线程自动产生多个 Segment，并查询正确。
/// </summary>
public sealed class BackgroundFlushIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public BackgroundFlushIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TsdbOptions MakeOptions() =>
        new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxPoints = 500,
                MaxBytes = 1024L * 1024 * 1024,
                MaxAge = TimeSpan.FromHours(24),
            },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
            // 本 fixture 验证 Flush 产生物理段；后台 Compaction 会合并这些段。
            Compaction = new() { Enabled = false },
            BackgroundFlush = new BackgroundFlushOptions
            {
                Enabled = true,
                PollInterval = TimeSpan.FromMilliseconds(100),
                ShutdownTimeout = TimeSpan.FromSeconds(15),
            },
        };

    private static Point MakePoint(long timestamp, double value) =>
        Point.Create("metrics", timestamp,
            new Dictionary<string, string> { ["host"] = "srv1" },
            new Dictionary<string, FieldValue> { ["val"] = FieldValue.FromDouble(value) });

    /// <summary>
    /// 分批写入 5000 点，每批超过 MaxPoints（500）后等待后台自动 Flush，验证：
    /// - 每批写入后后台线程至少产生一个新 Segment
    /// - 查询能返回全部 5000 点（跨 segment + MemTable 残余）
    /// </summary>
    /// <remarks>
    /// 分批写入并等待，而非一次性写完，是为了避免写入速度远快于后台 Flush 轮询频率时
    /// 所有数据堆积在一个 MemTable 中只产生一个 Segment 的竞态问题。
    /// 本 fixture 显式关闭 Compaction，确保物理段数可用于观察每批 Flush 的增长。
    /// </remarks>
    [Fact]
    public void ContinuousWrite_5000Points_AutoFlushesMultipleSegments()
    {
        const int totalPoints = 5000;
        const int batchSize = 600; // 大于 MaxPoints（500），确保每批都触发 Flush
        const int batchCount = totalPoints / batchSize;
        const int maxBatchPolls = 300; // 每次 50ms，且仍受原 15s 墙钟限制。
        var testClock = System.Diagnostics.Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(150));

        using var db = Tsdb.Open(MakeOptions());

        int written = 0;
        int segmentsBefore = 0;

        // 分批写入，每批写完后等待后台至少产生一个新 Segment
        for (int batch = 0; batch < batchCount; batch++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            Assert.True(testClock.Elapsed < TimeSpan.FromSeconds(150), "连续写入测试超出总墙钟预算。");
            for (int i = 0; i < batchSize; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                db.Write(MakePoint(1000L + written + i, written + i));
            }
            written += batchSize;

            int expectedMin = segmentsBefore + 1;
            var batchDeadline = DateTime.UtcNow.AddSeconds(15);
            for (int poll = 0; poll < maxBatchPolls && DateTime.UtcNow < batchDeadline
                && db.Segments.SegmentCount < expectedMin; poll++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Thread.Sleep(50);
            }

            Assert.True(db.Segments.SegmentCount >= expectedMin,
                $"批次 {batch + 1}/{batchCount} 应在 15s 内产生新 Segment；期望至少 {expectedMin}，实际 {db.Segments.SegmentCount}");

            segmentsBefore = db.Segments.SegmentCount;
        }

        // 写入剩余点
        for (int i = written; i < totalPoints; i++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            db.Write(MakePoint(1000L + i, i));
        }

        // 断言至少产生了多个 Segment（每批一个，共 totalPoints/batchSize = 8 批）
        Assert.True(db.Segments.SegmentCount >= batchCount,
            $"期望 SegmentCount >= {batchCount}，实际 {db.Segments.SegmentCount}");

        // 主动再 Flush 一次，确保残余 MemTable 数据也落盘
        db.FlushNow();

        // 查询全部数据
        var seriesId = db.Catalog.Snapshot().First().Id;
        var query = new PointQuery(seriesId, "val", new TimeRange(0, long.MaxValue));
        var points = db.Query.Execute(query).ToList();

        Assert.Equal(totalPoints, points.Count);
        cancellation.Token.ThrowIfCancellationRequested();
        Assert.True(testClock.Elapsed < TimeSpan.FromSeconds(150), "连续写入测试超出总墙钟预算。");
    }

    /// <summary>
    /// 并发写入后，等待后台 Flush 完成，重新打开数据库查询正确。
    /// </summary>
    [Fact]
    public void AfterAutoFlush_Reopen_QueryIsCorrect()
    {
        const int totalPoints = 2000;

        {
            using var db = Tsdb.Open(MakeOptions());

            for (int i = 0; i < totalPoints; i++)
                db.Write(MakePoint(1000L + i, i));

            // 等待后台线程至少触发一次 Flush（最长 30s，Windows CI 可能较慢）
            var deadline = DateTime.UtcNow.AddSeconds(30);
            for (int poll = 0; poll < 300 && DateTime.UtcNow < deadline && db.Segments.SegmentCount < 1; poll++)
                Thread.Sleep(100);

            // 正常 Dispose（会再 Flush 剩余）
        }

        // 重新打开验证查询
        {
            using var db = Tsdb.Open(MakeOptions());

            var seriesId = db.Catalog.Snapshot().First().Id;
            var query = new PointQuery(seriesId, "val", new TimeRange(0, long.MaxValue));
            var points = db.Query.Execute(query).ToList();

            Assert.Equal(totalPoints, points.Count);
        }
    }
}
