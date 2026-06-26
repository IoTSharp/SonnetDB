using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.VectorData;
using SonnetDB.Data.VectorData.Internal;

namespace SonnetDB.Data.VectorData;

/// <summary>
/// 基于 SonnetDB document collection 的 VectorData collection。
/// </summary>
/// <typeparam name="TKey">主键类型。</typeparam>
/// <typeparam name="TRecord">记录类型。</typeparam>
[RequiresUnreferencedCode("SonnetDBVectorCollection 通过反射访问 TRecord 的属性，可能被 trim 移除。")]
[RequiresDynamicCode("SonnetDBVectorCollection 通过反射访问 TRecord 的属性，AOT 下可能不可用。")]
public sealed class SonnetDBVectorCollection<TKey, TRecord> : VectorStoreCollection<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    private readonly SndbConnection _connection;
    private readonly SonnetDBVectorRecordMapper<TKey, TRecord> _mapper;
    private readonly VectorStoreCollectionMetadata _metadata;

    /// <summary>
    /// 初始化 <see cref="SonnetDBVectorCollection{TKey, TRecord}"/>。
    /// </summary>
    public SonnetDBVectorCollection(SndbConnection connection, string name)
        : this(connection, name, definition: null)
    {
    }

    /// <summary>
    /// 初始化 <see cref="SonnetDBVectorCollection{TKey, TRecord}"/>。
    /// </summary>
    public SonnetDBVectorCollection(
        SndbConnection connection,
        string name,
        VectorStoreCollectionDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(name);
        _connection = connection;
        Name = name;
        _mapper = definition is null
            ? new SonnetDBVectorRecordMapper<TKey, TRecord>()
            : new SonnetDBVectorRecordMapper<TKey, TRecord>(definition);
        _metadata = new VectorStoreCollectionMetadata
        {
            VectorStoreSystemName = "sonnetdb",
            CollectionName = name,
        };
    }

    /// <inheritdoc/>
    public override string Name { get; }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public override async Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CREATE DOCUMENT COLLECTION IF NOT EXISTS {SqlVectorStoreHelpers.FormatIdentifier(Name)}";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
            return;

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DROP DOCUMENT COLLECTION {SqlVectorStoreHelpers.FormatIdentifier(Name)}";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"INSERT INTO {SqlVectorStoreHelpers.FormatIdentifier(Name)} (id, document) VALUES (@id, @document)";
        cmd.Parameters.AddWithValue("@id", KeyConverter<TKey>.ToStorageId(_mapper.GetKey(record)));
        cmd.Parameters.AddWithValue("@document", _mapper.ToJson(record));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
            await UpsertAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {SqlVectorStoreHelpers.FormatIdentifier(Name)} WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", KeyConverter<TKey>.ToStorageId(key));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            if (key is not null)
                await DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public override async Task<TRecord?> GetAsync(
        TKey key,
        RecordRetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT id, document FROM {SqlVectorStoreHelpers.FormatIdentifier(Name)} WHERE id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", KeyConverter<TKey>.ToStorageId(key));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return _mapper.FromJson(reader.GetString(0), reader.GetString(1), includeVector: options?.IncludeVectors == true);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<TRecord> GetAsync(
        IEnumerable<TKey> keys,
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

    /// <inheritdoc/>
    public override async IAsyncEnumerable<TRecord> GetAsync(
        System.Linq.Expressions.Expression<Func<TRecord, bool>> filter,
        int top,
        FilteredRecordRetrievalOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        var where = LinqSqlFilterTranslator.Translate(filter, _mapper);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT id, document FROM {SqlVectorStoreHelpers.FormatIdentifier(Name)} WHERE {where.Sql} LIMIT {top}";
        foreach (var parameter in where.Parameters)
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return _mapper.FromJson(reader.GetString(0), reader.GetString(1), includeVector: options?.IncludeVectors == true);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TInput>(
        TInput searchValue,
        int top,
        VectorSearchOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchValue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        var query = SqlVectorStoreHelpers.FormatVectorLiteral(SqlVectorStoreHelpers.ExtractVector(searchValue));
        var metric = DistanceFunctionMapper.ToKnnMetric(_mapper.DistanceFunction);
        var where = options?.Filter is null
            ? SqlWhereClause.Empty
            : LinqSqlFilterTranslator.Translate(options.Filter, _mapper);
        var sql =
            $"SELECT id, document, vector_distance() AS distance FROM vector_search(" +
            $"source => '{SqlVectorStoreHelpers.EscapeSqlString(Name)}', " +
            $"vector_field => '{SqlVectorStoreHelpers.EscapeSqlString(_mapper.VectorJsonPath)}', " +
            $"vector => {query}, k => {top}, metric => '{metric}')" +
            (where.Sql.Length == 0 ? string.Empty : " WHERE " + where.Sql);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var parameter in where.Parameters)
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var json = reader.GetString(1);
            var distance = Convert.ToDouble(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture);
            var record = _mapper.FromJson(id, json, includeVector: options?.IncludeVectors == true);
            yield return new VectorSearchResult<TRecord>(record, distance);
        }
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(VectorStoreCollectionMetadata)
            ? _metadata
            : null;
    }

    private async ValueTask EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
