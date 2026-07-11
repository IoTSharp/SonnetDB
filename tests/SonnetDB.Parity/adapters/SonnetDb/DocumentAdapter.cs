using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;

namespace SonnetDB.Parity.Adapters.SonnetDb;

/// <summary>
/// 通过 SonnetDB 嵌入式 Document API 执行参考 parity 场景。
/// </summary>
public sealed class DocumentAdapter : IDocumentOps
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-document-parity-" + Guid.NewGuid().ToString("N"));
    private Tsdb _database;

    /// <summary>创建隔离的临时 SonnetDB 数据库。</summary>
    public DocumentAdapter()
    {
        Directory.CreateDirectory(_root);
        _database = Open();
    }

    /// <inheritdoc />
    public string BackendName => "sonnetdb";

    /// <inheritdoc />
    public Task ResetCollectionAsync(string collection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _database.Documents.Drop(collection);
        _database.Documents.Create(DocumentCollectionSchema.Create(collection));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task InsertManyAsync(string collection, IReadOnlyList<DocumentParityRecord> documents, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = Store(collection).InsertMany(documents.Select(static document =>
            new DocumentWriteRequest(document.Id, document.Json)));
        if (result.HasErrors)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(static error => error.Message)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TryInsertAsync(string collection, DocumentParityRecord document, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = Store(collection).Insert(document.Id, document.Json);
        return Task.FromResult(!result.HasErrors);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentParityRecord>> FindAsync(string collection, DocumentParityQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var projection = query.Projection is { Count: > 0 }
            ? new DocumentProjection(query.Projection.Select(static path =>
                new DocumentProjectionField(FieldName(path), DocumentFieldRef.JsonPath(path))).ToArray())
            : null;
        var sort = string.IsNullOrWhiteSpace(query.SortPath)
            ? Array.Empty<DocumentSort>()
            : new[] { new DocumentSort(DocumentFieldRef.JsonPath(query.SortPath), query.Descending) };
        var result = DocumentQueryPlanner.Execute(
            Store(collection),
            Store(collection).Schema,
            new DocumentQuery(ToFilter(query.Predicate), projection, sort, query.Limit));
        return Task.FromResult<IReadOnlyList<DocumentParityRecord>>(result.Items
            .Select(static item => new DocumentParityRecord(item.Id, item.Json))
            .ToArray());
    }

    /// <inheritdoc />
    public Task<int> UpdateAsync(
        string collection,
        DocumentParityPredicate predicate,
        DocumentParityUpdate update,
        bool many,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var coreUpdate = new DocumentUpdate(
            Set: ToJsonMap(update.Set),
            Unset: update.Unset?.ToDictionary(static path => path, static _ => JsonValue(true), StringComparer.Ordinal),
            Inc: ToJsonMap(update.Increment),
            Rename: update.Rename,
            Push: ToJsonMap(update.Push),
            AddToSet: ToJsonMap(update.AddToSet));
        var result = many
            ? Store(collection).UpdateMany(ToFilter(predicate), coreUpdate)
            : Store(collection).UpdateOne(ToFilter(predicate), coreUpdate);
        return Task.FromResult(result.Modified);
    }

    /// <inheritdoc />
    public Task<int> DeleteAsync(string collection, DocumentParityPredicate predicate, bool many, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var filter = ToFilter(predicate);
        var ids = Store(collection).Scan()
            .Where(row => DocumentQueryPlanner.Matches(filter, row))
            .Select(static row => row.Id)
            .Take(many ? int.MaxValue : 1)
            .ToArray();
        return Task.FromResult(Store(collection).DeleteMany(ids));
    }

    /// <inheritdoc />
    public Task CreateIndexAsync(string collection, DocumentParityIndex index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _database.Documents.CreateIndex(collection, new DocumentPathIndexDefinition(
            index.Name,
            index.Path,
            IsUnique: index.Unique,
            TtlPath: index.TtlSeconds is null ? null : index.Path,
            TtlSeconds: index.TtlSeconds));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentParityAggregateRow>> AggregateAsync(
        string collection,
        string groupPath,
        string averagePath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = Store(collection).Aggregate(new DocumentAggregationPipeline([
            new DocumentGroupStage(
                [new DocumentAggregationGroupKey("group", DocumentFieldRef.JsonPath(groupPath))],
                [
                    new DocumentAggregationAccumulator("count", DocumentAggregationAccumulatorOperator.Count),
                    new DocumentAggregationAccumulator("average", DocumentAggregationAccumulatorOperator.Average, DocumentFieldRef.JsonPath(averagePath)),
                ]),
            new DocumentSortStage([new DocumentSort(DocumentFieldRef.JsonPath("$.group"))]),
        ]));
        return Task.FromResult<IReadOnlyList<DocumentParityAggregateRow>>(result.Documents.Select(ParseAggregate).ToArray());
    }

    /// <inheritdoc />
    public Task<long> CountAsync(string collection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult((long)Store(collection).Count());
    }

    /// <inheritdoc />
    public Task RestartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _database.Dispose();
        _database = Open();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DocumentParityIndexState> VerifyIndexAsync(string collection, string indexName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var report = Store(collection).VerifyIndexConsistency();
        var index = report.Indexes.Single(entry => string.Equals(entry.IndexName, indexName, StringComparison.Ordinal));
        return Task.FromResult(new DocumentParityIndexState(index.IsConsistent, report.DocumentCount, index.ActualEntries));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return ValueTask.CompletedTask;
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private DocumentCollectionStore Store(string collection) => _database.Documents.Open(collection);

    private static DocumentFilter? ToFilter(DocumentParityPredicate? predicate) => predicate is null
        ? null
        : new DocumentFieldFilter(
            predicate.Path is "_id" or "id" ? DocumentFieldRef.Id : DocumentFieldRef.JsonPath(predicate.Path),
            predicate.Operator == DocumentParityOperator.Equal
                ? DocumentFilterOperator.Equal
                : DocumentFilterOperator.GreaterThanOrEqual,
            predicate.Value);

    private static IReadOnlyDictionary<string, JsonElement>? ToJsonMap(IReadOnlyDictionary<string, object?>? values)
        => values?.ToDictionary(static pair => pair.Key, static pair => JsonValue(pair.Value), StringComparer.Ordinal);

    private static JsonElement JsonValue(object? value) => JsonSerializer.SerializeToElement(value);

    private static string FieldName(string path)
    {
        int separator = path.LastIndexOf('.');
        return separator < 0 ? path.TrimStart('$') : path[(separator + 1)..];
    }

    private static DocumentParityAggregateRow ParseAggregate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new DocumentParityAggregateRow(
            root.GetProperty("group").GetString() ?? string.Empty,
            root.GetProperty("count").GetInt64(),
            root.GetProperty("average").GetDouble());
    }
}
