using System.Runtime.InteropServices;
using SonnetDB.Documents;
using SonnetDB.Documents.Vector;
using SonnetDB.Kv;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentVectorGraphRebuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-graph-rebuild-" + Guid.NewGuid().ToString("N"));
    private static readonly DocumentVectorIndex Definition = DocumentCollectionSchema.Create(
        "docs", vectorIndexes: [new("vector", "$.v", 3)]).VectorIndexes[0];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(300)]
    public async Task StartGraphRebuild_PagedInput_PublishesExactCountsAndPreservesPersistence(int count)
    {
        using (var store = Open())
        {
            for (int i = 0; i < count; i++) store.Upsert(Row(i.ToString()));
            var operation = store.StartGraphRebuild();
            var result = await operation.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("completed", result.State);
            Assert.Equal(count, result.ScannedEntries);
            Assert.Equal(count, result.IndexedVectors);
            Assert.NotNull(result.CompletedAtUtc);
            Assert.Null(result.ErrorCode);
            Assert.Equal(count, store.GetHealth().GraphVectorCount);
            Assert.Equal(count, store.Count);
        }
        using var reopened = Open();
        Assert.Equal(count, reopened.GetHealth().GraphVectorCount);
        Assert.Equal(count, reopened.Count);
    }

    [Fact]
    public async Task StartGraphRebuild_CancelBeforePublish_PreservesOldGraphAndAllowsRetry()
    {
        using var store = Open();
        store.Upsert(Row("a"));
        using var cancellation = new CancellationTokenSource();
        store.GraphRebuildPreparedTestHook = cancellation.Cancel;
        var operation = store.StartGraphRebuild(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Completion);
        Assert.Equal("canceled", operation.Progress.State);
        Assert.Equal("operation_canceled", operation.Progress.ErrorCode);
        Assert.Equal(1, operation.Progress.IndexedVectors);
        Assert.NotNull(operation.Progress.CompletedAtUtc);
        Assert.True(operation.Completion.IsCanceled);
        Assert.Equal("a", Assert.Single(store.Search([1, 0, 0], 1)).Id);
        store.GraphRebuildPreparedTestHook = null;
        Assert.Equal("completed", (await store.StartGraphRebuild().Completion).State);
    }

    [Fact]
    public async Task StartGraphRebuild_SnapshotBudgetFailure_PreservesOldGraphAndPropagatesException()
    {
        using var store = Open(new() { MaxSnapshotOverlayEntries = 1, AutoCheckpointEnabled = false });
        store.Upsert(Row("a"));
        store.Upsert(Row("b"));
        var operation = store.StartGraphRebuild();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Completion);
        Assert.Contains("MaxSnapshotOverlayEntries", error.Message);
        Assert.Equal("failed", operation.Progress.State);
        Assert.Equal("graph_rebuild_failed", operation.Progress.ErrorCode);
        Assert.NotNull(operation.Progress.CompletedAtUtc);
        Assert.Equal(2, store.Search([1, 0, 0], 2).Count);
        Assert.Equal(2, store.GetHealth().GraphVectorCount);
    }

    [Fact]
    public async Task StartGraphRebuild_CorruptPersistedVector_RejectsCandidateAndPreservesOldGraph()
    {
        using (var keyspace = KvKeyspace.Open("docvector.vector", _root, new()))
            keyspace.Put("z-bad", MemoryMarshal.AsBytes(new float[] { 1, 0 }.AsSpan()));
        using var store = Open();
        store.Upsert(Row("a-good"));
        var operation = store.StartGraphRebuild();
        await Assert.ThrowsAsync<InvalidDataException>(() => operation.Completion);
        Assert.Equal("failed", operation.Progress.State);
        Assert.Equal("invalid_vector_data", operation.Progress.ErrorCode);
        Assert.Equal(2, operation.Progress.ScannedEntries);
        Assert.Equal(1, operation.Progress.IndexedVectors);
        Assert.Equal("a-good", Assert.Single(store.Search([1, 0, 0], 2)).Id);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task StartGraphRebuild_CorruptPersistedKey_RejectsCandidateAndPreservesOldGraph()
    {
        using (var keyspace = KvKeyspace.Open("docvector.vector", _root, new()))
            keyspace.Put(new byte[] { 0xff }, MemoryMarshal.AsBytes(new float[] { 1, 0, 0 }.AsSpan()));
        using var store = Open();
        store.Upsert(Row("a-good"));
        var operation = store.StartGraphRebuild();
        await Assert.ThrowsAsync<InvalidDataException>(() => operation.Completion);
        Assert.Equal("failed", operation.Progress.State);
        Assert.Equal("invalid_vector_data", operation.Progress.ErrorCode);
        Assert.Equal(2, operation.Progress.ScannedEntries);
        Assert.Equal(1, operation.Progress.IndexedVectors);
        Assert.Equal(2, store.GetHealth().GraphVectorCount);
        Assert.Contains(store.Search([1, 0, 0], 2), hit => hit.Id == "a-good");
    }

    [Fact]
    public async Task StartGraphRebuild_IoFailureBeforePublish_PropagatesOriginalFailure()
    {
        using var store = Open();
        store.Upsert(Row("a"));
        var expected = new IOException("injected storage failure");
        store.GraphRebuildPreparedTestHook = () => throw expected;
        var operation = store.StartGraphRebuild();
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => operation.Completion));
        Assert.Equal("storage_error", operation.Progress.ErrorCode);
        Assert.Equal("a", Assert.Single(store.Search([1, 0, 0], 1)).Id);
    }

    [Fact]
    public async Task StartGraphRebuild_WhileBuilding_ExposesProgressAndRejectsOverlappingRebuild()
    {
        using var store = Open();
        store.Upsert(Row("a"));
        using var prepared = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.GraphRebuildPreparedTestHook = () =>
        {
            prepared.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var operation = store.StartGraphRebuild();
        try
        {
            Assert.True(prepared.Wait(TimeSpan.FromSeconds(15)));
            Assert.False(operation.Completion.IsCompleted);
            Assert.Equal("building", operation.Progress.State);
            Assert.Equal(1, operation.Progress.ScannedEntries);
            Assert.Equal(1, operation.Progress.IndexedVectors);
            Assert.Null(operation.Progress.CompletedAtUtc);
            Assert.Throws<InvalidOperationException>(() => store.StartGraphRebuild());
        }
        finally { release.Set(); }
        Assert.Equal("completed", (await operation.Completion).State);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("delete")]
    [InlineData("dispose")]
    [InlineData("drop_index")]
    public async Task StartGraphRebuild_ConcurrentCollectionMutation_SerializesWithoutLosingChanges(string mutation)
    {
        using var vectors = Open();
        using var documents = new DocumentCollectionStore(
            DocumentCollectionSchema.Create("docs", vectorIndexes: [new("vector", "$.v", 3)]),
            KvKeyspace.Open("docs", Path.Combine(_root, "documents"), new()),
            _ => throw new InvalidOperationException("No full text index expected."),
            _ => vectors);
        documents.Upsert("a", Row("a").Json);
        using var prepared = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var mutationStarted = new ManualResetEventSlim();
        vectors.GraphRebuildPreparedTestHook = () =>
        {
            prepared.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var operation = documents.StartVectorGraphRebuild("vector", default);
        Task? pendingMutation = null;
        try
        {
            Assert.True(prepared.Wait(TimeSpan.FromSeconds(15)));
            pendingMutation = Task.Run(() =>
            {
                mutationStarted.Set();
                switch (mutation)
                {
                    case "write": documents.Upsert("b", Row("b").Json); break;
                    case "delete": documents.Delete("a"); break;
                    case "dispose": documents.Dispose(); break;
                    case "drop_index": documents.ApplySchema(documents.Schema.WithoutVectorIndex("vector")); break;
                }
            });
            Assert.True(mutationStarted.Wait(TimeSpan.FromSeconds(15)));
            Assert.False(pendingMutation.IsCompleted);
            Assert.Equal("building", operation.Progress.State);
        }
        finally { release.Set(); }
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        await pendingMutation!.WaitAsync(TimeSpan.FromSeconds(15));
        switch (mutation)
        {
            case "write": Assert.Equal(2, vectors.Search([1, 0, 0], 2).Count); break;
            case "delete": Assert.Empty(vectors.Search([1, 0, 0], 1)); break;
            case "dispose": Assert.Throws<ObjectDisposedException>(() => vectors.StartGraphRebuild()); break;
            case "drop_index": Assert.Null(documents.Schema.TryGetVectorIndex("vector")); break;
        }
    }

    [Fact]
    public async Task StartGraphRebuild_CanceledWhileWaitingForIndexLock_CompletesWithoutTakingLock()
    {
        using var store = Open();
        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        IEnumerable<DocumentRow> HoldLock()
        {
            locked.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            yield break;
        }
        Task holder = Task.Run(() => store.UpsertMany(HoldLock()));
        try
        {
            Assert.True(locked.Wait(TimeSpan.FromSeconds(15)));
            var operation = store.StartGraphRebuild(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => operation.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("canceled", operation.Progress.State);
            Assert.Equal(0, operation.Progress.ScannedEntries);
            Assert.True(operation.Completion.IsCanceled);
            Assert.False(holder.IsCompleted);
        }
        finally { release.Set(); }
        await holder;
    }

    [Fact]
    public async Task StartGraphRebuild_IndexDisposedBeforeLock_FailsWithoutPublishingCandidate()
    {
        using var store = Open();
        store.Upsert(Row("a"));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.GraphRebuildStartingTestHook = () =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var operation = store.StartGraphRebuild();
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(15)));
            store.Dispose();
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => operation.Completion);
        Assert.Equal("failed", operation.Progress.State);
        Assert.Equal("index_disposed", operation.Progress.ErrorCode);
        Assert.Equal(0, operation.Progress.ScannedEntries);
        Assert.True(operation.Completion.IsFaulted);
    }

    [Fact]
    public async Task StartGraphRebuild_CancelAfterCompletion_DoesNotChangePublishedOutcome()
    {
        using var store = Open();
        store.Upsert(Row("a"));
        using var cancellation = new CancellationTokenSource();
        var operation = store.StartGraphRebuild(cancellation.Token);
        var result = await operation.Completion;
        cancellation.Cancel();
        Assert.Equal(result, operation.Progress);
        Assert.Equal("completed", operation.Progress.State);
    }

    [Fact]
    public void StartGraphRebuild_PreCanceledOrDisposed_DoesNotScheduleRebuild()
    {
        using var store = Open();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.StartGraphRebuild(canceled.Token));
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.StartGraphRebuild());
    }

    private DocumentVectorIndexStore Open(KvOptions? options = null)
        => DocumentVectorIndexStore.Open(_root, Definition, options ?? new());

    private static DocumentRow Row(string id) => new(id, "{\"v\":[1,0,0]}", 0);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
