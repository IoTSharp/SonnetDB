using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>measurement schema 策略、额度与变更诊断测试。</summary>
public sealed class TsdbMeasurementSchemaPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-schema-policy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Write_DisabledMode_RejectsImplicitCreateButAllowsExplicitCreate()
    {
        using var db = Open(new MeasurementSchemaPolicy { Mode = MeasurementSchemaMode.Disabled });

        string originalRevision = db.MeasurementSchemaRevision;
        Assert.Throws<InvalidOperationException>(() => db.Write(Point("cpu", 1, ("usage", FieldValue.FromLong(1)))));
        Assert.Null(db.Measurements.TryGet("cpu"));
        Assert.Equal(originalRevision, db.MeasurementSchemaRevision);

        db.CreateMeasurement(MeasurementSchema.Create("cpu",
            [new MeasurementColumn("usage", MeasurementColumnRole.Field, FieldType.Int64)]));
        db.Write(Point("cpu", 2, ("usage", FieldValue.FromLong(2))));

        Assert.NotEqual(originalRevision, db.MeasurementSchemaRevision);
        Assert.Equal(1, db.SchemaDiagnostics.GetMetrics().RejectedChanges);
        Assert.Single(db.SchemaDiagnostics.SnapshotAudit(), static record => record.Kind == MeasurementSchemaAuditKind.Rejected);
    }

    [Fact]
    public void Write_CreateOnlyWithMeasurementOverride_ControlsEvolutionPerMeasurement()
    {
        var policy = new MeasurementSchemaPolicy
        {
            Mode = MeasurementSchemaMode.CreateOnly,
            MeasurementModes = new Dictionary<string, MeasurementSchemaMode>
            {
                ["flexible"] = MeasurementSchemaMode.CreateAndEvolve,
                ["locked"] = MeasurementSchemaMode.Disabled,
            },
        };
        using var db = Open(policy);

        db.Write(Point("cpu", 1, ("usage", FieldValue.FromLong(1))));
        Assert.Throws<InvalidOperationException>(() =>
            db.Write(Point("cpu", 2, ("extra", FieldValue.FromLong(2)))));
        Assert.Throws<InvalidOperationException>(() =>
            db.Write(Point("cpu", 3, ("usage", FieldValue.FromDouble(3.5)))));
        Assert.Null(db.Measurements.TryGet("cpu")!.TryGetColumn("extra"));
        Assert.Equal(FieldType.Int64, db.Measurements.TryGet("cpu")!.TryGetColumn("usage")!.DataType);

        db.Write(Point("flexible", 1, ("usage", FieldValue.FromLong(1))));
        db.Write(Point("flexible", 2, ("extra", FieldValue.FromLong(2))));
        Assert.NotNull(db.Measurements.TryGet("flexible")!.TryGetColumn("extra"));
        Assert.Throws<InvalidOperationException>(() =>
            db.Write(Point("locked", 1, ("usage", FieldValue.FromLong(1)))));
    }

    [Fact]
    public void Write_MeasurementLimit_RejectsImplicitAndExplicitCreation()
    {
        using var db = Open(new MeasurementSchemaPolicy { MaxMeasurements = 1 });
        db.Write(Point("first", 1, ("value", FieldValue.FromLong(1))));
        string revision = db.MeasurementSchemaRevision;

        Assert.Throws<InvalidOperationException>(() => db.Write(Point("second", 1, ("value", FieldValue.FromLong(2)))));
        Assert.Throws<InvalidOperationException>(() => db.CreateMeasurement(MeasurementSchema.Create("explicit",
            [new MeasurementColumn("value", MeasurementColumnRole.Field, FieldType.Int64)])));

        Assert.Equal(1, db.Measurements.Count);
        Assert.Equal(revision, db.MeasurementSchemaRevision);
        Assert.Equal(2, db.SchemaDiagnostics.GetMetrics().RejectedChanges);
    }

    [Fact]
    public void Write_ColumnAndPerWriteLimits_RejectBeforePublishing()
    {
        using var db = Open(new MeasurementSchemaPolicy
        {
            MaxColumnsPerMeasurement = 3,
            MaxNewColumnsPerWrite = 2,
        });
        db.Write(Point("cpu", 1, ("a", FieldValue.FromLong(1))));
        string revision = db.MeasurementSchemaRevision;

        Assert.Throws<InvalidOperationException>(() => db.Write(Point("cpu", 2,
            ("b", FieldValue.FromLong(2)),
            ("c", FieldValue.FromLong(3)),
            ("d", FieldValue.FromLong(4)))));
        Assert.Equal(revision, db.MeasurementSchemaRevision);
        Assert.Single(db.Measurements.TryGet("cpu")!.Columns);

        db.Write(Point("cpu", 3, ("b", FieldValue.FromLong(3))));
        db.Write(Point("cpu", 4, ("c", FieldValue.FromLong(4))));
        Assert.Throws<InvalidOperationException>(() => db.Write(Point("cpu", 5, ("d", FieldValue.FromLong(5)))));
        Assert.Equal(3, db.Measurements.TryGet("cpu")!.Columns.Count);
    }

    [Fact]
    public void WriteMany_NewColumnsAcrossPoints_EnforcesBlockBudgetAtomically()
    {
        using var db = Open(new MeasurementSchemaPolicy { MaxNewColumnsPerWrite = 2 });
        db.Write(Point("cpu", 1, ("a", FieldValue.FromLong(1))));
        string revision = db.MeasurementSchemaRevision;

        Point[] points =
        [
            Point("cpu", 2, ("b", FieldValue.FromLong(2))),
            Point("cpu", 3, ("c", FieldValue.FromLong(3))),
            Point("cpu", 4, ("d", FieldValue.FromLong(4))),
        ];
        Assert.Throws<InvalidOperationException>(() => db.WriteMany(points));

        Assert.Equal(revision, db.MeasurementSchemaRevision);
        Assert.Single(db.Measurements.TryGet("cpu")!.Columns);
        Assert.Equal(1L, db.MemTable.PointCount);
    }

    [Fact]
    public void Write_WithCreateOnlyModeAndCaseVariants_DoesNotEvolveSchemaOrSpendColumnBudget()
    {
        using var db = Open(new MeasurementSchemaPolicy { Mode = MeasurementSchemaMode.CreateOnly });
        db.Write(Point("Sensors", 1, ("Usage", FieldValue.FromLong(1))));
        string revision = db.MeasurementSchemaRevision;

        db.Write(Point("sensors", 2, ("usage", FieldValue.FromLong(2))));

        Assert.Equal(revision, db.MeasurementSchemaRevision);
        Assert.Equal("Usage", Assert.Single(db.Measurements.TryGet("Sensors")!.Columns).Name);
        Assert.Equal(1, db.Catalog.Count);
        Assert.Equal(2L, db.MemTable.PointCount);
    }

    [Fact]
    public void WriteMany_CaseVariantsOfNewColumn_CountsOneNewColumn()
    {
        using var db = Open(new MeasurementSchemaPolicy { MaxNewColumnsPerWrite = 1 });
        db.Write(Point("Sensors", 1, ("Base", FieldValue.FromLong(1))));

        Point[] points =
        [
            Point("sensors", 2, ("Added", FieldValue.FromLong(2))),
            Point("SENSORS", 3, ("added", FieldValue.FromLong(3))),
        ];
        Assert.Equal(2, db.WriteMany(points));

        Assert.Equal(new[] { "Base", "Added" }, db.Measurements.TryGet("Sensors")!.Columns.Select(static column => column.Name));
        Assert.Equal(1, db.Measurements.Count);
        Assert.Equal(1, db.Catalog.Count);
    }

    [Fact]
    public void Write_SuccessfulEvolution_UpdatesRevisionMetricsAndAuditAcrossReopen()
    {
        string revision;
        using (var db = Open(new MeasurementSchemaPolicy()))
        {
            var changed = new List<MeasurementSchemaAuditEvent>();
            var audit = new List<MeasurementSchemaAuditEvent>();
            db.SchemaDiagnostics.SchemaChanged += (_, record) => changed.Add(record);
            db.SchemaDiagnostics.AuditEvent += (_, record) => audit.Add(record);

            db.Write(Point("cpu", 1, ("usage", FieldValue.FromLong(1))));
            string createdRevision = db.MeasurementSchemaRevision;
            db.Write(Point("cpu", 2,
                ("usage", FieldValue.FromDouble(2.5)),
                ("status", FieldValue.FromString("ok"))));
            revision = db.MeasurementSchemaRevision;

            Assert.NotEqual(createdRevision, revision);
            Assert.Equal(2, changed.Count);
            Assert.Equal(revision, changed[^1].Revision);
            Assert.Equal(MeasurementSchemaAuditKind.Evolved, changed[^1].Kind);
            Assert.Equal(1, changed[^1].AddedColumns);
            Assert.Equal(1, changed[^1].PromotedColumns);
            Assert.Equal(2, audit.Count);
            Assert.Equal(new MeasurementSchemaMetricsSnapshot(1, 1, 1, 0), db.SchemaDiagnostics.GetMetrics());
        }

        using var reopened = Open(new MeasurementSchemaPolicy());
        Assert.Equal(revision, reopened.MeasurementSchemaRevision);
        Assert.Equal(FieldType.Float64, reopened.Measurements.TryGet("cpu")!.TryGetColumn("usage")!.DataType);
    }

    [Fact]
    public async Task GetMeasurementSchemaSnapshot_ConcurrentEvolution_KeepsSchemaAndRevisionPaired()
    {
        using var db = Open(new MeasurementSchemaPolicy());
        db.Write(Point("cpu", 1, ("base", FieldValue.FromLong(1))));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var readerReady = new ManualResetEventSlim(false);
        var observedRevisions = new HashSet<string>(StringComparer.Ordinal);

        Task reader = Task.Run(() =>
        {
            readerReady.Set();
            for (int i = 0; i < 1_000; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                MeasurementSchemaSnapshot snapshot = db.GetMeasurementSchemaSnapshot();
                Assert.Equal(MeasurementSchemaCodec.ComputeRevision(snapshot.Measurements), snapshot.Revision);
                observedRevisions.Add(snapshot.Revision);
                Thread.Sleep(1);
            }
        }, cancellation.Token);

        Task writer = Task.Run(() =>
        {
            readerReady.Wait(cancellation.Token);
            for (int i = 0; i < 12; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                db.Write(Point("cpu", i + 2, ($"field_{i}", FieldValue.FromLong(i))));
                Thread.Sleep(2);
            }
        }, cancellation.Token);

        await Task.WhenAll(reader, writer).WaitAsync(TimeSpan.FromSeconds(35));
        Assert.True(observedRevisions.Count > 1);
    }

    private Tsdb Open(MeasurementSchemaPolicy policy)
        => Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            MeasurementSchemaPolicy = policy,
        });

    private static Point Point(string measurement, long timestamp, params (string Name, FieldValue Value)[] fields)
        => SonnetDB.Model.Point.Create(
            measurement,
            timestamp,
            new Dictionary<string, string>(),
            fields.ToDictionary(static field => field.Name, static field => field.Value, StringComparer.Ordinal));
}
