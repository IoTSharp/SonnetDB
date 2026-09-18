using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using SonnetDB.Data.Remote;
using SonnetDB.Kv;

namespace SonnetDB.Data.Kv;

/// <summary>KV 大 keyspace 游标选项。</summary>
public sealed record SndbKvCursorOptions
{
    /// <summary>默认单页条目数。</summary>
    public const int DefaultPageSize = 256;

    /// <summary>key 前缀；为空时扫描整个 keyspace。</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>单页最多返回的条目数，范围为 1 至 1000。</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>嵌入式游标复制的单页最大字节数。</summary>
    public int MaxPageBytes { get; init; } = KvRangeScanOptions.DefaultMaxPageBytes;
}

/// <summary>KV 游标的一页结果。</summary>
public sealed record SndbKvPage(
    IReadOnlyList<SndbKvEntry> Entries,
    string? NextCursor,
    bool HasMore);

/// <summary>
/// 以稳定快照或远程 continuation token 逐页读取 KV keyspace 的异步游标。
/// </summary>
public sealed class SndbKvCursor : IDisposable, IAsyncDisposable
{
    private const int MaxPagesPerCursor = 1_000_000;
    private readonly Func<string?, CancellationToken, Task<SndbKvPage>> _readPage;
    private readonly IDisposable? _owner;
    private int _readInProgress;
    private bool _disposed;
    private string? _nextCursor;

    internal SndbKvCursor(
        Func<string?, CancellationToken, Task<SndbKvPage>> readPage,
        IDisposable? owner = null)
    {
        _readPage = readPage;
        _owner = owner;
    }

    /// <summary>最近一次成功读取的页。</summary>
    public SndbKvPage? Current { get; private set; }

    /// <summary>是否已经读到范围末尾。</summary>
    public bool IsExhausted { get; private set; }

    /// <summary>下一页 continuation token；首页读取前为空。</summary>
    public string? ContinuationToken => _nextCursor;

    /// <summary>读取下一页；空页且无更多数据时返回 <see langword="false"/>。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否读取到至少一条记录。</returns>
    public async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _readInProgress, 1) != 0)
            throw new InvalidOperationException("同一个 KV cursor 不能并发读取。");

        try
        {
            for (int pageNumber = 0; !IsExhausted && pageNumber < MaxPagesPerCursor; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SndbKvPage page = await _readPage(_nextCursor, cancellationToken).ConfigureAwait(false);
                Current = page;
                _nextCursor = page.NextCursor;
                IsExhausted = !page.HasMore;
                if (page.Entries.Count > 0)
                    return true;
            }

            if (!IsExhausted)
                throw new InvalidOperationException("KV cursor 超过单次读取页数上限。");

            return false;
        }
        finally
        {
            Volatile.Write(ref _readInProgress, 0);
        }
    }

    /// <summary>从当前位置开始逐条异步枚举剩余记录。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按 key 字节序升序返回的记录流。</returns>
    public async IAsyncEnumerable<SndbKvEntry> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (SndbKvEntry entry in Current!.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }
        }
    }

    /// <summary>释放游标及其稳定快照租约。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _owner?.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>KV pipeline 中单个操作的类型。</summary>
public enum SndbKvBatchOperationKind
{
    /// <summary>读取 key。</summary>
    Get = 0,
    /// <summary>写入 key。</summary>
    Set = 1,
    /// <summary>删除 key。</summary>
    Remove = 2
}

/// <summary>KV pipeline 中单个有界操作。</summary>
public sealed record SndbKvBatchOperation(
    SndbKvBatchOperationKind Kind,
    string Key,
    byte[]? Value = null,
    DateTimeOffset? ExpiresAtUtc = null)
{
    /// <summary>创建读取操作。</summary>
    public static SndbKvBatchOperation Get(string key) => new(SndbKvBatchOperationKind.Get, key);

    /// <summary>创建写入操作。</summary>
    public static SndbKvBatchOperation Set(string key, byte[] value, DateTimeOffset? expiresAtUtc = null)
        => new(SndbKvBatchOperationKind.Set, key, value, expiresAtUtc);

    /// <summary>创建删除操作。</summary>
    public static SndbKvBatchOperation Remove(string key) => new(SndbKvBatchOperationKind.Remove, key);
}

