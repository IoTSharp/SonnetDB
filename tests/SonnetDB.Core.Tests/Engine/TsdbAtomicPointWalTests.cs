using System.Buffers.Binary;
using System.Collections;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

public sealed class TsdbAtomicPointWalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SonnetDB-T1-" + Guid.NewGuid().ToString("N"));

    private TsdbOptions Options => new()
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        Kv = KvOptions.Default with { ExpirerEnabled = false, CleanupEnabled = false },
        FlushPolicy = new MemTableFlushPolicy { MaxBytes = 128 * 1024 * 1024, MaxPoints = 1_000_000, MaxAge = TimeSpan.FromHours(1) },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
    };

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("SonnetDB-T1-", Path.GetFileName(full), StringComparison.Ordinal);
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }

    private static Point MakePoint(long timestamp, long value = 1) => Point.Create("atomic", timestamp,
        new Dictionary<string, string> { ["host"] = "test" }, Fields(value));

    private static Dictionary<string, FieldValue> Fields(long value) => new()
    {
        ["number"] = FieldValue.FromLong(value),
        ["text"] = FieldValue.FromString("point-" + value),
        ["flag"] = FieldValue.FromBool(value % 2 == 0),
    };

    private static void AssertPoints(Tsdb db, params long[] values)
    {
        if (values.Length == 0)
        {
            Assert.Equal(0, db.MemTable.PointCount);
            return;
        }
        SeriesEntry series = Assert.Single(db.Catalog.Find("atomic", new Dictionary<string, string> { ["host"] = "test" }));
        DataPoint[] numbers = db.Query.Execute(new PointQuery(series.Id, "number", TimeRange.All)).ToArray();
        DataPoint[] texts = db.Query.Execute(new PointQuery(series.Id, "text", TimeRange.All)).ToArray();
        DataPoint[] flags = db.Query.Execute(new PointQuery(series.Id, "flag", TimeRange.All)).ToArray();
        Assert.Equal(values, numbers.Select(point => point.Value.AsLong()));
        Assert.Equal(values.Select(value => "point-" + value), texts.Select(point => point.Value.AsString()));
        Assert.Equal(values.Select(value => value % 2 == 0), flags.Select(point => point.Value.AsBool()));
        Assert.Equal(numbers.Select(point => point.Timestamp), texts.Select(point => point.Timestamp));
        Assert.Equal(numbers.Select(point => point.Timestamp), flags.Select(point => point.Timestamp));
    }

    private WalRecord[] ReadWal() => WalSegmentLayout.Enumerate(TsdbPaths.WalDir(_root))
        .SelectMany(segment =>
        {
            using var reader = WalReader.OpenForRecovery(segment.Path);
            return reader.Replay().ToArray();
        }).ToArray();

    [Fact]
    public void DefaultPublicWrite_UsesOneCompleteRecord_AndOneLsnForAllFields()
    {
        using var db = Tsdb.Open(Options);
        db.Write(MakePoint(0));
        AssertPoints(db, 1);
        WalRecord[] records = ReadWal();
        Assert.Single(records.OfType<CreateSeriesRecord>());
        AtomicPointRecord point = Assert.Single(records.OfType<AtomicPointRecord>());
        Assert.Equal(3, point.Fields.Count);
        Assert.Empty(records.OfType<WritePointRecord>());
        Assert.Equal(point.Lsn, db.MemTable.FirstLsn);
        Assert.Equal(point.Lsn, db.MemTable.LastLsn);
        foreach (WalSegmentInfo segment in WalSegmentLayout.Enumerate(TsdbPaths.WalDir(_root)))
        {
            using var stream = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] header = new byte[FormatSizes.WalFileHeaderSize];
            Assert.Equal(header.Length, stream.Read(header, 0, header.Length));
            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)));
        }
    }

    [Fact]
    public void PublicWriteMany_KeepsEveryPointAndDuplicateTimestamp_AcrossRepeatedReopen()
    {
        Tsdb db = Tsdb.Open(Options);
        db.WriteMany(new[] { MakePoint(10, 1), MakePoint(10, 2), MakePoint(20, 3) }.AsSpan());
        AssertPoints(db, 1, 2, 3);
        Assert.Equal(3, ReadWal().OfType<AtomicPointRecord>().Count());
        db.CrashSimulationCloseWal();
        for (int reopen = 0; reopen < 3; reopen++)
        {
            db = Tsdb.Open(Options);
            AssertPoints(db, 1, 2, 3);
            db.CrashSimulationCloseWal();
        }
    }

    [Fact]
    public void PublicWrite_AllFieldTypesAndTimestampBoundaries_ReopenExactly()
    {
        var fields = new Dictionary<string, FieldValue>
        {
            ["long"] = FieldValue.FromLong(long.MinValue),
            ["double"] = FieldValue.FromDouble(double.MaxValue),
            ["bool"] = FieldValue.FromBool(true),
            ["string"] = FieldValue.FromString("零\0Unicode-新疆"),
            ["vector"] = FieldValue.FromVector(new[] { 1.0f, -2.0f }),
            ["geo"] = FieldValue.FromGeoPoint(43.8, 87.6),
        };
        Tsdb db = Tsdb.Open(Options);
        db.Write(Point.Create("types", 0, fields: fields));
        db.Write(Point.Create("types", long.MaxValue - 1, fields: fields));
        db.CrashSimulationCloseWal();
        using var reopened = Tsdb.Open(Options);
        SeriesEntry series = Assert.Single(reopened.Catalog.Find("types", null));
        foreach (var field in fields)
        {
            DataPoint[] points = reopened.Query.Execute(new PointQuery(series.Id, field.Key, TimeRange.All)).ToArray();
            Assert.Equal(2, points.Length);
            Assert.Equal(new[] { 0L, long.MaxValue - 1 }, points.Select(point => point.Timestamp));
            Assert.All(points, point => Assert.Equal(field.Value, point.Value));
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("negative")]
    [InlineData("surrogate-field")]
    [InlineData("surrogate-value")]
    [InlineData("surrogate-tag")]
    [InlineData("surrogate-measurement")]
    [InlineData("too-many")]
    [InlineData("too-large")]
    public void InvalidPublicPoint_ChangesNoSchemaCatalogOrWal_AndNextWriteWorks(string invalid)
    {
        using var db = Tsdb.Open(Options);
        Point point = invalid switch
        {
            "empty" => new Point { Measurement = "atomic", Timestamp = 0, Tags = new Dictionary<string, string>(), Fields = new Dictionary<string, FieldValue>() },
            "negative" => new Point { Measurement = "atomic", Timestamp = -1, Tags = new Dictionary<string, string>(), Fields = Fields(1) },
            "surrogate-field" => new Point { Measurement = "atomic", Timestamp = 0, Tags = new Dictionary<string, string>(), Fields = new Dictionary<string, FieldValue> { ["bad\uD800"] = FieldValue.FromLong(1) } },
            "surrogate-value" => new Point { Measurement = "atomic", Timestamp = 0, Tags = new Dictionary<string, string>(), Fields = new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromString("bad\uD800") } },
            "surrogate-tag" => new Point { Measurement = "atomic", Timestamp = 0, Tags = new Dictionary<string, string> { ["tag"] = "bad\uD800" }, Fields = Fields(1) },
            "surrogate-measurement" => new Point { Measurement = "bad\uD800", Timestamp = 0, Tags = new Dictionary<string, string>(), Fields = Fields(1) },
            "too-many" => new Point { Measurement = "atomic", Timestamp = 0, Tags = new Dictionary<string, string>(), Fields = Enumerable.Range(0, 1025).ToDictionary(index => "f" + index, index => FieldValue.FromLong(index)) },
            "too-large" => new Point { Measurement = "atomic", Timestamp = 0, Tags = new Dictionary<string, string>(), Fields = new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromString(new string('x', 8 * 1024 * 1024)) } },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };
        Assert.ThrowsAny<ArgumentException>(() => db.Write(point));
        Assert.Empty(ReadWal());
        Assert.Equal(0, db.Catalog.Count);
        Assert.Empty(db.Measurements.Snapshot());
        Assert.Equal(0, db.MemTable.PointCount);
        db.Write(MakePoint(1));
        AssertPoints(db, 1);
    }

    [Fact]
    public void CancelledBeforeWrite_ChangesNothing_AndEmptyBatchHasNoSideEffects()
    {
        using var db = Tsdb.Open(Options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => db.Write(MakePoint(1), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => db.WriteMany(new[] { MakePoint(1) }.AsSpan(), cancellation.Token));
        Assert.Equal(0, db.WriteMany(ReadOnlySpan<Point>.Empty));
        Assert.Empty(ReadWal());
        Assert.Equal(0, db.Catalog.Count);
    }

    [Fact]
    public void CancelledImmediatelyBeforePointAppend_PublishesNoField_AndCanContinue()
    {
        using var db = Tsdb.Open(Options);
        using var cancellation = new CancellationTokenSource();
        db.BeforeAtomicPointAppendTestHook = cancellation.Cancel;
        Assert.Throws<OperationCanceledException>(() => db.Write(MakePoint(1), cancellation.Token));
        Assert.Empty(ReadWal().OfType<AtomicPointRecord>());
        Assert.Equal(0, db.MemTable.PointCount);
        db.BeforeAtomicPointAppendTestHook = null;
        db.Write(MakePoint(2, 2));
        AssertPoints(db, 2);
    }

    [Fact]
    public void CancelledAfterRecordStarts_CommitsTheWholePoint()
    {
        using var db = Tsdb.Open(Options);
        using var cancellation = new CancellationTokenSource();
        db.AfterAtomicPointWalWriteTestHook = cancellation.Cancel;
        db.Write(MakePoint(1), cancellation.Token);
        AssertPoints(db, 1);
        Assert.Single(ReadWal().OfType<AtomicPointRecord>());
    }

    [Fact]
    public void UnknownAppend_SealsWritesAndCheckpoint_ReopenReconcilesWholePointOnce()
    {
        Tsdb db = Tsdb.Open(Options);
        db.AfterAtomicPointWalWriteTestHook = () =>
        {
            db.FlushUnacknowledgedPointBytesToOsForTest();
            throw new IOException("T1 deterministic unknown append after complete bytes.");
        };
        Assert.Throws<IOException>(() => db.Write(MakePoint(1)));
        Assert.Equal(0, db.MemTable.PointCount);
        Assert.Throws<InvalidOperationException>(() => db.Write(MakePoint(2, 2)));
        Assert.Throws<InvalidOperationException>(() => db.FlushNow());
        db.CrashSimulationCloseWal();
        Assert.Empty(ReadWal().OfType<CheckpointRecord>());
        Assert.Single(ReadWal().OfType<AtomicPointRecord>());
        for (int reopen = 0; reopen < 3; reopen++)
        {
            db = Tsdb.Open(Options);
            AssertPoints(db, 1);
            db.CrashSimulationCloseWal();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TornOrCrcInvalidPoint_DropsEveryField_AndDoesNotCrossSuccessor(bool corruptCrc)
    {
        Tsdb db = Tsdb.Open(Options);
        db.WriteMany(new[] { MakePoint(1, 1), MakePoint(2, 2), MakePoint(3, 3) }.AsSpan());
        db.CrashSimulationCloseWal();
        string path = Assert.Single(WalSegmentLayout.Enumerate(TsdbPaths.WalDir(_root))).Path;
        byte[] bytes = File.ReadAllBytes(path);
        int[] offsets = AtomicOffsets(bytes);
        int second = offsets[1];
        int payload = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(second + 8));
        if (corruptCrc)
        {
            bytes[second + FormatSizes.WalRecordHeaderSize + payload - 1] ^= 0x40;
            File.WriteAllBytes(path, bytes);
        }
        else
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
            stream.SetLength(second + FormatSizes.WalRecordHeaderSize + payload / 2);
        }
        db = Tsdb.Open(Options);
        AssertPoints(db, 1);
        db.Write(MakePoint(4, 4));
        AssertPoints(db, 1, 4);
        db.CrashSimulationCloseWal();
        using var reopened = Tsdb.Open(Options);
        AssertPoints(reopened, 1, 4);
    }

    [Fact]
    public void RollingFlushCheckpointAndDelete_ReopenWithoutDuplicateFields()
    {
        TsdbOptions options = Options with { WalRolling = new WalRollingPolicy { Enabled = true, MaxRecordsPerSegment = 3, MaxBytesPerSegment = 1_000_000 } };
        Tsdb db = Tsdb.Open(options);
        db.WriteMany(Enumerable.Range(1, 12).Select(index => MakePoint(index, index)).ToArray().AsSpan());
        Assert.True(WalSegmentLayout.Enumerate(TsdbPaths.WalDir(_root)).Count >= 4);
        Assert.Equal(12, ReadWal().OfType<AtomicPointRecord>().Count());
        db.FlushNow();
        Assert.True(db.CheckpointLsn > 0);
        Assert.Equal(0, db.MemTable.PointCount);
        Assert.Empty(ReadWal().OfType<AtomicPointRecord>());
        db.Write(MakePoint(13, 13));
        db.CrashSimulationCloseWal();
        db = Tsdb.Open(options);
        AssertPoints(db, Enumerable.Range(1, 13).Select(index => (long)index).ToArray());
        SeriesEntry series = Assert.Single(db.Catalog.Find("atomic", null));
        db.Delete(series.Id, "number", 2, 2);
        db.CrashSimulationCloseWal();
        using var reopened = Tsdb.Open(options);
        Assert.Equal(12, reopened.Query.Execute(new PointQuery(series.Id, "number", TimeRange.All)).Count());
        Assert.Equal(13, reopened.Query.Execute(new PointQuery(series.Id, "text", TimeRange.All)).Count());
    }

    [Fact]
    public void CheckpointAcrossSegments_RemovesWholeAtomicRecords()
    {
        Directory.CreateDirectory(_root);
        using var set = WalSegmentSet.OpenAtomicPoints(_root);
        var catalog = new SeriesCatalog();
        SeriesEntry series = catalog.GetOrAdd("atomic", new Dictionary<string, string>());
        set.AppendCreateSeries(series.Id, "atomic", series.Tags);
        long first = set.AppendAtomicPoint(series.Id, 1, Fields(1));
        set.Roll();
        set.AppendAtomicPoint(series.Id, 2, Fields(2));
        set.AppendCheckpoint(first);
        set.Sync();
        WalReplayResult replay = set.ReplayWithCheckpoint(new SeriesCatalog());
        AtomicPointRecord point = Assert.Single(replay.AtomicPoints);
        Assert.Equal(2, point.PointTimestamp);
        Assert.Equal(3, point.Fields.Count);
    }

    [Fact]
    public void LegacyV1_StaysByteIdentical_WhileNewPointsUseV2()
    {
        string wal = TsdbPaths.WalDir(_root);
        Directory.CreateDirectory(wal);
        var catalog = new SeriesCatalog();
        SeriesEntry series = catalog.GetOrAdd("atomic", new Dictionary<string, string> { ["host"] = "test" });
        string oldPath = WalSegmentLayout.SegmentPath(wal, 1);
        using (var writer = WalWriter.Open(oldPath))
        {
            writer.AppendCreateSeries(series.Id, "atomic", series.Tags);
            foreach (var field in Fields(1))
                writer.AppendWritePoint(series.Id, 1, field.Key, field.Value);
        }
        byte[] before = File.ReadAllBytes(oldPath);
        Assert.False(File.Exists(TsdbPaths.CatalogPath(_root)));
        Tsdb db = Tsdb.Open(Options);
        Assert.Equal(before, File.ReadAllBytes(oldPath));
        AssertPoints(db, 1);
        db.Write(MakePoint(2, 2));
        Assert.Equal(before, File.ReadAllBytes(oldPath));
        db.CrashSimulationCloseWal();
        using var reopened = Tsdb.Open(Options);
        AssertPoints(reopened, 1, 2);
    }

    [Fact]
    public void EmptyLegacyV1_UpgradesAndPreservesOriginalHeaderAsArchive()
    {
        string wal = TsdbPaths.WalDir(_root);
        Directory.CreateDirectory(wal);
        string oldPath = WalSegmentLayout.SegmentPath(wal, 1);
        using (var writer = WalWriter.Open(oldPath))
            writer.Sync();
        byte[] before = File.ReadAllBytes(oldPath);
        using var db = Tsdb.Open(Options);
        db.Write(MakePoint(1));
        AssertPoints(db, 1);
        Assert.Equal(before, File.ReadAllBytes(oldPath + ".legacy-empty-v1"));
    }

    [Fact]
    public void V2MissingSidecarAndPublishedSegment_DoesNotTrustTheRemainingWalCheckpoint()
    {
        Tsdb db = Tsdb.Open(Options);
        db.Write(MakePoint(1));
        SegmentBuildResult segment = Assert.IsType<SegmentBuildResult>(db.FlushNow());
        db.CrashSimulationCloseWal();
        Assert.Equal(2, CatalogFileCodec.Load(TsdbPaths.CatalogPath(_root)).PersistenceFormatVersion);
        Assert.Contains(ReadWal(), record => record is CheckpointRecord { CheckpointLsn: > 0 });
        File.Delete(WalSegmentLayout.CheckpointPath(TsdbPaths.WalDir(_root)));
        File.Delete(segment.Path);
        Assert.Throws<InvalidDataException>(() => Tsdb.Open(Options));
    }

    [Fact]
    public void GenuineV1DirectoryWithoutCheckpointSidecar_RetainsItsLegacyReadContract()
    {
        string walDirectory = TsdbPaths.WalDir(_root);
        Directory.CreateDirectory(walDirectory);
        var catalog = new SeriesCatalog();
        SeriesEntry series = catalog.GetOrAdd("atomic", new Dictionary<string, string> { ["host"] = "test" });
        var memory = new MemTable();
        using (WalSegmentSet legacy = WalSegmentSet.Open(walDirectory))
        {
            legacy.AppendCreateSeries(series.Id, series.Measurement, series.Tags);
            foreach (var field in Fields(1))
            {
                long lsn = legacy.AppendWritePoint(series.Id, 1, field.Key, field.Value);
                memory.Append(series.Id, 1, field.Key, field.Value, lsn);
            }
            CatalogFileCodec.Save(catalog, TsdbPaths.CatalogPath(_root));
            Assert.Equal(1, CatalogFileCodec.Load(TsdbPaths.CatalogPath(_root)).PersistenceFormatVersion);
            Assert.IsType<SegmentBuildResult>(new FlushCoordinator(Options).Flush(memory, legacy, 1));
        }
        File.Delete(WalSegmentLayout.CheckpointPath(walDirectory));
        Assert.Contains(ReadWal(), record => record is CheckpointRecord { CheckpointLsn: > 0 });
        for (int reopen = 0; reopen < 3; reopen++)
        {
            using var reopened = Tsdb.Open(Options);
            AssertPoints(reopened, 1);
            Assert.Single(reopened.ListSegments());
            Assert.Equal(2, CatalogFileCodec.Load(TsdbPaths.CatalogPath(_root)).PersistenceFormatVersion);
            WalCheckpointState state = Assert.IsType<WalCheckpointState>(WalCheckpointFile.TryLoad(WalSegmentLayout.CheckpointPath(walDirectory)));
            Assert.True(state.CheckpointLsn > 0);
            Assert.Equal(1, state.SegmentId);
        }
    }

    [Fact]
    public void PublicOpen_ConflictingLegacyDestination_PreservesTheCompleteOldWal()
    {
        string wal = TsdbPaths.WalDir(_root);
        Directory.CreateDirectory(wal);
        string legacyPath = Path.Combine(wal, WalSegmentLayout.LegacyActiveFileName);
        string targetPath = WalSegmentLayout.SegmentPath(wal, 1);
        var catalog = new SeriesCatalog();
        SeriesEntry series = catalog.GetOrAdd("atomic", new Dictionary<string, string> { ["host"] = "test" });
        using (var writer = WalWriter.Open(legacyPath))
        {
            writer.AppendCreateSeries(series.Id, series.Measurement, series.Tags);
            foreach (var field in Fields(1))
                writer.AppendWritePoint(series.Id, 1, field.Key, field.Value);
        }
        byte[] before = File.ReadAllBytes(legacyPath);
        File.WriteAllBytes(targetPath, []);
        Assert.Throws<InvalidDataException>(() => Tsdb.Open(Options));
        Assert.Equal(before, File.ReadAllBytes(legacyPath));
        Assert.Empty(File.ReadAllBytes(targetPath));
    }

    [Fact]
    public void V1CheckpointWithoutAnyPublishedSegment_CannotBecomeAnUpgradeBoundary()
    {
        string wal = TsdbPaths.WalDir(_root);
        Directory.CreateDirectory(wal);
        var catalog = new SeriesCatalog();
        SeriesEntry series = catalog.GetOrAdd("atomic", new Dictionary<string, string> { ["host"] = "test" });
        CatalogFileCodec.Save(catalog, TsdbPaths.CatalogPath(_root));
        string path = WalSegmentLayout.SegmentPath(wal, 1);
        using (var writer = WalWriter.Open(path))
        {
            writer.AppendCreateSeries(series.Id, series.Measurement, series.Tags);
            long last = 0;
            foreach (var field in Fields(1))
                last = writer.AppendWritePoint(series.Id, 1, field.Key, field.Value);
            writer.AppendCheckpoint(last);
        }
        byte[] original = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => Tsdb.Open(Options));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(WalSegmentLayout.CheckpointPath(wal)));
        Assert.Equal(1, CatalogFileCodec.Load(TsdbPaths.CatalogPath(_root)).PersistenceFormatVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingCatalogWithPublishedTimeSeries_RejectsBeforeChangingWal(bool keepCheckpoint)
    {
        Tsdb db = Tsdb.Open(Options);
        db.Write(MakePoint(1));
        SegmentBuildResult segment = Assert.IsType<SegmentBuildResult>(db.FlushNow());
        db.CrashSimulationCloseWal();
        var originalWal = WalSegmentLayout.Enumerate(TsdbPaths.WalDir(_root))
            .ToDictionary(info => info.Path, info => File.ReadAllBytes(info.Path));
        File.Delete(TsdbPaths.CatalogPath(_root));
        if (!keepCheckpoint)
            File.Delete(WalSegmentLayout.CheckpointPath(TsdbPaths.WalDir(_root)));
        Assert.Throws<InvalidDataException>(() => Tsdb.Open(Options));
        Assert.False(File.Exists(TsdbPaths.CatalogPath(_root)));
        Assert.True(File.Exists(segment.Path));
        foreach (var file in originalWal)
            Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Fact]
    public void WalOnlyV2WithMissingCatalog_RebuildsCompleteSeriesAndFields()
    {
        Tsdb db = Tsdb.Open(Options);
        db.Write(MakePoint(1));
        db.CrashSimulationCloseWal();
        Assert.Empty(TsdbPaths.EnumerateSegments(_root));
        Assert.False(File.Exists(WalSegmentLayout.CheckpointPath(TsdbPaths.WalDir(_root))));
        File.Delete(TsdbPaths.CatalogPath(_root));
        using var reopened = Tsdb.Open(Options);
        AssertPoints(reopened, 1);
        Assert.Equal(2, CatalogFileCodec.Load(TsdbPaths.CatalogPath(_root)).PersistenceFormatVersion);
    }

    [Fact]
    public void RecoveryBudgetOrCancellation_DoesNotTruncateExistingWal()
    {
        Tsdb db = Tsdb.Open(Options);
        db.Write(MakePoint(1));
        db.CrashSimulationCloseWal();
        string path = Assert.Single(WalSegmentLayout.Enumerate(TsdbPaths.WalDir(_root))).Path;
        byte[] before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => Tsdb.Open(Options with { WalRecovery = new AtomicPointWalRecoveryOptions(MaxRecords: 1) }));
        Assert.Equal(before, File.ReadAllBytes(path));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Tsdb.Open(Options, cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(path));
        using var reopened = Tsdb.Open(Options);
        AssertPoints(reopened, 1);
    }

    [Fact]
    public void MemTableTypeFailure_DoesNotPublishOtherFields()
    {
        var table = new MemTable();
        table.Append(1, 1, "existing", FieldValue.FromLong(1), 1);
        Assert.Throws<InvalidOperationException>(() => table.AppendAtomicPoint(1, 2,
            new Dictionary<string, FieldValue>
            {
                ["new"] = FieldValue.FromLong(2),
                ["existing"] = FieldValue.FromBool(true),
            }, 2));
        Assert.Equal(1, table.PointCount);
        Assert.Null(table.TryGet(new SeriesFieldKey(1, "new")));
    }

    [Fact]
    public async Task MemTableReaders_DoNotObserveAPointPublicationPrefix()
    {
        var table = new MemTable();
        using var betweenFields = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var readStarted = new ManualResetEventSlim();
        var fields = new PausingFields(Fields(1), betweenFields, release);
        Task append = Task.Run(() => table.AppendAtomicPoint(1, 1, fields, 1));
        Task<long>? read = null;
        try
        {
            Assert.True(betweenFields.Wait(TimeSpan.FromSeconds(5)));
            read = Task.Run(() =>
            {
                readStarted.Set();
                return table.PointCount;
            });
            Assert.True(readStarted.Wait(TimeSpan.FromSeconds(5)));
            await Task.Delay(50);
            Assert.False(read.IsCompleted);
        }
        finally
        {
            release.Set();
            await append.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(3, await read!.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static int[] AtomicOffsets(byte[] bytes)
    {
        var result = new List<int>();
        int offset = FormatSizes.WalFileHeaderSize;
        for (int record = 0; record < 100 && offset + FormatSizes.WalRecordHeaderSize <= bytes.Length; record++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) != WalRecordHeader.MagicValue)
                break;
            if (bytes[offset + 4] == (byte)WalRecordType.AtomicPoint)
                result.Add(offset);
            offset = checked(offset + FormatSizes.WalRecordHeaderSize + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 8)));
        }
        return result.ToArray();
    }

    private sealed class PausingFields(
        IReadOnlyDictionary<string, FieldValue> fields,
        ManualResetEventSlim betweenFields,
        ManualResetEventSlim release) : IReadOnlyDictionary<string, FieldValue>
    {
        private int _enumerations;
        public int Count => fields.Count;
        public IEnumerable<string> Keys => fields.Keys;
        public IEnumerable<FieldValue> Values => fields.Values;
        public FieldValue this[string key] => fields[key];
        public bool ContainsKey(string key) => fields.ContainsKey(key);
        public bool TryGetValue(string key, out FieldValue value) => fields.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, FieldValue>> GetEnumerator()
        {
            int enumeration = Interlocked.Increment(ref _enumerations);
            int index = 0;
            foreach (var field in fields)
            {
                yield return field;
                if (enumeration == 2 && index++ == 0)
                {
                    betweenFields.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Atomic publication reader did not release the test writer.");
                }
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
