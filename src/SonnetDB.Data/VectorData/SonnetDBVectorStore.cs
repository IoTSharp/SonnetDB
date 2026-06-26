using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using SonnetDB.Data.VectorData.Internal;

namespace SonnetDB.Data.VectorData;

/// <summary>
/// SonnetDB 对 <see cref="VectorStore"/> 的实现。
/// </summary>
/// <remarks>
/// VectorData collection 映射为 SonnetDB document collection，记录整体存储为 JSON 文档，
/// 主键使用 document id，向量默认存储在 <c>$.embedding</c>。
/// </remarks>
public sealed class SonnetDBVectorStore : VectorStore
{
    private readonly SndbConnection _connection;
    private readonly bool _ownsConnection;
    private readonly VectorStoreMetadata _metadata;

    /// <summary>
    /// 初始化 <see cref="SonnetDBVectorStore"/>。
    /// </summary>
    /// <param name="connection">SonnetDB ADO.NET 连接。</param>
    /// <param name="ownsConnection">为 <see langword="true"/> 时，释放 store 会释放连接。</param>
    public SonnetDBVectorStore(SndbConnection connection, bool ownsConnection = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _ownsConnection = ownsConnection;
        _metadata = new VectorStoreMetadata
        {
            VectorStoreSystemName = "sonnetdb",
        };
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode("SonnetDBVectorCollection 通过反射访问 TRecord 的属性。")]
    [RequiresDynamicCode("SonnetDBVectorCollection 通过反射访问 TRecord 的属性。")]
    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name,
        VectorStoreCollectionDefinition? definition = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new SonnetDBVectorCollection<TKey, TRecord>(_connection, name, definition);
    }

    /// <inheritdoc/>
    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(
        string name,
        VectorStoreCollectionDefinition definition)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(definition);
        return new SonnetDBDynamicVectorCollection(_connection, name, definition);
    }

    /// <inheritdoc/>
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SHOW DOCUMENT COLLECTIONS";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(0), name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public override async Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await CollectionExistsAsync(name, cancellationToken).ConfigureAwait(false))
            return;

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DROP DOCUMENT COLLECTION {SqlVectorStoreHelpers.FormatIdentifier(name)}";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<string> ListCollectionNamesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SHOW DOCUMENT COLLECTIONS";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return reader.GetString(0);
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType == typeof(VectorStoreMetadata))
            return _metadata;
        if (serviceKey is null && serviceType == typeof(SndbConnection))
            return _connection;
        return null;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsConnection)
            _connection.Dispose();
        base.Dispose(disposing);
    }

    private async ValueTask EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_connection.State != System.Data.ConnectionState.Open)
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
