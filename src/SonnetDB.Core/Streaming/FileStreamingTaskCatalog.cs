using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Streaming;

/// <summary>
/// 保存文件订阅任务定义并按目录 revision 提供条件更新、有界分页和真实订阅打开入口。
/// </summary>
/// <remarks>
/// <para>目录只保存任务定义，不复制订阅 spool、检查点或运行状态。</para>
/// <para>打开任务时由 <see cref="FileStreamingSubscription.OpenAsync"/> 取得订阅自己的文件锁；目录本身也只允许一个执行者。</para>
/// </remarks>
public sealed class FileStreamingTaskCatalog : IAsyncDisposable
{
    private const int FormatVersion = 1;
    private const string StateFileName = "tasks.catalog.json";
    private const string LeaseFileName = "tasks.catalog.lock";
    private readonly FileStreamingTaskCatalogOptions _options;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private StreamingTaskCatalogState _state;
    private int _disposed;

    private FileStreamingTaskCatalog(
        string rootPath,
        FileStream lease,
        FileStreamingTaskCatalogOptions options,
        StreamingTaskCatalogState state)
    {
        RootPath = rootPath;
        _lease = lease;
        _options = options;
        _state = state;
    }

    /// <summary>任务目录根的绝对路径。</summary>
    public string RootPath { get; }

    /// <summary>当前目录 revision；每次注册、更新或删除成功后递增。</summary>
    public long Revision => Volatile.Read(ref _state).Revision;

