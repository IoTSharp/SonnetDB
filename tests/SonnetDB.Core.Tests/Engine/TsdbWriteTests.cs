using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// <see cref="Tsdb"/> 写入路径的单元测试。
/// </summary>
public sealed class TsdbWriteTests : IDisposable
{
    private readonly string _tempDir;

    public TsdbWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TsdbOptions MakeOptions(MemTableFlushPolicy? flushPolicy = null) =>
        new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            FlushPolicy = flushPolicy ?? new MemTableFlushPolicy { MaxPoints = 1_000_000, MaxBytes = 64 * 1024 * 1024 },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        };

    [Fact]
    public void Open_EmptyDirectory_CreatesExpectedStructure()
    {
        using var db = Tsdb.Open(MakeOptions());

        Assert.True(Directory.Exists(TsdbPaths.WalDir(_tempDir)));
        Assert.True(Directory.Exists(TsdbPaths.SegmentsDir(_tempDir)));
        // 新模型：WAL 以 segment 文件存在，wal/ 目录中应有至少一个 .SDBWAL 文件
        var walSegments = WalSegmentLayout.Enumerate(TsdbPaths.WalDir(_tempDir));
        Assert.NotEmpty(walSegments);
        Assert.Equal(0, db.Catalog.Count);
        Assert.Equal(0L, db.MemTable.PointCount);
    }

    [Fact]
    public void Write_SinglePoint_CatalogAndMemTableUpdated()
    {
        using var db = Tsdb.Open(MakeOptions());

        var point = Point.Create("cpu", 1000L,
            new Dictionary<string, string> { ["host"] = "srv1" },
            new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(75.0) });

        db.Write(point);

        Assert.Equal(1, db.Catalog.Count);
        Assert.Equal(1L, db.MemTable.PointCount);
        // 新模型：WAL 以 segment 文件存在
        var walSegments = WalSegmentLayout.Enumerate(TsdbPaths.WalDir(_tempDir));
        Assert.NotEmpty(walSegments);
    }

    [Fact]
    public void Write_SinglePoint_AutoCreatesAndPersistsMeasurementSchema()
    {
        using var db = Tsdb.Open(MakeOptions());

        var point = Point.Create("cpu", 1000L,
            new Dictionary<string, string> { ["host"] = "srv1" },
            new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(75.0) });

        db.Write(point);

        var schema = db.Measurements.TryGet("cpu");
        Assert.NotNull(schema);
        Assert.Equal(MeasurementColumnRole.Tag, schema!.TryGetColumn("host")!.Role);
        Assert.Equal(MeasurementColumnRole.Field, schema.TryGetColumn("usage")!.Role);
        Assert.True(File.Exists(TsdbPaths.MeasurementSchemaPath(_tempDir)));
    }

    [Fact]
    public void Write_CrashAfterAutoSchema_WalReplayKeepsSchemaVisible()
    {
        var point = Point.Create("cpu", 1000L,
            new Dictionary<string, string> { ["host"] = "srv1" },
            new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(75.0) });

        var db = Tsdb.Open(MakeOptions());
        db.Write(point);
        db.CrashSimulationCloseWal();

        using var reopened = Tsdb.Open(MakeOptions());
        var schema = reopened.Measurements.TryGet("cpu");
        Assert.NotNull(schema);
        Assert.NotNull(schema!.TryGetColumn("host"));
        Assert.NotNull(schema.TryGetColumn("usage"));
    }

    [Fact]
    public void Write_IntFieldThenFloatValue_PromotesSchemaAndKeepsQueryReadable()
    {
        using var db = Tsdb.Open(MakeOptions());

        db.Write(Point.Create("cpu", 1,
            new Dictionary<string, string> { ["host"] = "srv1" },
            new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromLong(1) }));
        db.Write(Point.Create("cpu", 2,
            new Dictionary<string, string> { ["host"] = "srv1" },
            new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(1.5) }));

        var schema = db.Measurements.TryGet("cpu")!;
        Assert.Equal(FieldType.Float64, schema.TryGetColumn("usage")!.DataType);

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>
        {
            ["host"] = "srv1",
        }));
        var points = db.Query.Execute(new PointQuery(seriesId, "usage",
            new TimeRange(0, long.MaxValue))).ToList();
        Assert.Equal(2, points.Count);
        Assert.Equal(FieldType.Int64, points[0].Value.Type);
        Assert.Equal(FieldType.Float64, points[1].Value.Type);
    }

    [Fact]
    public void Write_MultiplePoints_MemTableCountsCorrect()
    {
        using var db = Tsdb.Open(MakeOptions());

        for (int i = 0; i < 10; i++)
        {
            var point = Point.Create("sensor", 1000L + i,
                new Dictionary<string, string> { ["id"] = "s1" },
                new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromDouble(i) });
            db.Write(point);
        }

        Assert.Equal(10L, db.MemTable.PointCount);
        Assert.Equal(1, db.Catalog.Count);
    }

    [Fact]
    public void WriteMany_NPoints_MemTablePointCountEquals_N()
    {
        using var db = Tsdb.Open(MakeOptions());

        var points = Enumerable.Range(0, 20).Select(i => Point.Create(
            "metric", 1000L + i,
            new Dictionary<string, string> { ["host"] = "h" },
            new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(i) }));

        int written = db.WriteMany(points);

        Assert.Equal(20, written);
        Assert.Equal(20L, db.MemTable.PointCount);
    }

    [Fact]
    public void Write_WithHardCapBytesExceeded_ForcesSynchronousFlush()
    {
        using var db = Tsdb.Open(MakeOptions(new MemTableFlushPolicy
        {
            MaxBytes = long.MaxValue,
            HardCapBytes = 1,
            MaxPoints = long.MaxValue,
            MaxAge = TimeSpan.MaxValue,
        }));

        db.Write(Point.Create("metric", 1000L,
            new Dictionary<string, string> { ["host"] = "h" },
            new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(1.0) }));

        Assert.Equal(0L, db.MemTable.PointCount);
        Assert.Equal(1, db.Segments.SegmentCount);
    }

    [Fact]
    public void WriteMany_LargeBatchWithHardCap_FlushesMidBatchAndBoundsMemTable()
    {
        // 硬上限极小：每块（8192 点）写完后都应触发同步 flush，MemTable 不会累积整批。
        using var db = Tsdb.Open(MakeOptions(new MemTableFlushPolicy
        {
            MaxBytes = long.MaxValue,
            HardCapBytes = 1,
            MaxPoints = long.MaxValue,
            MaxAge = TimeSpan.MaxValue,
        }));

        // 超过一个 chunk（8192）以确保批内多次分块 + 多次 flush。
        const int total = 8192 * 3 + 100;
        var points = new Point[total];
        for (int i = 0; i < total; i++)
        {
            points[i] = Point.Create("metric", 1000L + i,
                new Dictionary<string, string> { ["host"] = "h" },
                new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(i) });
        }

        int written = db.WriteMany(points);

        Assert.Equal(total, written);
        // 每块结束都因 HardCapBytes=1 被 seal+flush，批末 MemTable 应为空。
        Assert.Equal(0L, db.MemTable.PointCount);
        // 分块数（至少 4 块）各自 flush，段数应远大于 1（证明批内多次 flush，而非批末一次）。
        Assert.True(db.Segments.SegmentCount >= 4,
            $"期望批内多次分块 flush 产生 >= 4 段，实际 {db.Segments.SegmentCount}。");
    }

    [Fact]
    public void WriteMany_WithNewFieldsAndTags_PersistsMeasurementSchemaOnce()
    {
        using var db = Tsdb.Open(MakeOptions());

        Point[] points =
        [
            Point.Create("metric", 1000L,
                new Dictionary<string, string> { ["host"] = "h1" },
                new Dictionary<string, FieldValue> { ["f1"] = FieldValue.FromDouble(1.0) }),
            Point.Create("metric", 1001L,
                new Dictionary<string, string> { ["host"] = "h1", ["rack"] = "r1" },
                new Dictionary<string, FieldValue> { ["f1"] = FieldValue.FromDouble(2.0), ["f2"] = FieldValue.FromLong(2L) }),
            Point.Create("metric", 1002L,
                new Dictionary<string, string> { ["host"] = "h2", ["zone"] = "z1" },
                new Dictionary<string, FieldValue> { ["f3"] = FieldValue.FromBool(true) }),
        ];

        int written = db.WriteMany(points);

        Assert.Equal(3, written);
        Assert.Equal(1L, db.MeasurementSchemaPersistCount);

        var schema = Assert.Single(MeasurementSchemaCodec.Load(TsdbPaths.MeasurementSchemaPath(_tempDir)));
        Assert.NotNull(schema.TryGetColumn("host"));
        Assert.NotNull(schema.TryGetColumn("rack"));
        Assert.NotNull(schema.TryGetColumn("zone"));
        Assert.NotNull(schema.TryGetColumn("f1"));
        Assert.NotNull(schema.TryGetColumn("f2"));
        Assert.NotNull(schema.TryGetColumn("f3"));
    }

    [Fact]
    public void WriteMany_IntThenFloatInSameBatch_PromotesSchemaAndPersistsOnce()
    {
        using var db = Tsdb.Open(MakeOptions());

        Point[] points =
        [
            Point.Create("cpu", 1L,
                new Dictionary<string, string> { ["host"] = "srv1" },
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromLong(1L) }),
            Point.Create("cpu", 2L,
                new Dictionary<string, string> { ["host"] = "srv1" },
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(1.5) }),
        ];

        int written = db.WriteMany(points);

        Assert.Equal(2, written);
        Assert.Equal(1L, db.MeasurementSchemaPersistCount);

        var schema = db.Measurements.TryGet("cpu")!;
        Assert.Equal(FieldType.Float64, schema.TryGetColumn("usage")!.DataType);
        var persisted = Assert.Single(MeasurementSchemaCodec.Load(TsdbPaths.MeasurementSchemaPath(_tempDir)));
        Assert.Equal(FieldType.Float64, persisted.TryGetColumn("usage")!.DataType);

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>
        {
            ["host"] = "srv1",
        }));
        var queried = db.Query.Execute(new PointQuery(seriesId, "usage", TimeRange.All)).ToList();
        Assert.Equal(2, queried.Count);
    }

    [Fact]
    public void FlushNow_EmptyMemTable_ReturnsNull()
    {
        using var db = Tsdb.Open(MakeOptions());
        var result = db.FlushNow();
        Assert.Null(result);
    }

    [Fact]
    public void FlushNow_AfterWrite_CreatesSegmentAndClearsMemTable()
    {
        using var db = Tsdb.Open(MakeOptions());

        for (int i = 0; i < 5; i++)
        {
            var p = Point.Create("temp", 1000L + i,
                new Dictionary<string, string> { ["loc"] = "lab" },
                new Dictionary<string, FieldValue> { ["c"] = FieldValue.FromDouble(20.0 + i) });
            db.Write(p);
        }

        Assert.Equal(5L, db.MemTable.PointCount);

        var result = db.FlushNow();

        Assert.NotNull(result);
        Assert.Equal(0L, db.MemTable.PointCount);
        Assert.Equal(0, db.MemTable.SeriesCount);

        // Segment 文件存在
        var segments = db.ListSegments();
        Assert.Single(segments);
        Assert.True(File.Exists(segments[0].Path));

        // Flush 在回收旧 WAL 前会持久化 catalog checkpoint
        Assert.True(File.Exists(TsdbPaths.CatalogPath(_tempDir)));
    }

    [Fact]
    public void FlushNow_SegmentId_IsMonotonicallyIncreasing()
    {
        using var db = Tsdb.Open(MakeOptions());

        for (int flush = 0; flush < 3; flush++)
        {
            for (int i = 0; i < 3; i++)
            {
                var p = Point.Create("m", 1000L + flush * 100 + i,
                    new Dictionary<string, string> { ["k"] = "v" },
                    new Dictionary<string, FieldValue> { ["f"] = FieldValue.FromDouble(i) });
                db.Write(p);
            }
            db.FlushNow();
        }

        var segs = db.ListSegments();
        Assert.Equal(3, segs.Count);
        Assert.Equal(1L, segs[0].SegmentId);
        Assert.Equal(2L, segs[1].SegmentId);
        Assert.Equal(3L, segs[2].SegmentId);
    }

    [Fact]
    public void Dispose_SavesCatalog_ClosesWal()
    {
        var p = Point.Create("cpu", 1000L,
            new Dictionary<string, string> { ["host"] = "h" },
            new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(1.0) });

        using (var db = Tsdb.Open(MakeOptions()))
        {
            db.Write(p);
        }

        // Catalog 应已保存
        Assert.True(File.Exists(TsdbPaths.CatalogPath(_tempDir)));
    }

    [Fact]
    public void Dispose_FinalFlushFailure_RecordsDiagnosticAndDoesNotThrow()
    {
        var expected = new IOException("final flush boom");
        var options = new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxPoints = 1_000_000,
                MaxBytes = 64 * 1024 * 1024,
            },
            SegmentWriterOptions = new SegmentWriterOptions
            {
                FsyncOnCommit = false,
                FailAt = _ => throw expected,
            },
        };

        var db = Tsdb.Open(options);
        TsdbDiagnosticEvent? diagnostic = null;
        db.DiagnosticEvent += (_, e) => diagnostic = e;
        db.DiagnosticEvent += (_, _) => throw new InvalidOperationException("subscriber failure");

        db.Write(Point.Create("cpu", 1000L,
            new Dictionary<string, string> { ["host"] = "h" },
            new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(1.0) }));

        var disposeException = Record.Exception(() => db.Dispose());

        Assert.Null(disposeException);
        Assert.Same(expected, db.LastError);
        Assert.NotNull(diagnostic);
        Assert.Equal("Dispose.FinalFlush", diagnostic!.Operation);
        Assert.Equal(TsdbDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Same(expected, diagnostic.Exception);
        Assert.True(
            diagnostic.Message.Contains("final flush", StringComparison.OrdinalIgnoreCase),
            diagnostic.Message);
    }

    [Fact]
    public void Dispose_ThenOpen_CatalogAndSegmentsPreserved()
    {
        // First session: write + flush
        using (var db = Tsdb.Open(MakeOptions()))
        {
            for (int i = 0; i < 5; i++)
            {
                var p = Point.Create("m", 1000L + i,
                    new Dictionary<string, string> { ["k"] = "v" },
                    new Dictionary<string, FieldValue> { ["f"] = FieldValue.FromDouble(i) });
                db.Write(p);
            }
            db.FlushNow();
        }

        // Second session: verify state is preserved
        using (var db = Tsdb.Open(MakeOptions()))
        {
            Assert.Equal(1, db.Catalog.Count);

            var segs = db.ListSegments();
            Assert.Single(segs);

            // MemTable should be empty (all data flushed)
            Assert.Equal(0L, db.MemTable.PointCount);
        }
    }

    [Fact]
    public void Write_NullPoint_ThrowsArgumentNull()
    {
        using var db = Tsdb.Open(MakeOptions());
        Assert.Throws<ArgumentNullException>(() => db.Write(null!));
    }

    [Fact]
    public void ListSegments_ReturnsSegmentsInAscendingOrder()
    {
        using var db = Tsdb.Open(MakeOptions());

        // Flush 3 times
        for (int flush = 0; flush < 3; flush++)
        {
            var p = Point.Create("m", 1000L + flush,
                new Dictionary<string, string> { ["i"] = flush.ToString() },
                new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(flush) });
            db.Write(p);
            db.FlushNow();
        }

        var segs = db.ListSegments();
        for (int i = 1; i < segs.Count; i++)
            Assert.True(segs[i].SegmentId > segs[i - 1].SegmentId);
    }

    [Fact]
    public void Open_WithExistingSegments_NextSegmentIdIsMaxPlusOne()
    {
        // Create fake segment files to simulate existing segments
        Directory.CreateDirectory(TsdbPaths.SegmentsDir(_tempDir));
        Directory.CreateDirectory(Path.GetDirectoryName(TsdbPaths.SegmentPath(_tempDir, 3L))!);
        Directory.CreateDirectory(Path.GetDirectoryName(TsdbPaths.SegmentPath(_tempDir, 7L))!);
        File.WriteAllBytes(TsdbPaths.SegmentPath(_tempDir, 3L), []);
        File.WriteAllBytes(TsdbPaths.SegmentPath(_tempDir, 7L), []);

        using var db = Tsdb.Open(MakeOptions());
        Assert.Equal(8L, db.NextSegmentId);
    }

    [Fact]
    public void Open_WithLayeredSegments_LoadsExistingDataAndAllocatesNextId()
    {
        {
            using var db = Tsdb.Open(MakeOptions());
            db.Write(Point.Create(
                "metrics",
                1000L,
                new Dictionary<string, string> { ["host"] = "srv1" },
                new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromDouble(1.25) }));
            var flush = db.FlushNow();
            Assert.NotNull(flush);
            Assert.Contains(Path.Combine("segments", "v2"), flush.Path);
        }

        using var reopened = Tsdb.Open(MakeOptions());
        Assert.Equal(2L, reopened.NextSegmentId);
        var seriesId = reopened.Catalog.Snapshot().Single().Id;
        var points = reopened.Query.Execute(new PointQuery(
            seriesId,
            "value",
            new TimeRange(0, long.MaxValue))).ToList();

        var point = Assert.Single(points);
        Assert.Equal(1000L, point.Timestamp);
        Assert.Equal(1.25, point.Value.AsDouble());
    }
}
