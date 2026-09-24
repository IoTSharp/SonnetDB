using Microsoft.Extensions.VectorData;
using SonnetDB.Data;
using SonnetDB.Data.VectorData;

namespace SonnetDB.Core.Tests.Ado;

public sealed class SonnetDBVectorSearchApiTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-vectordata-api-" + Guid.NewGuid().ToString("N"));

    public SonnetDBVectorSearchApiTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task SearchAsync_WithExactThresholdAndInclude_ReturnsBoundedMatches()
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        using var store = new SonnetDBVectorStore(connection);
        var collection = store.GetCollection<string, VectorRecord>("vectors");
        await collection.EnsureCollectionExistsAsync();
        await collection.UpsertAsync(
        [
            new VectorRecord { Id = "near", Category = "a", Embedding = [1, 0, 0] },
            new VectorRecord { Id = "mid", Category = "a", Embedding = [0.95f, 0.31f, 0] },
            new VectorRecord { Id = "far", Category = "b", Embedding = [0, 1, 0] },
        ]);

        var options = new SndbVectorSearchOptions<VectorRecord>
        {
            TopK = 3,
            Exact = true,
            IncludeVectors = true,
            ScoreThreshold = 0.01,
            Filter = record => record.Category == "a",
        };
        var results = await collection
            .SearchAsync(new ReadOnlyMemory<float>([1, 0, 0]), options)
            .ToArrayAsync();

        Assert.Equal(["near"], results.Select(static result => result.Record.Id).ToArray());
        Assert.Equal([1, 0, 0], results[0].Record.Embedding);
        Assert.InRange(results[0].Score ?? double.NaN, 0, 0.000001);
    }

    [Fact]
    public async Task SearchBatchAsync_WithAccuratePreset_PreservesQueryOrder()
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        using var store = new SonnetDBVectorStore(connection);
        var collection = store.GetCollection<string, VectorRecord>("vectors");
        await collection.EnsureCollectionExistsAsync();
        await collection.UpsertAsync(
        [
            new VectorRecord { Id = "x", Category = "a", Embedding = [1, 0, 0] },
            new VectorRecord { Id = "y", Category = "a", Embedding = [0, 1, 0] },
        ]);

        var options = new SndbVectorSearchOptions<VectorRecord>
        {
            TopK = 1,
            Extensions = new SndbVectorSearchExtensionOptions
            {
                Preset = SndbVectorSearchPreset.Accurate,
                MaxBatchSize = 2,
            },
        };
        var results = await collection.SearchBatchAsync(
            new[]
            {
                new ReadOnlyMemory<float>([1, 0, 0]),
                new ReadOnlyMemory<float>([0, 1, 0]),
            },
            options);

        Assert.Equal([0, 1], results.Select(static result => result.QueryIndex).ToArray());
        Assert.Equal(["x", "y"], results.Select(static result => result.Hits.Single().Record.Id).ToArray());
    }

    [Fact]
    public async Task SearchBatchAsync_OverMaxBatchSize_RejectsBeforeExecutingNextQuery()
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        using var store = new SonnetDBVectorStore(connection);
        var collection = store.GetCollection<string, VectorRecord>("vectors");
        await collection.EnsureCollectionExistsAsync();
        var options = new SndbVectorSearchOptions<VectorRecord>
        {
            TopK = 1,
            MaxBatchSize = 1,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => collection.SearchBatchAsync(
                new[]
                {
                    new ReadOnlyMemory<float>([1, 0, 0]),
                    new ReadOnlyMemory<float>([0, 1, 0]),
                },
                options));

        Assert.Contains("MaxBatchSize", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchBatchAsync_PreCancelledEmptyInput_RejectsBeforeEnumerationOrQuery()
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        using var store = new SonnetDBVectorStore(connection);
        var collection = store.GetCollection<string, VectorRecord>("vectors");
        var inputs = new EmptyBatchInputs();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collection.SearchBatchAsync(
            (IEnumerable<ReadOnlyMemory<float>>)inputs,
            new SndbVectorSearchOptions<VectorRecord>(), cancelled.Token));

        Assert.Equal(cancelled.Token, exception.CancellationToken);
        Assert.Equal(0, inputs.EnumeratorRequests);
        Assert.Equal(0, inputs.MoveNextCalls);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task SearchBatchAsync_PreCancelledEmptyAsyncInput_RejectsBeforeEnumerationOrQuery()
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        using var store = new SonnetDBVectorStore(connection);
        var collection = store.GetCollection<string, VectorRecord>("vectors");
        var inputs = new EmptyBatchInputs();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collection.SearchBatchAsync(
            (IAsyncEnumerable<ReadOnlyMemory<float>>)inputs,
            new SndbVectorSearchOptions<VectorRecord>(), cancelled.Token));

        Assert.Equal(cancelled.Token, exception.CancellationToken);
        Assert.Equal(0, inputs.EnumeratorRequests);
        Assert.Equal(0, inputs.MoveNextCalls);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private sealed class EmptyBatchInputs : IEnumerable<ReadOnlyMemory<float>>,
        IAsyncEnumerable<ReadOnlyMemory<float>>, IAsyncEnumerator<ReadOnlyMemory<float>>
    {
        public int EnumeratorRequests { get; private set; }
        public int MoveNextCalls { get; private set; }
        public ReadOnlyMemory<float> Current => throw new InvalidOperationException("Empty input has no current item.");

        public IEnumerator<ReadOnlyMemory<float>> GetEnumerator()
        {
            EnumeratorRequests++;
            return Enumerable.Empty<ReadOnlyMemory<float>>().GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public IAsyncEnumerator<ReadOnlyMemory<float>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            EnumeratorRequests++;
            return this;
        }

        public ValueTask<bool> MoveNextAsync()
        {
            MoveNextCalls++;
            return ValueTask.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class VectorRecord
    {
        [VectorStoreKey]
        public string Id { get; set; } = string.Empty;

        [VectorStoreData]
        public string Category { get; set; } = string.Empty;

        [VectorStoreVector(3, DistanceFunction = DistanceFunction.CosineDistance)]
        public float[] Embedding { get; set; } = [];
    }
}