    /// <summary>打开或创建任务目录，并取得单执行者目录锁。</summary>
    /// <param name="rootPath">任务目录根路径。</param>
    /// <param name="options">目录容量与操作边界。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已加载且持有目录锁的目录对象。</returns>
    public static async ValueTask<FileStreamingTaskCatalog> OpenAsync(
        string rootPath,
        FileStreamingTaskCatalogOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        options ??= new FileStreamingTaskCatalogOptions();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        string absoluteRoot = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(absoluteRoot);
        var lease = new FileStream(
            Path.Combine(absoluteRoot, LeaseFileName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.OperationTimeoutMilliseconds);
            StreamingTaskCatalogState state = await ReadStateAsync(
                Path.Combine(absoluteRoot, StateFileName), options, timeout.Token).ConfigureAwait(false);
            return new FileStreamingTaskCatalog(absoluteRoot, lease, options, state);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            lease.Dispose();
            throw new TimeoutException("打开持久订阅任务目录超过存储操作超时边界。", exception);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>按任务标识读取已登记定义；不存在时返回空。</summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务定义，或空值。</returns>
    public async ValueTask<StreamingTaskDefinition?> GetAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return await ExecuteAsync(() =>
            Volatile.Read(ref _state).Entries.FirstOrDefault(
                entry => string.Equals(entry.TaskId, taskId, StringComparison.Ordinal)), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>登记新任务，并按目录 revision 执行条件写入。</summary>
    /// <param name="definition">要登记的任务定义。</param>
    /// <param name="expectedRevision">调用方观察到的目录 revision。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入后的目录 revision。</returns>
    public ValueTask<long> RegisterAsync(
        StreamingTaskDefinition definition,
        long expectedRevision = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        return MutateAsync(expectedRevision, entries =>
        {
            if (entries.Any(entry => string.Equals(entry.TaskId, definition.TaskId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"任务标识 '{definition.TaskId}' 已登记。");
            if (entries.Any(entry => string.Equals(entry.DirectoryName, definition.DirectoryName, StringComparison.Ordinal)))
                throw new InvalidOperationException($"订阅目录 '{definition.DirectoryName}' 已登记。");
            if (entries.Count >= _options.MaxEntries)
                throw new InvalidOperationException("任务目录已达到容量上限。");
            entries.Add(definition);
        }, cancellationToken);
    }

    /// <summary>按目录 revision 更新已登记任务定义。</summary>
    /// <param name="definition">更新后的任务定义。</param>
    /// <param name="expectedRevision">调用方观察到的目录 revision。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入后的目录 revision。</returns>
    public ValueTask<long> UpdateAsync(
        StreamingTaskDefinition definition,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        return MutateAsync(expectedRevision, entries =>
        {
            int index = entries.FindIndex(entry => string.Equals(entry.TaskId, definition.TaskId, StringComparison.Ordinal));
            if (index < 0)
                throw new KeyNotFoundException($"任务标识 '{definition.TaskId}' 未登记。");
            for (int itemIndex = 0; itemIndex < entries.Count; itemIndex++)
            {
                if (itemIndex != index
                    && string.Equals(entries[itemIndex].DirectoryName, definition.DirectoryName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"订阅目录 '{definition.DirectoryName}' 已登记。");
                }
            }
            entries[index] = definition;
        }, cancellationToken);
    }

    /// <summary>按目录 revision 删除已登记任务定义。</summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="expectedRevision">调用方观察到的目录 revision。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入后的目录 revision。</returns>
    public ValueTask<long> RemoveAsync(
        string taskId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return MutateAsync(expectedRevision, entries =>
        {
            int removed = entries.RemoveAll(entry => string.Equals(entry.TaskId, taskId, StringComparison.Ordinal));
            if (removed == 0)
                throw new KeyNotFoundException($"任务标识 '{taskId}' 未登记。");
        }, cancellationToken);
    }

    /// <summary>读取按任务标识排序的一页任务定义。</summary>
    /// <param name="pageSize">请求页大小，不能超过目录配置上限。</param>
    /// <param name="continuationToken">上一页返回的续页游标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务页和下一页游标。</returns>
    public async ValueTask<StreamingTaskCatalogPage> ListAsync(
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, _options.MaxPageSize);
        return await ExecuteAsync(() =>
        {
            StreamingTaskCatalogState state = Volatile.Read(ref _state);
            int start = ParseCursor(continuationToken, state.Revision);
            StreamingTaskDefinition[] sorted = state.Entries
                .OrderBy(static entry => entry.TaskId, StringComparer.Ordinal)
                .ToArray();
            int count = Math.Min(pageSize, sorted.Length - start);
            var items = new StreamingTaskDefinition[count];
            Array.Copy(sorted, start, items, 0, count);
            int next = start + count;
            string? nextToken = next < sorted.Length
                ? $"{state.Revision.ToString(CultureInfo.InvariantCulture)}:{next.ToString(CultureInfo.InvariantCulture)}"
                : null;
            return new StreamingTaskCatalogPage(state.Revision, items, nextToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按任务定义打开真实文件订阅；调用方负责释放返回的订阅。</summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>持有订阅目录锁的真实文件订阅。</returns>
    public async ValueTask<FileStreamingSubscription> OpenSubscriptionAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        StreamingTaskDefinition definition = await GetRequiredAsync(taskId, cancellationToken).ConfigureAwait(false);
        string path = ResolveSubscriptionPath(definition);
        return await FileStreamingSubscription.OpenAsync(
            path, definition.SubscriptionDefinition, definition.SubscriptionOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>打开真实文件订阅并读取其本地运行状态。</summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>真实文件订阅生成的状态快照。</returns>
    public async ValueTask<FileStreamingSubscriptionStatus> GetStatusAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        await using FileStreamingSubscription subscription =
            await OpenSubscriptionAsync(taskId, cancellationToken).ConfigureAwait(false);
        return await subscription.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>通过真实文件订阅执行条件暂停。</summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="expectedStateRevision">订阅状态 revision。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新的订阅状态 revision。</returns>
    public async ValueTask<long> PauseConsumptionAsync(
        string taskId,
        long expectedStateRevision,
        CancellationToken cancellationToken = default)
    {
        await using FileStreamingSubscription subscription =
            await OpenSubscriptionAsync(taskId, cancellationToken).ConfigureAwait(false);
        return await subscription.PauseConsumptionAsync(expectedStateRevision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>通过真实文件订阅执行条件恢复。</summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="expectedStateRevision">订阅状态 revision。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新的订阅状态 revision。</returns>
    public async ValueTask<long> ResumeConsumptionAsync(
        string taskId,
        long expectedStateRevision,
        CancellationToken cancellationToken = default)
    {
        await using FileStreamingSubscription subscription =
            await OpenSubscriptionAsync(taskId, cancellationToken).ConfigureAwait(false);
        return await subscription.ResumeConsumptionAsync(expectedStateRevision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>释放目录锁。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _operationGate.Dispose();
        await _lease.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<StreamingTaskDefinition> GetRequiredAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        StreamingTaskDefinition? definition = await GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        return definition ?? throw new KeyNotFoundException($"任务标识 '{taskId}' 未登记。");
    }

    private string ResolveSubscriptionPath(StreamingTaskDefinition definition)
    {
        string path = Path.GetFullPath(Path.Combine(RootPath, definition.DirectoryName));
        string rootWithSeparator = RootPath.EndsWith(Path.DirectorySeparatorChar)
            ? RootPath
            : RootPath + Path.DirectorySeparatorChar;
        if (!string.Equals(path, RootPath, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("任务目录路径越过目录根。");
        }
        return path;
    }

    private async ValueTask<long> MutateAsync(
        long expectedRevision,
        Action<List<StreamingTaskDefinition>> mutation,
        CancellationToken cancellationToken)
    {
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), "目录 revision 不能小于 0。");
        return await ExecuteAsync(async () =>
        {
            StreamingTaskCatalogState current = Volatile.Read(ref _state);
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException($"任务目录 revision 不匹配，预期 {expectedRevision}，实际 {current.Revision}。");
            var entries = current.Entries.ToList();
            mutation(entries);
            entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.TaskId, right.TaskId));
            var next = new StreamingTaskCatalogState(FormatVersion, checked(current.Revision + 1), entries);
            await PersistAsync(next, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _state, next);
            return next.Revision;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TResult> ExecuteAsync<TResult>(
        Func<TResult> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeoutMilliseconds);
        await _operationGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            return operation();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<TResult> ExecuteAsync<TResult>(
        Func<ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeoutMilliseconds);
        await _operationGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static int ParseCursor(string? token, long revision)
    {
        if (string.IsNullOrWhiteSpace(token))
            return 0;
        string[] parts = token.Split(':', StringSplitOptions.None);
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long tokenRevision)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            || tokenRevision != revision || index < 0)
        {
            throw new InvalidOperationException("任务目录续页游标无效或已过期。");
        }
        return index;
    }

    private static async ValueTask<StreamingTaskCatalogState> ReadStateAsync(
        string statePath,
        FileStreamingTaskCatalogOptions options,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
            return new StreamingTaskCatalogState(FormatVersion, 0, Array.Empty<StreamingTaskDefinition>());
        FileInfo info = new(statePath);
        if (info.Length > options.MaxCatalogBytes)
            throw new InvalidDataException("任务目录文件超过配置的容量上限。");
        byte[] bytes = await File.ReadAllBytesAsync(statePath, cancellationToken).ConfigureAwait(false);
        try
        {
            StreamingTaskCatalogEnvelope envelope = JsonSerializer.Deserialize(
                    bytes, StreamingTaskCatalogJsonContext.Default.StreamingTaskCatalogEnvelope)
                ?? throw new InvalidDataException("任务目录 JSON 为空。");
            byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(
                envelope.State, StreamingTaskCatalogJsonContext.Default.StreamingTaskCatalogState);
            string expected = Convert.ToHexString(SHA256.HashData(stateBytes));
            if (!string.Equals(expected, envelope.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("任务目录校验哈希不匹配。");
            ValidateState(envelope.State, options);
            return envelope.State;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("任务目录 JSON 损坏，已拒绝加载。", exception);
        }
    }

    private static void ValidateState(StreamingTaskCatalogState state, FileStreamingTaskCatalogOptions options)
    {
        if (state.FormatVersion != FormatVersion)
            throw new InvalidDataException($"不支持任务目录格式版本 {state.FormatVersion}。");
        ArgumentOutOfRangeException.ThrowIfNegative(state.Revision);
        if (state.Entries.Count > options.MaxEntries)
            throw new InvalidDataException("任务目录条目数超过容量上限。");
        var taskIds = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (StreamingTaskDefinition entry in state.Entries)
        {
            entry.Validate();
            if (!taskIds.Add(entry.TaskId) || !directories.Add(entry.DirectoryName))
                throw new InvalidDataException("任务目录包含重复任务标识或订阅目录。");
        }
    }

    private async ValueTask PersistAsync(StreamingTaskCatalogState state, CancellationToken cancellationToken)
    {
        byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(
            state, StreamingTaskCatalogJsonContext.Default.StreamingTaskCatalogState);
        byte[] envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
            new StreamingTaskCatalogEnvelope(state, Convert.ToHexString(SHA256.HashData(stateBytes))),
            StreamingTaskCatalogJsonContext.Default.StreamingTaskCatalogEnvelope);
        if (envelopeBytes.Length > _options.MaxCatalogBytes)
            throw new InvalidOperationException("任务目录写入将超过容量上限。");
        string temporaryPath = Path.Combine(RootPath, $"{StateFileName}.tmp-{Guid.NewGuid():N}");
        try
        {
            await using (var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 16 * 1024, options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(envelopeBytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, Path.Combine(RootPath, StateFileName), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
