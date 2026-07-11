using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace SonnetDB.Parity.Adapters.Mongo;

/// <summary>
/// 使用 MongoDB 官方 .NET Driver 连接参考 MongoDB 容器的 Document parity 适配器。
/// </summary>
public sealed class MongoAdapter : IDocumentOps
{
    private readonly string _connectionString;
    private readonly string _databaseName;
    private readonly Dictionary<string, HashSet<string>> _ttlFields = new(StringComparer.Ordinal);
    private MongoClient _client;
    private IMongoDatabase _database;

    /// <summary>根据 <c>PARITY_MONGO_*</c> 环境变量创建适配器。</summary>
    public MongoAdapter()
    {
        _connectionString = Environment.GetEnvironmentVariable("PARITY_MONGO_URL") ?? "mongodb://127.0.0.1:27017";
        _databaseName = Environment.GetEnvironmentVariable("PARITY_MONGO_DB") ?? "sonnetdb_parity";
        _client = CreateClient();
        _database = _client.GetDatabase(_databaseName);
    }

    /// <inheritdoc />
    public string BackendName => "mongodb";

    /// <summary>探测参考 MongoDB 是否可达。</summary>
    public static async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(
                Environment.GetEnvironmentVariable("PARITY_MONGO_URL") ?? "mongodb://127.0.0.1:27017");
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            var client = new MongoClient(settings);
            await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1),
                cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ResetCollectionAsync(string collection, CancellationToken ct)
    {
        var names = await (await _database.ListCollectionNamesAsync(cancellationToken: ct).ConfigureAwait(false))
            .ToListAsync(ct).ConfigureAwait(false);
        if (names.Contains(collection, StringComparer.Ordinal))
            await _database.DropCollectionAsync(collection, ct).ConfigureAwait(false);
        await _database.CreateCollectionAsync(collection, cancellationToken: ct).ConfigureAwait(false);
        _ttlFields.Remove(collection);
    }

    /// <inheritdoc />
    public async Task InsertManyAsync(string collection, IReadOnlyList<DocumentParityRecord> documents, CancellationToken ct)
    {
        await Collection(collection).InsertManyAsync(
            documents.Select(document => ToBsonDocument(collection, document)).ToArray(),
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(string collection, DocumentParityRecord document, CancellationToken ct)
    {
        try
        {
            await Collection(collection).InsertOneAsync(ToBsonDocument(collection, document), cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentParityRecord>> FindAsync(
        string collection,
        DocumentParityQuery query,
        CancellationToken ct)
    {
        var find = Collection(collection).Find(ToFilter(query.Predicate));
        if (query.Projection is { Count: > 0 })
        {
            var projection = Builders<BsonDocument>.Projection.Include("_id");
            foreach (string path in query.Projection)
                projection = projection.Include(Field(path));
            find = find.Project<BsonDocument>(projection);
        }
        if (!string.IsNullOrWhiteSpace(query.SortPath))
        {
            var sort = query.Descending
                ? Builders<BsonDocument>.Sort.Descending(Field(query.SortPath))
                : Builders<BsonDocument>.Sort.Ascending(Field(query.SortPath));
            find = find.Sort(sort);
        }
        if (query.Limit is { } limit)
            find = find.Limit(limit);

        var rows = await find.ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToRecord).ToArray();
    }

    /// <inheritdoc />
    public async Task<int> UpdateAsync(
        string collection,
        DocumentParityPredicate predicate,
        DocumentParityUpdate update,
        bool many,
        CancellationToken ct)
    {
        var updates = new List<UpdateDefinition<BsonDocument>>();
        if (update.Set is not null)
            updates.AddRange(update.Set.Select(pair => Builders<BsonDocument>.Update.Set(Field(pair.Key), ToBsonValue(pair.Value))));
        if (update.Unset is not null)
            updates.AddRange(update.Unset.Select(path => Builders<BsonDocument>.Update.Unset(Field(path))));
        if (update.Increment is not null)
            updates.AddRange(update.Increment.Select(pair => Builders<BsonDocument>.Update.Inc(Field(pair.Key), Convert.ToDouble(pair.Value, System.Globalization.CultureInfo.InvariantCulture))));
        if (update.Rename is not null)
            updates.AddRange(update.Rename.Select(pair => Builders<BsonDocument>.Update.Rename(Field(pair.Key), Field(pair.Value))));
        if (update.Push is not null)
            updates.AddRange(update.Push.Select(pair => Builders<BsonDocument>.Update.Push(Field(pair.Key), ToBsonValue(pair.Value))));
        if (update.AddToSet is not null)
            updates.AddRange(update.AddToSet.Select(pair => Builders<BsonDocument>.Update.AddToSet(Field(pair.Key), ToBsonValue(pair.Value))));

        if (updates.Count == 0)
            throw new ArgumentException("Document parity update must contain at least one operator.", nameof(update));
        var combined = Builders<BsonDocument>.Update.Combine(updates);
        var result = many
            ? await Collection(collection).UpdateManyAsync(ToFilter(predicate), combined, cancellationToken: ct).ConfigureAwait(false)
            : await Collection(collection).UpdateOneAsync(ToFilter(predicate), combined, cancellationToken: ct).ConfigureAwait(false);
        return checked((int)result.ModifiedCount);
    }

    /// <inheritdoc />
    public async Task<int> DeleteAsync(string collection, DocumentParityPredicate predicate, bool many, CancellationToken ct)
    {
        var result = many
            ? await Collection(collection).DeleteManyAsync(ToFilter(predicate), ct).ConfigureAwait(false)
            : await Collection(collection).DeleteOneAsync(ToFilter(predicate), ct).ConfigureAwait(false);
        return checked((int)result.DeletedCount);
    }

    /// <inheritdoc />
    public async Task CreateIndexAsync(string collection, DocumentParityIndex index, CancellationToken ct)
    {
        var options = new CreateIndexOptions { Name = index.Name, Unique = index.Unique };
        if (index.TtlSeconds is { } ttlSeconds)
        {
            options.ExpireAfter = TimeSpan.FromSeconds(ttlSeconds);
            if (!_ttlFields.TryGetValue(collection, out var fields))
            {
                fields = new HashSet<string>(StringComparer.Ordinal);
                _ttlFields.Add(collection, fields);
            }
            fields.Add(Field(index.Path));
        }
        await Collection(collection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending(Field(index.Path)), options),
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentParityAggregateRow>> AggregateAsync(
        string collection,
        string groupPath,
        string averagePath,
        CancellationToken ct)
    {
        var pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = "$" + Field(groupPath),
                ["count"] = new BsonDocument("$sum", 1),
                ["average"] = new BsonDocument("$avg", "$" + Field(averagePath)),
            }),
            new BsonDocument("$sort", new BsonDocument("_id", 1)),
        };
        var rows = await Collection(collection).Aggregate<BsonDocument>(pipeline).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(static row => new DocumentParityAggregateRow(
            row["_id"].AsString,
            row["count"].ToInt64(),
            row["average"].ToDouble())).ToArray();
    }

    /// <inheritdoc />
    public Task<long> CountAsync(string collection, CancellationToken ct)
        => Collection(collection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);

    /// <inheritdoc />
    public async Task RestartAsync(CancellationToken ct)
    {
        _client = CreateClient();
        _database = _client.GetDatabase(_databaseName);
        await _database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DocumentParityIndexState> VerifyIndexAsync(string collection, string indexName, CancellationToken ct)
    {
        var indexes = await (await Collection(collection).Indexes.ListAsync(ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false);
        bool exists = indexes.Any(index => string.Equals(index["name"].AsString, indexName, StringComparison.Ordinal));
        var validation = await _database.RunCommandAsync<BsonDocument>(
            new BsonDocument { ["validate"] = collection, ["full"] = false },
            cancellationToken: ct).ConfigureAwait(false);
        long count = await CountAsync(collection, ct).ConfigureAwait(false);
        bool valid = !validation.TryGetValue("valid", out var value) || value.ToBoolean();
        return new DocumentParityIndexState(exists && valid, count, count);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private MongoClient CreateClient()
    {
        var settings = MongoClientSettings.FromConnectionString(_connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        return new MongoClient(settings);
    }

    private IMongoCollection<BsonDocument> Collection(string collection) => _database.GetCollection<BsonDocument>(collection);

    private BsonDocument ToBsonDocument(string collection, DocumentParityRecord record)
    {
        var document = BsonDocument.Parse(record.Json);
        document["_id"] = record.Id;
        if (_ttlFields.TryGetValue(collection, out var fields))
        {
            foreach (string field in fields)
            {
                if (!document.TryGetValue(field, out var value))
                    continue;
                if (value.IsInt64 || value.IsInt32)
                    document[field] = new BsonDateTime(value.ToInt64());
                else if (value.IsString && DateTimeOffset.TryParse(value.AsString, out var parsed))
                    document[field] = new BsonDateTime(parsed.UtcDateTime);
            }
        }
        return document;
    }

    private static FilterDefinition<BsonDocument> ToFilter(DocumentParityPredicate? predicate)
    {
        if (predicate is null)
            return FilterDefinition<BsonDocument>.Empty;
        string field = Field(predicate.Path);
        return predicate.Operator == DocumentParityOperator.Equal
            ? Builders<BsonDocument>.Filter.Eq(field, ToBsonValue(predicate.Value))
            : Builders<BsonDocument>.Filter.Gte(field, ToBsonValue(predicate.Value));
    }

    private static DocumentParityRecord ToRecord(BsonDocument source)
    {
        var document = source.DeepClone().AsBsonDocument;
        string id = document["_id"].AsString;
        document.Remove("_id");
        return new DocumentParityRecord(id, document.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson }));
    }

    private static string Field(string path) => path switch
    {
        "_id" or "id" => "_id",
        _ when path.StartsWith("$.", StringComparison.Ordinal) => path[2..],
        _ => path,
    };

    private static BsonValue ToBsonValue(object? value) => value switch
    {
        null => BsonNull.Value,
        string text => new BsonString(text),
        bool boolean => new BsonBoolean(boolean),
        int integer => new BsonInt32(integer),
        long integer => new BsonInt64(integer),
        float number => new BsonDouble(number),
        double number => new BsonDouble(number),
        decimal number => new BsonDecimal128(number),
        DateTimeOffset timestamp => new BsonDateTime(timestamp.UtcDateTime),
        _ => throw new NotSupportedException($"Document parity BSON scalar '{value.GetType().Name}' is not supported."),
    };
}
