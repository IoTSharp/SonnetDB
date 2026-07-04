using System.Diagnostics;
using System.Diagnostics.Metrics;
using SonnetDB.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Diagnostics;

/// <summary>
/// 串行集合：<see cref="SonnetDbMeter"/> 是进程级单例，并行运行的其它测试类的写入/查询会污染
/// 本类的全局 MeterListener / ActivityListener 采样，必须与其它集合串行执行。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SonnetDbMeterSerialCollection
{
    /// <summary>集合名。</summary>
    public const string Name = "SonnetDbMeterSerial";
}

/// <summary>
/// M17 #89：<see cref="SonnetDbMeter"/> / <see cref="SonnetDbActivitySource"/> 基线插桩测试。
/// 通过 BCL <see cref="MeterListener"/> / <see cref="ActivityListener"/> 订阅，
/// 不依赖 OpenTelemetry SDK。
/// </summary>
[Collection(SonnetDbMeterSerialCollection.Name)]
public sealed class SonnetDbMeterTests : IDisposable
{
    private readonly string _tempDir;

    public SonnetDbMeterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TsdbOptions MakeOptions(bool syncWal = false) =>
        new()
        {
            RootDirectory = _tempDir,
            SyncWalOnEveryWrite = syncWal,
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxPoints = 10_000_000,
                MaxBytes = 1024L * 1024 * 1024,
                MaxAge = TimeSpan.FromHours(24),
            },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Retention = new RetentionPolicy { Enabled = false },
        };

    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly object _sync = new();
        private readonly Dictionary<string, long> _longSums = new();
        private readonly Dictionary<string, List<(double Value, KeyValuePair<string, object?>[] Tags)>> _doubles = new();

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SonnetDbMeter.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
            {
                lock (_sync)
                {
                    _longSums[inst.Name] = _longSums.GetValueOrDefault(inst.Name) + value;
                }
            });
            _listener.SetMeasurementEventCallback<double>((inst, value, tags, _) =>
            {
                lock (_sync)
                {
                    if (!_doubles.TryGetValue(inst.Name, out var list))
                        _doubles[inst.Name] = list = new List<(double, KeyValuePair<string, object?>[])>();
                    list.Add((value, tags.ToArray()));
                }
            });
            _listener.Start();
        }

        public long LongSum(string name)
        {
            lock (_sync)
                return _longSums.GetValueOrDefault(name);
        }

        public IReadOnlyList<(double Value, KeyValuePair<string, object?>[] Tags)> Doubles(string name)
        {
            lock (_sync)
                return _doubles.TryGetValue(name, out var list) ? list.ToArray() : Array.Empty<(double, KeyValuePair<string, object?>[])>();
        }

        public void RecordObservable() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public void Write_RecordsPointCounterAndDuration()
    {
        using var collector = new MetricCollector();
        using var db = Tsdb.Open(MakeOptions());
        db.Write(CreatePoint(0));
        db.Write(CreatePoint(1));

        Assert.Equal(2, collector.LongSum("sonnetdb.write.points"));
        Assert.Equal(2, collector.Doubles("sonnetdb.write.duration").Count);
    }

    [Fact]
    public void WriteMany_RecordsPointCounterPerChunk()
    {
        using var collector = new MetricCollector();
        using var db = Tsdb.Open(MakeOptions());
        var points = Enumerable.Range(0, 100).Select(CreatePoint).ToArray();

        db.WriteMany(points);

        Assert.Equal(100, collector.LongSum("sonnetdb.write.points"));
        Assert.True(collector.Doubles("sonnetdb.write.duration").Count >= 1);
    }

    [Fact]
    public void Flush_RecordsDurationPointsAndBytes()
    {
        using var collector = new MetricCollector();
        using var db = Tsdb.Open(MakeOptions());
        db.WriteMany(Enumerable.Range(0, 50).Select(CreatePoint).ToArray());

        var result = db.FlushNow();

        Assert.NotNull(result);
        var flushDurations = collector.Doubles("sonnetdb.flush.duration");
        Assert.Single(flushDurations);
        Assert.Contains(flushDurations[0].Tags, t => t.Key == "outcome" && (string?)t.Value == "ok");
        Assert.Equal(50, collector.LongSum("sonnetdb.flush.points"));
        Assert.Equal(result!.TotalBytes, collector.LongSum("sonnetdb.flush.bytes"));
    }

    [Fact]
    public void WalFsync_RecordedWhenSyncWalOnEveryWrite()
    {
        using var collector = new MetricCollector();
        using var db = Tsdb.Open(MakeOptions(syncWal: true));
        db.Write(CreatePoint(0));

        Assert.True(collector.Doubles("sonnetdb.wal.fsync.duration").Count >= 1);
    }

    [Fact]
    public void PointQuery_RecordsDurationWithOperationTag()
    {
        using var db = Tsdb.Open(MakeOptions());
        db.WriteMany(Enumerable.Range(0, 20).Select(CreatePoint).ToArray());
        ulong seriesId = db.Catalog.Snapshot().Single().Id;

        using var collector = new MetricCollector();
        var points = db.Query.Execute(new PointQuery(seriesId, "value", TimeRange.All)).ToList();

        Assert.Equal(20, points.Count);
        var durations = collector.Doubles("sonnetdb.query.duration");
        Assert.Single(durations);
        Assert.Contains(durations[0].Tags, t => t.Key == "db.operation" && (string?)t.Value == "points");
    }

    [Fact]
    public void PointQuery_EarlyBreak_StillRecordsDuration()
    {
        using var db = Tsdb.Open(MakeOptions());
        db.WriteMany(Enumerable.Range(0, 20).Select(CreatePoint).ToArray());
        ulong seriesId = db.Catalog.Snapshot().Single().Id;

        using var collector = new MetricCollector();
        foreach (var _ in db.Query.Execute(new PointQuery(seriesId, "value", TimeRange.All)))
            break;

        Assert.Single(collector.Doubles("sonnetdb.query.duration"));
    }

    [Fact]
    public void AggregateQuery_RecordsDurationWithOperationTag()
    {
        using var db = Tsdb.Open(MakeOptions());
        db.WriteMany(Enumerable.Range(0, 20).Select(CreatePoint).ToArray());
        ulong seriesId = db.Catalog.Snapshot().Single().Id;

        using var collector = new MetricCollector();
        var buckets = db.Query.Execute(
            new AggregateQuery(seriesId, "value", TimeRange.All, Aggregator.Count, 0)).ToList();

        Assert.NotEmpty(buckets);
        var durations = collector.Doubles("sonnetdb.query.duration");
        Assert.Single(durations);
        Assert.Contains(durations[0].Tags, t => t.Key == "db.operation" && (string?)t.Value == "aggregate");
    }

    [Fact]
    public void SegmentRead_CountsPhysicalBlockReads()
    {
        using var db = Tsdb.Open(MakeOptions());
        db.WriteMany(Enumerable.Range(0, 50).Select(CreatePoint).ToArray());
        db.FlushNow();
        ulong seriesId = db.Catalog.Snapshot().Single().Id;

        using var collector = new MetricCollector();
        _ = db.Query.Execute(new PointQuery(seriesId, "value", TimeRange.All)).ToList();

        Assert.True(collector.LongSum("sonnetdb.segment.block.reads") >= 1);
        Assert.True(collector.LongSum("sonnetdb.segment.block.read.bytes") > 0);
    }

    [Fact]
    public void ObservableGauges_ReportPerDatabaseState()
    {
        using var collector = new MetricCollector();
        using var db = Tsdb.Open(MakeOptions());
        db.WriteMany(Enumerable.Range(0, 30).Select(CreatePoint).ToArray());

        var listener = new MeterListener();
        var gauges = new Dictionary<string, List<(long Value, string? Db)>>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == SonnetDbMeter.MeterName && instrument is ObservableInstrument<long>)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            string? dbTag = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "sonnetdb.database")
                    dbTag = tag.Value as string;
            }
            if (!gauges.TryGetValue(inst.Name, out var list))
                gauges[inst.Name] = list = new List<(long, string?)>();
            list.Add((value, dbTag));
        });
        listener.Start();
        listener.RecordObservableInstruments();
        listener.Dispose();

        string expectedDb = Path.GetFileName(_tempDir);
        Assert.Contains(gauges["sonnetdb.memtable.points"], m => m.Value == 30 && m.Db == expectedDb);
        Assert.Contains(gauges["sonnetdb.memtable.bytes"], m => m.Value > 0 && m.Db == expectedDb);
        Assert.Contains(gauges["sonnetdb.segments.count"], m => m.Db == expectedDb);
    }

    [Fact]
    public void ObservableGauges_DisposedEngineNoLongerReported()
    {
        var db = Tsdb.Open(MakeOptions());
        db.Write(CreatePoint(0));
        string expectedDb = Path.GetFileName(_tempDir);
        db.Dispose();

        var listener = new MeterListener();
        var seen = new List<string?>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == SonnetDbMeter.MeterName && instrument.Name == "sonnetdb.memtable.points")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "sonnetdb.database")
                    seen.Add(tag.Value as string);
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        listener.Dispose();

        Assert.DoesNotContain(expectedDb, seen);
    }

    [Fact]
    public void FlushActivity_EmitsSpanWithSemanticTags()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SonnetDbActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (activities)
                    activities.Add(a);
            },
        };
        ActivitySource.AddActivityListener(listener);

        using (var db = Tsdb.Open(MakeOptions()))
        {
            db.WriteMany(Enumerable.Range(0, 10).Select(CreatePoint).ToArray());
            db.FlushNow();
        }

        Activity flush;
        lock (activities)
            flush = Assert.Single(activities, a => a.OperationName == "sonnetdb.flush");
        Assert.Equal("sonnetdb", flush.GetTagItem("db.system"));
        Assert.Equal("flush", flush.GetTagItem("db.operation"));
        Assert.NotNull(flush.GetTagItem("sonnetdb.segment.id"));
    }

    [Fact]
    public void QueryActivity_EmitsPointsSpan()
    {
        using var db = Tsdb.Open(MakeOptions());
        db.WriteMany(Enumerable.Range(0, 10).Select(CreatePoint).ToArray());
        ulong seriesId = db.Catalog.Snapshot().Single().Id;

        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SonnetDbActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (activities)
                    activities.Add(a);
            },
        };
        ActivitySource.AddActivityListener(listener);

        _ = db.Query.Execute(new PointQuery(seriesId, "value", TimeRange.All)).ToList();

        Activity span;
        lock (activities)
            span = Assert.Single(activities, a => a.OperationName == "sonnetdb.query.points");
        Assert.Equal("sonnetdb", span.GetTagItem("db.system"));
        Assert.Equal("points", span.GetTagItem("db.operation"));
    }

    private static Point CreatePoint(int index)
        => Point.Create(
            "metric",
            1_700_000_000_000L + index,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromDouble(index) });
}
