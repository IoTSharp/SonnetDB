using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.VectorData;
using SonnetDB.Data.VectorData.Internal;

namespace SonnetDB.Data.VectorData;

internal sealed class SonnetDBDynamicVectorCollection
    : VectorStoreCollection<object, Dictionary<string, object?>>
{
    private readonly SndbConnection _connection;
    private readonly VectorStoreCollectionMetadata _metadata;
    private readonly string _keyName;
    private readonly string _keyStorageName;
    private readonly string _vectorName;
    private readonly string _vectorStorageName;
    private readonly string _vectorJsonPath;
    private readonly Dictionary<string, string> _storageToProperty;
    private readonly Dictionary<string, string> _propertyToStorage;
    private readonly string? _distanceFunction;

    public SonnetDBDynamicVectorCollection(
        SndbConnection connection,
        string name,
        VectorStoreCollectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(definition);
        _connection = connection;
        Name = name;

        string? keyName = null;
        string? keyStorageName = null;
        string? vectorName = null;
        string? vectorStorageName = null;
        var propertyToStorage = new Dictionary<string, string>(StringComparer.Ordinal);
        var storageToProperty = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in definition.Properties)
        {
            switch (property)
            {
                case VectorStoreKeyProperty:
                    keyName = keyName is null ? property.Name : throw new InvalidOperationException("VectorStoreCollectionDefinition 中存在多个 Key 属性。");
                    keyStorageName = string.IsNullOrEmpty(property.StorageName) ? property.Name : property.StorageName;
                    break;
                case VectorStoreVectorProperty vectorProperty:
                    vectorName = vectorName is null ? property.Name : throw new InvalidOperationException("VectorStoreCollectionDefinition 中存在多个 Vector 属性。");
                    vectorStorageName = string.IsNullOrEmpty(property.StorageName) ? property.Name : property.StorageName;
                    _distanceFunction = vectorProperty.DistanceFunction;
                    break;
                case VectorStoreDataProperty dataProperty:
                    var storageName = string.IsNullOrEmpty(dataProperty.StorageName) ? dataProperty.Name : dataProperty.StorageName;
                    propertyToStorage[dataProperty.Name] = storageName;
                    storageToProperty[storageName] = dataProperty.Name;
                    break;
            }
        }

        _keyName = keyName ?? throw new InvalidOperationException("VectorStoreCollectionDefinition 必须包含一个 Key 属性。");
        _keyStorageName = keyStorageName ?? _keyName;
        _vectorName = vectorName ?? throw new InvalidOperationException("VectorStoreCollectionDefinition 必须包含一个 Vector 属性。");
        _vectorStorageName = vectorStorageName ?? _vectorName;
        _vectorJsonPath = "$." + _vectorStorageName;
        _propertyToStorage = propertyToStorage;
        _storageToProperty = storageToProperty;
        _metadata = new VectorStoreCollectionMetadata
        {
            VectorStoreSystemName = "sonnetdb",
            CollectionName = name,
        };
    }

    public override string Name { get; }

    public override async Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SHOW DOCUMENT COLLECTIONS";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(0), Name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public override async Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CREATE DOCUMENT COLLECTION IF NOT EXISTS {SqlVectorStoreHelpers.FormatIdentifier(Name)}";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
            return;

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DROP DOCUMENT COLLECTION {SqlVectorStoreHelpers.FormatIdentifier(Name)}";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task UpsertAsync(Dictionary<string, object?> record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"INSERT INTO {SqlVectorStoreHelpers.FormatIdentifier(Name)} (id, document) VALUES (@id, @document)";
        cmd.Parameters.AddWithValue("@id", Convert.ToString(GetKey(record), System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@document", ToJson(record));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task UpsertAsync(IEnumerable<Dictionary<string, object?>> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
            await UpsertAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public override async Task DeleteAsync(object key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {SqlVectorStoreHelpers.FormatIdentifier(Name)} WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task DeleteAsync(IEnumerable<object> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            if (key is not null)
                await DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<Dictionary<string, object?>?> GetAsync(
        object key,
        RecordRetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT id, document FROM {SqlVectorStoreHelpers.FormatIdentifier(Name)} WHERE id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? FromJson(reader.GetString(0), reader.GetString(1), options?.IncludeVectors == true)
            : null;
    }

    public override async IAsyncEnumerable<Dictionary<string, object?>> GetAsync(
        IEnumerable<object> keys,
        RecordRetrievalOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            if (key is null)
                continue;
            var record = await GetAsync(key, options, cancellationToken).ConfigureAwait(false);
            if (record is not null)
                yield return record;
        }
    }

    public override IAsyncEnumerable<Dictionary<string, object?>> GetAsync(
        System.Linq.Expressions.Expression<Func<Dictionary<string, object?>, bool>> filter,
        int top,
        FilteredRecordRetrievalOptions<Dictionary<string, object?>>? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SonnetDB 动态 VectorData collection 暂不支持 LINQ Filter。");

    public override async IAsyncEnumerable<VectorSearchResult<Dictionary<string, object?>>> SearchAsync<TInput>(
        TInput searchValue,
        int top,
        VectorSearchOptions<Dictionary<string, object?>>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchValue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);
        if (options?.Filter is not null)
            throw new NotSupportedException("SonnetDB 动态 VectorData collection 暂不支持 LINQ Filter。");

        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        var query = SqlVectorStoreHelpers.FormatVectorLiteral(SqlVectorStoreHelpers.ExtractVector(searchValue));
        var metric = DistanceFunctionMapper.ToKnnMetric(_distanceFunction);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT id, document, vector_distance() AS distance FROM vector_search(" +
            $"source => '{SqlVectorStoreHelpers.EscapeSqlString(Name)}', " +
            $"vector_field => '{SqlVectorStoreHelpers.EscapeSqlString(_vectorJsonPath)}', " +
            $"vector => {query}, k => {top}, metric => '{metric}')";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var distance = Convert.ToDouble(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture);
            yield return new VectorSearchResult<Dictionary<string, object?>>(
                FromJson(reader.GetString(0), reader.GetString(1), options?.IncludeVectors == true),
                distance);
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(VectorStoreCollectionMetadata)
            ? _metadata
            : null;
    }

    private string ToJson(Dictionary<string, object?> record)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        payload[_vectorStorageName] = SqlVectorStoreHelpers.ExtractVector(GetVector(record));
        foreach (var (property, storage) in _propertyToStorage)
        {
            if (record.TryGetValue(property, out var value))
                payload[storage] = value;
        }

        return JsonSerializer.Serialize(payload, VectorJsonSerializerContext.Default.DictionaryStringObject);
    }

    private Dictionary<string, object?> FromJson(string id, string json, bool includeVector)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [_keyName] = id,
        };
        using var document = JsonDocument.Parse(json);
        if (includeVector && document.RootElement.TryGetProperty(_vectorStorageName, out var vector))
            record[_vectorName] = vector.EnumerateArray().Select(static item => item.GetSingle()).ToArray();
        foreach (var (storage, property) in _storageToProperty)
        {
            if (document.RootElement.TryGetProperty(storage, out var value))
                record[property] = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        }

        return record;
    }

    private object GetKey(Dictionary<string, object?> record)
        => record.TryGetValue(_keyName, out var key) && key is not null
            ? key
            : throw new InvalidOperationException($"动态记录缺少键字段 '{_keyName}'。");

    private object GetVector(Dictionary<string, object?> record)
        => record.TryGetValue(_vectorName, out var vector) && vector is not null
            ? vector
            : throw new InvalidOperationException($"动态记录缺少向量字段 '{_vectorName}'。");

    private async ValueTask EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_connection.State != System.Data.ConnectionState.Open)
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