/// <summary>KV pipeline 并发和背压选项。</summary>
public sealed record SndbKvBatchOptions
{
    /// <summary>默认并发 worker 数。</summary>
    public const int DefaultMaxConcurrency = 4;

    /// <summary>默认待处理队列容量。</summary>
    public const int DefaultQueueCapacity = 256;

    /// <summary>并发 worker 数，范围为 1 至 32。</summary>
    public int MaxConcurrency { get; init; } = DefaultMaxConcurrency;

    /// <summary>有界队列容量，范围为 1 至 4096。</summary>
    public int QueueCapacity { get; init; } = DefaultQueueCapacity;
}

/// <summary>KV pipeline 单项结果；单项失败不会掩盖同批其他结果。</summary>
public sealed record SndbKvBatchItemResult(
    int Index,
    string Key,
    SndbKvBatchOperationKind Kind,
    bool Succeeded,
    SndbKvEntry? Entry,
    long? Version,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>KV pipeline 批量结果。</summary>
public sealed record SndbKvBatchResult(
    IReadOnlyList<SndbKvBatchItemResult> Items,
    int SucceededCount,
    int FailedCount,
    bool IsCanceled);

internal sealed record KvCursorRequest(string? Prefix, string? Cursor, int? Limit);

internal sealed record KvCursorResponse(
    List<KvEntryResponse> Entries,
    string? NextCursor,
    bool HasMore);

public sealed partial class SndbKvClient
{
    /// <summary>打开 KV 大 keyspace 异步游标。</summary>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="namespace">逻辑命名空间；为空表示 root。</param>
    /// <param name="options">前缀和分页预算。</param>
    /// <returns>可取消、可释放的异步游标。</returns>
    public SndbKvCursor OpenCursor(
        string keyspace,
        string @namespace,
        SndbKvCursorOptions? options = null)
    {
        ThrowIfDisposed();
        ValidateNames(keyspace, @namespace);
        options ??= new SndbKvCursorOptions();
        ValidateCursorOptions(options);

        string qualifiedPrefix = Qualify(@namespace, options.Prefix);
        if (_embedded is not null)
        {
            KvReadSnapshot snapshot = _embedded.Keyspaces.Open(keyspace).AcquireReadSnapshot();
            try
            {
                KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
                {
                    Prefix = Encoding.UTF8.GetBytes(qualifiedPrefix),
                    PageSize = options.PageSize,
                    MaxPageBytes = options.MaxPageBytes
                });
                return new SndbKvCursor(
                    async (_, cancellationToken) =>
                    {
                        IReadOnlyList<KvEntry> page = await cursor.ReadNextPageAsync(cancellationToken).ConfigureAwait(false);
                        var entries = page.Select(entry => new SndbKvEntry(
                            Unqualify(@namespace, Encoding.UTF8.GetString(entry.Key.Span)),
                            entry.Value.ToArray(), entry.Version, entry.ExpiresAtUtc)).ToArray();
                        return new SndbKvPage(entries, null, !cursor.IsExhausted);
                    },
                    new CursorOwner(cursor, snapshot));
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }
        }

        return new SndbKvCursor(
            (cursor, cancellationToken) => ReadRemoteCursorPageAsync(
                keyspace, @namespace, qualifiedPrefix, options.PageSize, cursor, cancellationToken));
    }

    /// <summary>执行有界 KV pipeline，并为每个操作返回独立结果。</summary>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="namespace">逻辑命名空间；为空表示 root。</param>
    /// <param name="operations">待执行操作；最多 4096 项。</param>
    /// <param name="options">并发与有界队列选项。</param>
    /// <param name="cancellationToken">取消令牌；已完成项保留，未完成项返回 cancelled。</param>
    /// <returns>按输入顺序排列的逐项结果。</returns>
    public async Task<SndbKvBatchResult> ExecutePipelineAsync(
        string keyspace,
        string @namespace,
        IEnumerable<SndbKvBatchOperation> operations,
        SndbKvBatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateNames(keyspace, @namespace);
        ArgumentNullException.ThrowIfNull(operations);
        options ??= new SndbKvBatchOptions();
        ValidateBatchOptions(options);
        SndbKvBatchOperation[] requested = operations.ToArray();
        if (requested.Length > 4096)
            throw new ArgumentOutOfRangeException(nameof(operations), "单次 KV pipeline 最多 4096 项。");

        var results = new SndbKvBatchItemResult?[requested.Length];
        var channel = Channel.CreateBounded<(int Index, SndbKvBatchOperation Operation)>(
            new BoundedChannelOptions(options.QueueCapacity) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        Task[] workers = Enumerable.Range(0, options.MaxConcurrency)
            .Select(_ => ConsumeBatchAsync(channel.Reader, results, keyspace, @namespace, cancellationToken))
            .ToArray();
        bool canceled = false;
        try
        {
            for (int index = 0; index < requested.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await channel.Writer.WriteAsync((index, requested[index]), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        for (int index = 0; index < requested.Length; index++)
        {
            results[index] ??= new SndbKvBatchItemResult(
                index, requested[index].Key, requested[index].Kind, false, null, null,
                canceled || cancellationToken.IsCancellationRequested ? "cancelled" : "not_processed",
                canceled || cancellationToken.IsCancellationRequested ? "操作被取消。" : "操作未执行。");
        }

        SndbKvBatchItemResult[] completed = results.Select(static result => result!).ToArray();
        return new SndbKvBatchResult(
            completed,
            completed.Count(static result => result.Succeeded),
            completed.Count(static result => !result.Succeeded),
            canceled || cancellationToken.IsCancellationRequested);
    }

    /// <summary>获取 KV 容量、TTL 与热点 key 诊断。</summary>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="topHotKeys">最多返回的热点 key 数，范围为 0 至 100。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>诊断快照。</returns>
    public async Task<SndbKvDiagnostics> GetDiagnosticsAsync(
        string keyspace,
        int topHotKeys = 10,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);
        if (topHotKeys is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(topHotKeys));
        cancellationToken.ThrowIfCancellationRequested();

        if (_embedded is not null)
        {
            KvDiagnostics diagnostics = _embedded.Keyspaces.Open(keyspace).GetDiagnostics(topHotKeys);
            return ToDiagnostics(diagnostics);
        }

        using var response = await PostJsonAsync(
            $"v1/db/{Uri.EscapeDataString(_database)}/kv/{Uri.EscapeDataString(keyspace)}/diagnostics",
            new KvDiagnosticsRequest(topHotKeys),
            RemoteJsonContext.Default.KvDiagnosticsRequest,
            cancellationToken).ConfigureAwait(false);
        KvDiagnosticsResponse body = await ReadJsonAsync(
            response, RemoteJsonContext.Default.KvDiagnosticsResponse, cancellationToken).ConfigureAwait(false);
        return new SndbKvDiagnostics(
            body.TotalKeys,
            body.ActiveKeys,
            body.ExpiredKeys,
            body.ExpiringKeys,
            body.NearestExpiresAtUtc,
            body.MutableOverlayEntries,
            body.FrozenOverlayEntries,
            body.WalBytes,
            body.MaxSnapshotOverlayEntries,
            body.HotKeys.Select(static key => new SndbKvHotKey(key.Key, key.Reads)).ToArray());
    }

    private static SndbKvDiagnostics ToDiagnostics(KvDiagnostics diagnostics)
        => new(
            diagnostics.TotalKeys,
            diagnostics.ActiveKeys,
            diagnostics.ExpiredKeys,
            diagnostics.ExpiringKeys,
            diagnostics.NearestExpiresAtUtc,
            diagnostics.MutableOverlayEntries,
            diagnostics.FrozenOverlayEntries,
            diagnostics.WalBytes,
            diagnostics.MaxSnapshotOverlayEntries,
            diagnostics.HotKeys.Select(static key => new SndbKvHotKey(key.Key, key.Reads)).ToArray());

    private async Task ConsumeBatchAsync(
        ChannelReader<(int Index, SndbKvBatchOperation Operation)> reader,
        SndbKvBatchItemResult?[] results,
        string keyspace,
        string @namespace,
        CancellationToken cancellationToken)
    {
        await foreach ((int index, SndbKvBatchOperation operation) in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                SndbKvBatchItemResult result = operation.Kind switch
                {
                    SndbKvBatchOperationKind.Get => new(index, operation.Key, operation.Kind, true,
                        await GetAsync(keyspace, @namespace, operation.Key, cancellationToken).ConfigureAwait(false), null, null, null),
                    SndbKvBatchOperationKind.Set => new(index, operation.Key, operation.Kind, true, null,
                        await SetAsync(keyspace, @namespace, operation.Key,
                            operation.Value ?? throw new ArgumentException("Set 操作缺少 value。", nameof(operation)),
                            operation.ExpiresAtUtc, cancellationToken).ConfigureAwait(false), null, null),
                    SndbKvBatchOperationKind.Remove => new(index, operation.Key, operation.Kind, true, null, null, null,
                        null),
                    _ => throw new ArgumentOutOfRangeException(nameof(operation.Kind))
                };
                if (operation.Kind == SndbKvBatchOperationKind.Remove)
                    result = result with { Succeeded = await RemoveAsync(keyspace, @namespace, operation.Key, cancellationToken).ConfigureAwait(false) };
                results[index] = result;
            }
            catch (OperationCanceledException)
            {
                results[index] = new SndbKvBatchItemResult(index, operation.Key, operation.Kind, false, null, null, "cancelled", "操作被取消。");
            }
            catch (Exception exception)
            {
                results[index] = new SndbKvBatchItemResult(index, operation.Key, operation.Kind, false, null, null,
                    "operation_failed", "KV pipeline 单项操作失败。" + (exception is ArgumentException ? " 请检查输入。" : string.Empty));
            }
        }
    }

    private async Task<SndbKvPage> ReadRemoteCursorPageAsync(
        string keyspace,
        string @namespace,
        string prefix,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        using var response = await PostJsonAsync(
            $"v1/db/{Uri.EscapeDataString(_database)}/kv/{Uri.EscapeDataString(keyspace)}/scan",
            new KvCursorRequest(prefix, cursor, pageSize),
            RemoteJsonContext.Default.KvCursorRequest,
            cancellationToken).ConfigureAwait(false);
        KvCursorResponse body = await ReadJsonAsync(response, RemoteJsonContext.Default.KvCursorResponse, cancellationToken).ConfigureAwait(false);
        var entries = body.Entries.Select(entry => new SndbKvEntry(
            Unqualify(@namespace, entry.Key), entry.Value, entry.Version, entry.ExpiresAtUtc)).ToArray();
        return new SndbKvPage(entries, body.NextCursor, body.HasMore);
    }

    private static void ValidateCursorOptions(SndbKvCursorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Prefix);
        if (options.PageSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(options.PageSize), "游标页大小必须在 1 至 1000 之间。");
        if (options.MaxPageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxPageBytes));
    }

    private static void ValidateBatchOptions(SndbKvBatchOptions options)
    {
        if (options.MaxConcurrency is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrency));
        if (options.QueueCapacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(options.QueueCapacity));
    }

    private sealed class CursorOwner : IDisposable
    {
        private readonly KvRangeCursor _cursor;
        private readonly KvReadSnapshot _snapshot;
        public CursorOwner(KvRangeCursor cursor, KvReadSnapshot snapshot) { _cursor = cursor; _snapshot = snapshot; }
        public void Dispose() { _cursor.Dispose(); _snapshot.Dispose(); }
    }
}
