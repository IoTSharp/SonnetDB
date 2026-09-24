using System.Text.Json;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;

namespace SonnetDB.Core.Tests.Query;

/// <summary>真实时序目录、配置和有界数据检查的合同回归。</summary>
public sealed class TimeSeriesPreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    private TsdbOptions Options => new()
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Preflight_ActualMeasurementCatalog_ReportsMultipleSeriesAndTagCounts()
    {
        using var database = Tsdb.Open(Options);
        Declare(database, FieldType.Float64);
        ulong id = Write(database, 10, 1, "a");
        Write(database, 20, 2, "b");
        Write(database, 30, 3, "b");
        database.Catalog.GetOrAdd("unrelated", new Dictionary<string, string> { ["host"] = "c" });

        var result = database.ForSeries(id, "value").Between(0, 100).Preflight(new()
        {
            SeriesWarningThreshold = 2,
            TagValueWarningThreshold = 2,
        });

        Assert.Equal("sensor", result.Measurement);
        Assert.True(result.HasSchema);
        Assert.Equal(FieldType.Float64, result.DeclaredFieldType);
        Assert.Equal(2, result.MeasurementSeriesCount);
        Assert.Equal(new TimeSeriesTagCardinality("host", 2), Assert.Single(result.Tags));
        Assert.Equal(1, result.SampledPoints);
        Assert.True(result.SampleComplete);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP005");
        Assert.Contains(result.Issues, issue => issue.Code == "TSP006");
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Preflight_TooManyTagKeys_ReportsExplicitlyPartialCardinality()
    {
        using var database = Tsdb.Open(Options);
        var entry = database.Catalog.GetOrAdd("sensor", new Dictionary<string, string>
        {
            ["host"] = "a",
            ["region"] = "east",
            ["device"] = "one",
        });
        var result = database.ForSeries(entry.Id, "value").Preflight(new() { MaxTagKeys = 1 });
        Assert.Single(result.Tags);
        Assert.True(result.TagsTruncated);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP007");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("host")]
    public void Preflight_NonFieldSchemaName_ReportsSchemaError(string field)
    {
        using var database = Tsdb.Open(Options);
        Declare(database, FieldType.Float64);
        ulong id = Write(database, 10, 1);
        var result = database.ForSeries(id, field).Preflight();
        Assert.Null(result.DeclaredFieldType);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP003");
    }

    [Fact]
    public void Preflight_StringAggregate_UsesDeclaredAndObservedTypesButAllowsCount()
    {
        using var database = Tsdb.Open(Options);
        Declare(database, FieldType.String);
        var point = Point.Create("sensor", 1, new Dictionary<string, string> { ["host"] = "a" },
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromString("ok") });
        database.Write(point);
        ulong id = database.Catalog.GetOrAdd(point).Id;
        var sum = database.ForSeries(id, "value").Aggregate(Aggregator.Sum).Preflight();
        Assert.Contains(sum.Issues, issue => issue.Code == "TSP004");
        Assert.Equal([FieldType.String], sum.ObservedFieldTypes);
        var count = database.ForSeries(id, "value").Aggregate(Aggregator.Count).Preflight();
        Assert.False(count.HasErrors);
    }

    [Fact]
    public void Preflight_LowLevelDataContradictsSchema_ReportsActualMismatch()
    {
        using var database = Tsdb.Open(Options);
        Declare(database, FieldType.Float64);
        ulong id = database.Catalog.GetOrAdd("sensor", null).Id;
        // 构造来自底层装载的不兼容真实内存点，避免将正常写入校验误当成可绕过。
        database.MemTable.Append(id, 1, "value", FieldValue.FromString("wrong type"), 1);
        var result = database.ForSeries(id, "value").Preflight();
        Assert.Equal(1, result.SchemaMismatchPoints);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP014");
    }

    [Fact]
    public void Preflight_MeasurementPromotedByOtherSeries_AcceptsHistoricalIntegerPoints()
    {
        using var database = Tsdb.Open(Options);
        var integerPoint = Point.Create("sensor", 1, new Dictionary<string, string> { ["host"] = "integer" },
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromLong(42) });
        database.Write(integerPoint);
        ulong id = database.Catalog.GetOrAdd(integerPoint).Id;
        database.FlushNow();
        Write(database, 2, 1.5, "floating");
        var result = database.ForSeries(id, "value").Aggregate(Aggregator.Avg).Preflight();
        Assert.Equal(FieldType.Float64, result.DeclaredFieldType);
        Assert.Equal([FieldType.Int64], result.ObservedFieldTypes);
        Assert.Equal(2, result.MeasurementSeriesCount);
        Assert.Equal(0, result.SchemaMismatchPoints);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Preflight_RetentionUsesActualUnitsAndClock_ReportsExclusiveCutoffWithoutDeleting()
    {
        int clockReads = 0;
        using var database = Tsdb.Open(Options with
        {
            Retention = new RetentionPolicy
            {
                Enabled = true,
                Ttl = TimeSpan.FromDays(1),
                TtlInTimestampUnits = 20,
                PollInterval = TimeSpan.FromHours(1),
                NowFn = () => { clockReads++; return 100; },
            },
        });
        ulong id = Write(database, 79, 1);
        Write(database, 80, 2);
        Write(database, 100, 3);
        var result = database.ForSeries(id, "value").Between(79, 100).Preflight();
        Assert.Equal(1, clockReads);
        Assert.True(result.RetentionEnabled);
        Assert.Equal(20, result.RetentionTtl);
        Assert.Equal(100, result.RetentionNow);
        Assert.Equal(80, result.RetentionCutoff);
        Assert.Equal(1, result.ExpiredPoints);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP009");
        Assert.Contains(result.Issues, issue => issue.Code == "TSP015");
        Assert.Equal(0, database.Tombstones.Count);
        Assert.Equal(3, database.ForSeries(id, "value").ReadPoints().Count());
    }

    [Fact]
    public void Preflight_DisabledRetention_DoesNotInvokeClockOrInventCutoff()
    {
        using var database = Tsdb.Open(Options with
        {
            Retention = new RetentionPolicy { Enabled = false, NowFn = () => throw new InvalidOperationException() },
        });
        ulong id = Write(database, 0, 1);
        var result = database.ForSeries(id, "value").Preflight();
        Assert.False(result.RetentionEnabled);
        Assert.Null(result.RetentionCutoff);
        Assert.Equal(0, result.ExpiredPoints);
    }

    [Theory]
    [InlineData(long.MinValue, 1)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    public void Preflight_InvalidRetentionArithmetic_ReportsUnknownCutoff(long now, long ttl)
    {
        using var database = Tsdb.Open(Options with
        {
            Retention = new RetentionPolicy
            {
                Enabled = true,
                TtlInTimestampUnits = ttl,
                NowFn = () => now,
                PollInterval = TimeSpan.FromHours(1),
            },
        });
        ulong id = Write(database, 1, 1);
        var result = database.ForSeries(id, "value").Preflight();
        Assert.Null(result.RetentionCutoff);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP008");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preflight_LimitedSample_OnlyClaimsInspectedPrefix(bool descending)
    {
        using var database = Tsdb.Open(Options);
        ulong id = Write(database, 1, 1);
        Write(database, 2, double.NaN);
        Write(database, 3, double.PositiveInfinity);
        var builder = database.ForSeries(id, "value").Limit(1);
        if (descending) builder.Descending();
        var result = builder.Preflight(new() { MaxSamplePoints = 2 });
        Assert.True(result.DataChecked);
        Assert.False(result.SampleComplete);
        Assert.Equal(2, result.SampledPoints);
        Assert.Equal(descending ? 2 : 1, result.NonFinitePoints);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP012");
        Assert.Contains(result.Issues, issue => issue.Code == "TSP013");
    }

    [Fact]
    public void Preflight_ExactSampleLimit_StillRecognizesCompleteRange()
    {
        using var database = Tsdb.Open(Options);
        ulong id = Write(database, long.MaxValue, 1);
        var result = database.ForSeries(id, "value").Between(long.MaxValue, long.MaxValue)
            .Preflight(new() { MaxSamplePoints = 1 });
        Assert.True(result.SampleComplete);
        Assert.Equal(1, result.SampledPoints);
    }

    [Fact]
    public void Preflight_UnsortedMemTableExceedsSourceBudget_SkipsBeforeSortingWholeBucket()
    {
        using var database = Tsdb.Open(Options);
        ulong id = Write(database, 100, 1);
        Write(database, 0, 2);
        var result = database.ForSeries(id, "value").Between(0, 0)
            .Preflight(new() { MaxSamplePoints = 1, MaxSourcePoints = 1 });
        Assert.False(result.DataChecked);
        Assert.Equal(0, result.SampledPoints);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP010");
    }

    [Fact]
    public void Preflight_NarrowPersistedRange_ChargesFullBlockBeforeDecoding()
    {
        using var database = Tsdb.Open(Options);
        ulong id = Write(database, 1, 1);
        Write(database, 2, 2);
        database.FlushNow();
        var result = database.ForSeries(id, "value").Between(1, 1).Preflight(new() { MaxSourcePoints = 1 });
        Assert.False(result.DataChecked);
        Assert.Equal(0, result.SampledPoints);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP010");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preflight_SourceByteBudget_RejectsMemoryAndPersistedBlocks(bool flush)
    {
        using var database = Tsdb.Open(Options);
        ulong id = Write(database, 1, 1);
        if (flush) database.FlushNow();
        var result = database.ForSeries(id, "value").Preflight(new() { MaxSourceBytes = 1 });
        Assert.False(result.DataChecked);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP010");
    }

    [Fact]
    public void Preflight_TooManyLoadedSegments_RejectsBeforeReaderLeaseLoopAndReleasesAdmission()
    {
        using (var database = Tsdb.Open(Options))
        {
            ulong id = Write(database, 1, 1);
            database.FlushNow();
            Write(database, 2, 2);
            database.FlushNow();
            var result = database.ForSeries(id, "value").Preflight(new() { MaxSources = 1 });
            Assert.False(result.DataChecked);
            Assert.Contains(result.Issues, issue => issue.Code == "TSP010");
        }
        using var reopened = Tsdb.Open(Options);
        Assert.Equal(2, reopened.Segments.SegmentCount);
    }

    [Fact]
    public void Preflight_TooManyTombstones_RejectsBeforeFilteringOrScanning()
    {
        using var database = Tsdb.Open(Options);
        ulong id = Write(database, 10, 1);
        database.Delete(id, "value", 0, 0);
        database.Delete(id, "value", 2, 2);
        var result = database.ForSeries(id, "value").Preflight(new() { MaxSources = 1 });
        Assert.False(result.DataChecked);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP010");
    }

    [Fact]
    public void Preflight_Reopen_UsesRecoveredSchemaCatalogSegmentsAndTombstones()
    {
        ulong id;
        using (var database = Tsdb.Open(Options))
        {
            Declare(database, FieldType.Float64);
            id = Write(database, 1, double.NaN);
            Write(database, 2, double.NegativeInfinity);
            Write(database, 3, 1);
            database.FlushNow();
            database.Delete(id, "value", 1, 1);
        }
        using var reopened = Tsdb.Open(Options);
        var result = reopened.ForSeries(id, "value").Preflight();
        Assert.True(result.HasSchema);
        Assert.Equal(1, result.MeasurementSeriesCount);
        Assert.True(result.SampleComplete);
        Assert.Equal(2, result.SampledPoints);
        Assert.Equal(1, result.NonFinitePoints);
    }

    [Fact]
    public void Preflight_MissingSeriesOrEmptyRange_DoesNotInventFieldAbsenceOrQualityEvidence()
    {
        using var database = Tsdb.Open(Options);
        Assert.False(database.ForSeries(123, "value").Preflight().DataChecked);
        ulong id = Write(database, 1, 1);
        var empty = database.ForSeries(id, "value").Between(2, 3).Preflight();
        Assert.True(empty.DataChecked);
        Assert.True(empty.SampleComplete);
        Assert.Empty(empty.ObservedFieldTypes);
        Assert.Contains(empty.Issues, issue => issue.Code == "TSP011");
        Assert.DoesNotContain(empty.Issues, issue => issue.Code == "TSP003");
    }

    [Fact]
    public void Preflight_EngineOnlyBuilder_ReportsUnknownMetadataWhileCheckingRealData()
    {
        using var database = Tsdb.Open(Options);
        ulong id = Write(database, 1, double.NaN);
        var result = database.Query.ForSeries(id, "value").Preflight();
        Assert.Null(result.RetentionEnabled);
        Assert.Null(result.MeasurementSeriesCount);
        Assert.Null(result.HasSchema);
        Assert.Equal(1, result.NonFinitePoints);
        Assert.Contains(result.Issues, issue => issue.Code == "TSP001");
    }

    [Fact]
    public void Preflight_PreCanceled_DoesNotAccessDisposedEngine()
    {
        var database = Tsdb.Open(Options);
        var builder = database.ForSeries(123, "value");
        database.Dispose();
        Assert.Throws<OperationCanceledException>(() => builder.Preflight(cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void Preflight_CanceledByActualRetentionClock_StopsBeforeDataRead()
    {
        using var cancellation = new CancellationTokenSource();
        using var database = Tsdb.Open(Options with
        {
            Retention = new RetentionPolicy
            {
                Enabled = true,
                PollInterval = TimeSpan.FromHours(1),
                NowFn = () => { cancellation.Cancel(); return 100; },
            },
        });
        ulong id = Write(database, 1, 1);
        Assert.Throws<OperationCanceledException>(() => database.ForSeries(id, "value").Preflight(cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Preflight_SourceGeneratedJson_RoundTripsEvidenceAndNonDefaultRange()
    {
        using var database = Tsdb.Open(Options);
        ulong id = Write(database, 20, double.NaN);
        var result = database.ForSeries(id, "value").Between(10, 30).Preflight();
        string json = JsonSerializer.Serialize(result, TimeSeriesQueryJsonContext.Default.TimeSeriesPreflightReport);
        var copy = JsonSerializer.Deserialize(json, TimeSeriesQueryJsonContext.Default.TimeSeriesPreflightReport)!;
        Assert.Equal(new TimeRange(10, 30), copy.Query.Range);
        Assert.Equal(result.NonFinitePoints, copy.NonFinitePoints);
        Assert.Equal(result.Tags, copy.Tags);
        Assert.Equal(result.Issues, copy.Issues);
        Assert.Equal(result.ObservedFieldTypes, copy.ObservedFieldTypes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void Preflight_InvalidSampleBudget_RejectsBeforeEngineRead(int maxSamplePoints)
    {
        using var database = Tsdb.Open(Options);
        Assert.Throws<ArgumentOutOfRangeException>(() => database.ForSeries(1, "value")
            .Preflight(new() { MaxSamplePoints = maxSamplePoints }));
    }

    private static void Declare(Tsdb database, FieldType type)
        => database.CreateMeasurement(MeasurementSchema.Create("sensor",
        [new("host", MeasurementColumnRole.Tag, FieldType.String), new("value", MeasurementColumnRole.Field, type)]));

    private static ulong Write(Tsdb database, long timestamp, double value, string host = "a")
    {
        var point = Point.Create("sensor", timestamp, new Dictionary<string, string> { ["host"] = host },
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromDouble(value) });
        database.Write(point);
        return database.Catalog.GetOrAdd(point).Id;
    }
}
