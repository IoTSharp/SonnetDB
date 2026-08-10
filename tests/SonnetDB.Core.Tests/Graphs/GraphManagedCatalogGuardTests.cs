using SonnetDB.Catalog;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;
using SonnetDB.Views;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphManagedCatalogGuardTests : IDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-graph-managed-catalog-" + Guid.NewGuid().ToString("N"));

    public GraphManagedCatalogGuardTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ManagedCatalogs_AfterGraphExists_RejectDirectAddLoadOrReplaceAndRemove()
    {
        using Tsdb database = OpenDatabase("managed");
        database.CreateMeasurement(CreateMeasurement("metric"));
        database.Views.Create(ViewDefinition.Create("logical_view", "SELECT 1 AS value"));
        database.MaterializedViews.Create(
            MaterializedViewDefinition.Create("snapshot_view", "SELECT 1 AS value"));
        database.Documents.Create(DocumentCollectionSchema.Create("documents"));
        database.Graphs.Create("reserved_graph");

        AssertManagedMutationRejected(
            "Tsdb",
            () => database.Measurements.Add(CreateMeasurement("reserved_graph")));
        AssertManagedMutationRejected(
            "Tsdb",
            () => database.Measurements.LoadOrReplace(CreateMeasurement("metric")));
        AssertManagedMutationRejected("Tsdb", () => database.Measurements.Remove("metric"));

        AssertManagedMutationRejected(
            "ViewManager",
            () => database.Views.Catalog.Add(ViewDefinition.Create("reserved_graph", "SELECT 1 AS value")));
        AssertManagedMutationRejected(
            "ViewManager",
            () => database.Views.Catalog.LoadOrReplace(
                ViewDefinition.Create("logical_view", "SELECT 2 AS value")));
        AssertManagedMutationRejected("ViewManager", () => database.Views.Catalog.Remove("logical_view"));

        AssertManagedMutationRejected(
            "MaterializedViewManager",
            () => database.MaterializedViews.Catalog.Add(
                MaterializedViewDefinition.Create("reserved_graph", "SELECT 1 AS value")));
        AssertManagedMutationRejected(
            "MaterializedViewManager",
            () => database.MaterializedViews.Catalog.LoadOrReplace(
                MaterializedViewDefinition.Create("snapshot_view", "SELECT 2 AS value")));
        AssertManagedMutationRejected(
            "MaterializedViewManager",
            () => database.MaterializedViews.Catalog.Remove("snapshot_view"));

        AssertManagedMutationRejected(
            "DocumentCollectionManager",
            () => database.Documents.Catalog.Add(DocumentCollectionSchema.Create("reserved_graph")));
        AssertManagedMutationRejected(
            "DocumentCollectionManager",
            () => database.Documents.Catalog.LoadOrReplace(
                DocumentCollectionSchema.Create(
                    "documents",
                    indexes: [new DocumentPathIndexDefinition("idx_kind", "$.kind")])));
        AssertManagedMutationRejected(
            "DocumentCollectionManager",
            () => database.Documents.Catalog.Remove("documents"));

        Assert.True(database.Measurements.Contains("metric"));
        Assert.NotNull(database.Views.Catalog.TryGet("logical_view"));
        Assert.NotNull(database.MaterializedViews.Catalog.TryGet("snapshot_view"));
        Assert.NotNull(database.Documents.Catalog.TryGet("documents"));
    }

    [Fact]
    public void StandaloneCatalogs_AddLoadOrReplaceAndRemove_RemainWritable()
    {
        var measurements = new MeasurementCatalog();
        measurements.Add(CreateMeasurement("metric"));
        measurements.LoadOrReplace(CreateMeasurement("metric"));
        Assert.True(measurements.Remove("metric"));

        var views = new ViewCatalog();
        views.Add(ViewDefinition.Create("logical_view", "SELECT 1 AS value"));
        views.LoadOrReplace(ViewDefinition.Create("logical_view", "SELECT 2 AS value"));
        Assert.True(views.Remove("logical_view"));

        var materializedViews = new MaterializedViewCatalog();
        materializedViews.Add(MaterializedViewDefinition.Create("snapshot_view", "SELECT 1 AS value"));
        materializedViews.LoadOrReplace(
            MaterializedViewDefinition.Create("snapshot_view", "SELECT 2 AS value"));
        Assert.True(materializedViews.Remove("snapshot_view"));

        var documents = new DocumentCollectionCatalog();
        documents.Add(DocumentCollectionSchema.Create("documents"));
        documents.LoadOrReplace(DocumentCollectionSchema.Create(
            "documents",
            indexes: [new DocumentPathIndexDefinition("idx_kind", "$.kind")]));
        Assert.True(documents.Remove("documents"));
    }

    [Fact]
    public void ManagedSchemaApis_CreateRefreshAlterAndDrop_RemainWritable()
    {
        using Tsdb database = OpenDatabase("normal-paths");

        database.CreateMeasurement(CreateMeasurement("metric"));
        Assert.True(database.DropMeasurement("metric"));

        database.Views.Create(ViewDefinition.Create("logical_view", "SELECT 1 AS value"));
        Assert.True(database.Views.Drop("logical_view"));

        database.MaterializedViews.Create(
            MaterializedViewDefinition.Create("snapshot_view", "SELECT 1 AS value"));
        SelectExecutionResult result = database.MaterializedViews.Refresh(
            "snapshot_view",
            static () => new SelectExecutionResult(["value"], [new object?[] { 1L }]));
        Assert.Equal(1L, Assert.Single(Assert.Single(result.Rows)));
        Assert.True(database.MaterializedViews.Drop("snapshot_view"));

        database.Documents.Create(DocumentCollectionSchema.Create("documents"));
        _ = database.Documents.CreateIndex(
            "documents",
            new DocumentPathIndexDefinition("idx_kind", "$.kind"));
        _ = database.Documents.CreateFullTextIndex(
            "documents",
            new DocumentFullTextIndexDefinition("ft_body", ["$.body"]));
        _ = database.Documents.CreateVectorIndex(
            "documents",
            new DocumentVectorIndexDefinition("vec_embedding", "$.embedding", 2));
        _ = database.Documents.SetValidator(
            "documents",
            new DocumentValidatorDefinition([
                new DocumentValidatorRuleDefinition("$.kind", Required: true),
            ]));

        Assert.True(database.Documents.DropValidator("documents"));
        Assert.True(database.Documents.DropIndex("documents", "idx_kind"));
        Assert.True(database.Documents.DropFullTextIndex("documents", "ft_body"));
        Assert.True(database.Documents.DropVectorIndex("documents", "vec_embedding"));
        Assert.True(database.Documents.Drop("documents"));
    }

    [Fact]
    public async Task GraphCreate_ConcurrentDocumentCreate_SerializesAndOnlyGraphCommits()
    {
        using Tsdb database = OpenDatabase("concurrent-graph-document");
        using var graphCatalogPersisted = new ManualResetEventSlim();
        using var releaseGraphCreate = new ManualResetEventSlim();
        database.Graphs.AfterCatalogPersistedBeforePublishTestHook = () =>
        {
            graphCatalogPersisted.Set();
            if (!releaseGraphCreate.Wait(OperationTimeout))
                throw new TimeoutException("测试未能释放 graph CREATE。");
        };

        Task graphTask = Task.Run(() => database.Graphs.Create("shared"));
        Assert.True(graphCatalogPersisted.Wait(OperationTimeout));
        Task documentTask = Task.Run(() =>
            database.Documents.Create(DocumentCollectionSchema.Create("shared")));
        try
        {
            Task first = await Task.WhenAny(documentTask, Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(documentTask, first);
        }
        finally
        {
            releaseGraphCreate.Set();
        }

        await graphTask.WaitAsync(OperationTimeout);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await documentTask.WaitAsync(OperationTimeout));
        Assert.Contains("graph", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(database.Documents.Catalog.TryGet("shared"));
        Assert.NotNull(database.Graphs.Catalog.TryGet("shared"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AssertManagedMutationRejected(string owner, Action mutation)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(mutation);
        Assert.Contains(owner, error.Message, StringComparison.Ordinal);
    }

    private Tsdb OpenDatabase(string directoryName)
        => Tsdb.Open(new TsdbOptions
        {
            RootDirectory = Path.Combine(_root, directoryName),
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Kv = new KvOptions
            {
                AutoCheckpointEnabled = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            },
        });

    private static MeasurementSchema CreateMeasurement(string name)
        => MeasurementSchema.Create(
            name,
            [new MeasurementColumn("value", MeasurementColumnRole.Field, FieldType.Int64)]);
}
