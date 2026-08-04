using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Ingest;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Ingest;

public sealed class BulkIngestorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public BulkIngestorTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TsdbOptions Opts() => new()
    {
        RootDirectory = _tempDir,
        WalBufferSize = 64 * 1024,
        FlushPolicy = new MemTableFlushPolicy { MaxPoints = 1_000_000, MaxBytes = 64 * 1024 * 1024 },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
    };

    [Fact]
    public void Ingest_LineProtocol_AllPointsWritten()
    {
        using var db = Tsdb.Open(Opts());
        const string lp = "cpu,h=a v=1 1\ncpu,h=a v=2 2\ncpu,h=b v=3 3";
        var reader = new LineProtocolReader(lp.AsMemory());
        var result = BulkIngestor.Ingest(db, reader);
        Assert.Equal(3, result.Written);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(3L, db.MemTable.PointCount);
        Assert.Equal(2, db.Catalog.Count);
        var schema = db.Measurements.TryGet("cpu");
        Assert.NotNull(schema);
        Assert.Equal(MeasurementColumnRole.Tag, schema!.TryGetColumn("h")!.Role);
        Assert.Equal(MeasurementColumnRole.Field, schema.TryGetColumn("v")!.Role);
        Assert.Equal(FieldType.Float64, schema.TryGetColumn("v")!.DataType);
    }

    [Fact]
    public void Ingest_Json_AutoCreatesSchema()
    {
        using var db = Tsdb.Open(Opts());
        const string json = """
        {"m":"sensor","points":[
          {"t":1,"tags":{"host":"a"},"fields":{"value":1.5,"ok":true}}
        ]}
        """;
        using var reader = new JsonPointsReader(json.AsMemory());
        var result = BulkIngestor.Ingest(db, reader);

        Assert.Equal(1, result.Written);
        var schema = db.Measurements.TryGet("sensor");
        Assert.NotNull(schema);
        Assert.Equal(MeasurementColumnRole.Tag, schema!.TryGetColumn("host")!.Role);
        Assert.Equal(FieldType.Float64, schema.TryGetColumn("value")!.DataType);
        Assert.Equal(FieldType.Boolean, schema.TryGetColumn("ok")!.DataType);
    }

    [Fact]
    public void Ingest_FailFastOnBadField_Throws()
    {
        using var db = Tsdb.Open(Opts());
        const string lp = "cpu,h=a v=1 1\nbad-line-without-fields\n";
        var reader = new LineProtocolReader(lp.AsMemory());
        Assert.Throws<BulkIngestException>(() => BulkIngestor.Ingest(db, reader));
    }

    [Fact]
    public void Ingest_SkipPolicy_SkipsBadLines()
    {
        using var db = Tsdb.Open(Opts());
        // line 2 没有 fields → 抛 BulkIngestException → Skip 策略捕获
        const string lp = "cpu,h=a v=1 1\nbad-line-without-fields\ncpu,h=a v=3 3";
        var reader = new LineProtocolReader(lp.AsMemory());
        var result = BulkIngestor.Ingest(db, reader, BulkErrorPolicy.Skip);
        Assert.Equal(2, result.Written);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void Ingest_FlushOnComplete_ClearsMemTable()
    {
        using var db = Tsdb.Open(Opts());
        const string lp = "cpu,h=a v=1 1";
        var reader = new LineProtocolReader(lp.AsMemory());
        BulkIngestor.Ingest(db, reader, flushOnComplete: true);
        Assert.Equal(0L, db.MemTable.PointCount);
    }

    [Fact]
    public void Ingest_FlushSync_EquivalentToFlushOnCompleteTrue()
    {
        using var db = Tsdb.Open(Opts());
        const string lp = "cpu,h=a v=1 1\ncpu,h=a v=2 2";
        var reader = new LineProtocolReader(lp.AsMemory());
        var result = BulkIngestor.Ingest(db, reader, BulkErrorPolicy.FailFast, BulkFlushMode.Sync);
        Assert.Equal(2, result.Written);
        // Sync 档位等价于 FlushNow，MemTable 应被清空。
        Assert.Equal(0L, db.MemTable.PointCount);
    }

    [Fact]
    public void Ingest_FlushNone_KeepsPointsInMemTable()
    {
        using var db = Tsdb.Open(Opts());
        const string lp = "cpu,h=a v=3 3\ncpu,h=a v=4 4";
        var reader = new LineProtocolReader(lp.AsMemory());
        var result = BulkIngestor.Ingest(db, reader, BulkErrorPolicy.FailFast, BulkFlushMode.None);
        Assert.Equal(2, result.Written);
        // None：不触发 Flush，点仍在 MemTable。
        Assert.Equal(2L, db.MemTable.PointCount);
    }

    [Fact]
    public void Ingest_FlushAsync_DoesNotBlock()
    {
        // 异步档位：Ingest 调用立即返回（仅向后台 Flush 线程发信号），不阻塞等待落盘。
        // 后台线程是否真的执行 Flush 取决于 FlushPolicy 阈值（此处不做断言，由 BackgroundFlushWorker 测试覆盖）。
        using var db = Tsdb.Open(Opts());
        const string lp = "cpu,h=a v=5 5\ncpu,h=a v=6 6";
        var reader = new LineProtocolReader(lp.AsMemory());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = BulkIngestor.Ingest(db, reader, BulkErrorPolicy.FailFast, BulkFlushMode.Async);
        sw.Stop();
        Assert.Equal(2, result.Written);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"async flush should not block, took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Ingest_BatchBoundary_StillCorrect()
    {
        using var db = Tsdb.Open(Opts());
        // 制造 > BulkIngestor.BatchSize 行
        var sb = new System.Text.StringBuilder(BulkIngestor.BatchSize * 30);
        int n = BulkIngestor.BatchSize + 17;
        for (int i = 0; i < n; i++)
            sb.Append("cpu,h=a v=").Append(i).Append(' ').Append(i + 1).Append('\n');
        var reader = new LineProtocolReader(sb.ToString().AsMemory());
        var result = BulkIngestor.Ingest(db, reader);
        Assert.Equal(n, result.Written);
        Assert.Equal((long)n, db.MemTable.PointCount);
    }

    /// <summary>验证摄取已经进入 reader 后，下一轮循环会确定性响应取消且不提交未满批次。</summary>
    [Fact]
    public async Task Ingest_CancellationAfterReaderEntry_StopsBeforeBatchCommit()
    {
        using var db = Tsdb.Open(Opts());
        using var reader = new GatedPointReader();
        using var cancellation = new CancellationTokenSource();
        Task ingest = Task.Run(() => BulkIngestor.Ingest(
            db,
            reader,
            BulkErrorPolicy.FailFast,
            BulkFlushMode.None,
            cancellation.Token));
        try
        {
            await reader.WaitUntilEnteredAsync().WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            reader.Release();

            await Assert.ThrowsAsync<OperationCanceledException>(() => ingest)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0L, db.MemTable.PointCount);
        }
        finally
        {
            reader.Release();
        }
    }

    /// <summary>在首次读取中设置门闩的测试 reader，用于排除取消发生在摄取入口之前的假阳性。</summary>
    private sealed class GatedPointReader : IPointReader, IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(false);
        private bool _returned;

        /// <summary>等待摄取循环实际调用 reader。</summary>
        public Task WaitUntilEnteredAsync() => _entered.Task;

        /// <summary>允许 reader 返回首个点。</summary>
        public void Release() => _release.Set();

        /// <summary>首个点等待测试线程释放，后续读取结束。</summary>
        public bool TryRead(out Point point)
        {
            if (_returned)
            {
                point = null!;
                return false;
            }

            _entered.TrySetResult();
            Assert.True(_release.Wait(TimeSpan.FromSeconds(5)));
            _returned = true;
            point = Point.Create(
                "cpu",
                1,
                fields: new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromDouble(1) });
            return true;
        }

        /// <summary>释放测试门闩资源。</summary>
        public void Dispose() => _release.Dispose();
    }
}
