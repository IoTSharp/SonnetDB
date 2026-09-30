using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>measurement schema 文件写入失败时的内存发布原子性测试。</summary>
public sealed class TsdbSchemaPersistenceAtomicityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    /// <summary>为单个用例创建独占的数据库目录。</summary>
    public TsdbSchemaPersistenceAtomicityTests() => Directory.CreateDirectory(_root);

    /// <summary>清理本用例创建的目录。</summary>
    public void Dispose()
    {
        string target = Path.GetFullPath(_root);
        string temporaryDirectory = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(target), temporaryDirectory, comparison))
            throw new InvalidOperationException("Schema persistence test directory escaped the system temp directory.");
        Directory.Delete(target, recursive: true);
    }

    [Fact]
    public void CreateMeasurement_WhenSchemaSaveFails_DoesNotPublishAndCanRetry()
    {
        using var db = Open();
        MeasurementSchema schema = MeasurementSchema.Create("cpu",
        [new MeasurementColumn("usage", MeasurementColumnRole.Field, FieldType.Float64)]);
        string originalRevision = db.MeasurementSchemaRevision;
        Directory.CreateDirectory(SchemaTempPath);

        AssertSchemaSaveFailed(() => db.CreateMeasurement(schema));

        Assert.Equal(0, db.Measurements.Count);
        Assert.Equal(0L, db.MeasurementSchemaPersistCount);
        Assert.False(File.Exists(SchemaPath));
        Assert.Equal(originalRevision, db.MeasurementSchemaRevision);
        Assert.Empty(db.SchemaDiagnostics.SnapshotAudit());

        Directory.Delete(SchemaTempPath);
        db.CreateMeasurement(schema);
        Assert.Same(schema, db.Measurements.TryGet("cpu"));
        Assert.NotEqual(originalRevision, db.MeasurementSchemaRevision);
    }

    [Fact]
    public void CreateMeasurement_WhenAtomicRenameFails_DoesNotPublishAndRemovesTemporaryFile()
    {
        using var db = Open();
        MeasurementSchema schema = MeasurementSchema.Create("cpu",
        [new MeasurementColumn("usage", MeasurementColumnRole.Field, FieldType.Float64)]);
        string originalRevision = db.MeasurementSchemaRevision;
        Directory.CreateDirectory(SchemaPath);

        AssertSchemaSaveFailed(() => db.CreateMeasurement(schema));

        Assert.Equal(0, db.Measurements.Count);
        Assert.Equal(originalRevision, db.MeasurementSchemaRevision);
        Assert.False(File.Exists(SchemaTempPath));

        Directory.Delete(SchemaPath);
        db.CreateMeasurement(schema);
        Assert.NotNull(db.Measurements.TryGet("cpu"));
    }

    [Fact]
    public void Write_WhenSchemaSaveFails_DoesNotPublishMeasurementOrPoint()
    {
        using var db = Open();
        Point point = Point.Create("cpu", 1,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromLong(1) });
        string originalRevision = db.MeasurementSchemaRevision;
        Directory.CreateDirectory(SchemaTempPath);

        AssertSchemaSaveFailed(() => db.Write(point));

        Assert.Equal(0, db.Measurements.Count);
        Assert.Equal(0, db.Catalog.Count);
        Assert.Equal(0L, db.MemTable.PointCount);
        Assert.Equal(0L, db.MeasurementSchemaPersistCount);
        Assert.Equal(originalRevision, db.MeasurementSchemaRevision);
        Assert.Empty(db.SchemaDiagnostics.SnapshotAudit());

        Directory.Delete(SchemaTempPath);
        db.Write(point);
        Assert.NotNull(db.Measurements.TryGet("cpu"));
        Assert.Equal(1L, db.MemTable.PointCount);
    }

    [Fact]
    public void Write_WhenEvolutionSaveFails_KeepsPreviousColumnsAndMemTable()
    {
        using var db = Open();
        db.Write(Point.Create("cpu", 1,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromLong(1) }));
        var originalMemTable = db.MemTable;
        long originalPersistCount = db.MeasurementSchemaPersistCount;
        string originalRevision = db.MeasurementSchemaRevision;
        int originalAuditCount = db.SchemaDiagnostics.SnapshotAudit().Count;
        Point evolved = Point.Create("cpu", 2,
            new Dictionary<string, string> { ["host"] = "h1", ["rack"] = "r1" },
            new Dictionary<string, FieldValue>
            {
                ["usage"] = FieldValue.FromDouble(1.5),
                ["status"] = FieldValue.FromString("ok"),
            });
        Directory.CreateDirectory(SchemaTempPath);

        AssertSchemaSaveFailed(() => db.Write(evolved));

        MeasurementSchema schema = db.Measurements.TryGet("cpu")!;
        Assert.Equal(FieldType.Int64, schema.TryGetColumn("usage")!.DataType);
        Assert.Null(schema.TryGetColumn("rack"));
        Assert.Null(schema.TryGetColumn("status"));
        Assert.Same(originalMemTable, db.MemTable);
        Assert.Equal(1L, db.MemTable.PointCount);
        Assert.Equal(originalPersistCount, db.MeasurementSchemaPersistCount);
        Assert.Equal(originalRevision, db.MeasurementSchemaRevision);
        Assert.Equal(originalAuditCount, db.SchemaDiagnostics.SnapshotAudit().Count);
        Assert.Equal(FieldType.Int64,
            Assert.Single(MeasurementSchemaCodec.Load(SchemaPath)).TryGetColumn("usage")!.DataType);

        Directory.Delete(SchemaTempPath);
        db.Write(evolved);
        Assert.Equal(FieldType.Float64, db.Measurements.TryGet("cpu")!.TryGetColumn("usage")!.DataType);
        Assert.NotNull(db.Measurements.TryGet("cpu")!.TryGetColumn("status"));
    }

    [Fact]
    public void WriteMany_WhenSchemaSaveFails_DoesNotPartiallyPublishOrWrite()
    {
        using var db = Open();
        Point[] points =
        [
            Point.Create("cpu", 1, new Dictionary<string, string>(),
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromLong(1) }),
            Point.Create("memory", 2, new Dictionary<string, string>(),
                new Dictionary<string, FieldValue> { ["used"] = FieldValue.FromLong(2) }),
        ];
        Directory.CreateDirectory(SchemaTempPath);

        AssertSchemaSaveFailed(() => db.WriteMany(points));

        Assert.Equal(0, db.Measurements.Count);
        Assert.Equal(0, db.Catalog.Count);
        Assert.Equal(0L, db.MemTable.PointCount);
        Assert.Equal(0L, db.MeasurementSchemaPersistCount);
        Assert.Empty(db.SchemaDiagnostics.SnapshotAudit());

        Directory.Delete(SchemaTempPath);
        Assert.Equal(2, db.WriteMany(points));
        Assert.Equal(2, db.Measurements.Count);
    }

    [Fact]
    public void DropMeasurement_WhenSchemaSaveFails_KeepsSchemaAndSeries()
    {
        using var db = Open();
        db.Write(Point.Create("cpu", 1,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromLong(1) }));
        string originalRevision = db.MeasurementSchemaRevision;
        int originalAuditCount = db.SchemaDiagnostics.SnapshotAudit().Count;
        Directory.CreateDirectory(SchemaTempPath);

        AssertSchemaSaveFailed(() => db.DropMeasurement("cpu"));

        Assert.NotNull(db.Measurements.TryGet("cpu"));
        Assert.Equal(1, db.Catalog.Count);
        Assert.Single(MeasurementSchemaCodec.Load(SchemaPath));
        Assert.Equal(originalRevision, db.MeasurementSchemaRevision);
        Assert.Equal(originalAuditCount, db.SchemaDiagnostics.SnapshotAudit().Count);
        Assert.False(File.Exists(DropIntentPath));
        var seriesId = SeriesId.Compute(new SeriesKey("cpu",
            new Dictionary<string, string> { ["host"] = "h1" }));
        Assert.Single(db.Query.Execute(new PointQuery(
            seriesId, "usage", new TimeRange(0, long.MaxValue))));

        Directory.Delete(SchemaTempPath);
        Assert.True(db.DropMeasurement("cpu"));
        Assert.Null(db.Measurements.TryGet("cpu"));
    }

    [Fact]
    public void DropMeasurement_WhenCleanupFails_RecoversBeforeSameNameCanBeRecreated()
    {
        Point dropped = CreatePoint("cpu", 1);
        Point kept = CreatePoint("memory", 1);
        var droppedSeriesId = SeriesId.Compute(new SeriesKey("cpu", dropped.Tags));
        var keptSeriesId = SeriesId.Compute(new SeriesKey("memory", kept.Tags));

        using (var db = Open())
        {
            db.Write(dropped);
            db.Write(kept);
            db.FlushNow();
            db.AfterMeasurementSchemaDropTestHook = () => throw new IOException("Injected cleanup failure.");

            Assert.Throws<IOException>(() => db.DropMeasurement("cpu"));
            Assert.Null(db.Measurements.TryGet("cpu"));
            Assert.True(File.Exists(DropIntentPath));
            Assert.Throws<InvalidOperationException>(() => db.CreateMeasurement(CreateSchema("cpu")));
            Assert.Throws<InvalidOperationException>(() => db.Write(dropped));
            db.AfterMeasurementSchemaDropTestHook = null;
        }

        using var reopened = Open();
        Assert.Null(reopened.Measurements.TryGet("cpu"));
        Assert.Null(reopened.Catalog.TryGet(droppedSeriesId));
        Assert.Empty(reopened.Query.Execute(new PointQuery(
            droppedSeriesId, "usage", new TimeRange(0, long.MaxValue))));
        Assert.Single(reopened.Query.Execute(new PointQuery(
            keptSeriesId, "usage", new TimeRange(0, long.MaxValue))));
        Assert.False(File.Exists(DropIntentPath));

        reopened.CreateMeasurement(CreateSchema("cpu"));
        Assert.Empty(reopened.Query.Execute(new PointQuery(
            droppedSeriesId, "usage", new TimeRange(0, long.MaxValue))));
    }

    [Fact]
    public void DropMeasurement_WhenCatalogCheckpointFails_RecoveryAcceptsAlreadyPrunedCatalog()
    {
        Point dropped = CreatePoint("cpu", 1);
        Point kept = CreatePoint("memory", 1);
        var droppedSeriesId = SeriesId.Compute(new SeriesKey("cpu", dropped.Tags));
        var keptSeriesId = SeriesId.Compute(new SeriesKey("memory", kept.Tags));

        using (var db = Open())
        {
            db.Write(dropped);
            db.Write(kept);
            db.FlushNow();
            Directory.CreateDirectory(CatalogTempPath);

            AssertSchemaSaveFailed(() => db.DropMeasurement("cpu"));
            Assert.True(File.Exists(DropIntentPath));
            Directory.Delete(CatalogTempPath);
        }

        using var reopened = Open();
        Assert.Null(reopened.Catalog.TryGet(droppedSeriesId));
        Assert.Empty(reopened.Query.Execute(new PointQuery(
            droppedSeriesId, "usage", new TimeRange(0, long.MaxValue))));
        Assert.Single(reopened.Query.Execute(new PointQuery(
            keptSeriesId, "usage", new TimeRange(0, long.MaxValue))));
        Assert.False(File.Exists(DropIntentPath));
    }

    [Fact]
    public void Open_WhenDropIntentPrecedesSchemaDeletion_CancelsIntent()
    {
        using (var db = Open())
            db.CreateMeasurement(CreateSchema("cpu"));

        MeasurementDropIntentFile.Save(DropIntentPath, "cpu");
        using var reopened = Open();
        Assert.NotNull(reopened.Measurements.TryGet("cpu"));
        Assert.False(File.Exists(DropIntentPath));
    }

    [Fact]
    public void Open_WhenDropIntentIsCorrupt_FailsClosed()
    {
        using (Open()) { }
        File.WriteAllBytes(DropIntentPath, [0, 1, 2]);

        Assert.Throws<InvalidDataException>(() => Open());
    }

    [Fact]
    public void CreateMeasurement_WhenAuditCallbackMutatesManagedCatalog_RejectsDirectMutation()
    {
        using var db = Open();
        Exception? addFailure = null;
        Exception? removeFailure = null;
        db.SchemaDiagnostics.SchemaChanged += (_, _) =>
        {
            addFailure = Record.Exception(() => db.Measurements.Add(CreateSchema("unpersisted")));
            removeFailure = Record.Exception(() => db.Measurements.Remove("cpu"));
        };

        db.CreateMeasurement(CreateSchema("cpu"));

        Assert.IsType<InvalidOperationException>(addFailure);
        Assert.IsType<InvalidOperationException>(removeFailure);
        Assert.NotNull(db.Measurements.TryGet("cpu"));
        Assert.Null(db.Measurements.TryGet("unpersisted"));
        Assert.Single(MeasurementSchemaCodec.Load(SchemaPath));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private string SchemaPath => TsdbPaths.MeasurementSchemaPath(_root);

    private string SchemaTempPath => SchemaPath + ".tmp";

    private string DropIntentPath => TsdbPaths.MeasurementDropIntentPath(_root);

    private string CatalogTempPath => TsdbPaths.CatalogPath(_root) + ".tmp";

    private static MeasurementSchema CreateSchema(string name) => MeasurementSchema.Create(name,
        [new MeasurementColumn("usage", MeasurementColumnRole.Field, FieldType.Int64)]);

    private static Point CreatePoint(string name, long timestamp) => Point.Create(name, timestamp,
        new Dictionary<string, string> { ["host"] = "h1" },
        new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromLong(1) });

    private static void AssertSchemaSaveFailed(Action action)
    {
        Exception? error = Record.Exception(action);
        Assert.True(error is IOException or UnauthorizedAccessException,
            $"Expected schema file I/O failure, got {error?.GetType().FullName}: {error?.Message}");
    }
}
