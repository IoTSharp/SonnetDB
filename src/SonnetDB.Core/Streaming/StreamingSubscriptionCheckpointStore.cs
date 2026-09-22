using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Streaming;

/// <summary>
/// 为 Streaming 订阅保存已确认检查点的持久化边界。
/// </summary>
/// <remarks>
/// <para>
/// 实现必须把检查点视为条件更新：<paramref name="expectedRevision"/> 必须与当前
/// 持久化版本一致，且待写入版本只能前进一位。调用方应先读取检查点，再在确认批次后
/// 使用返回的 revision 写入下一版本。
/// </para>
/// <para>该接口只持久化已确认的检查点，不承诺事件缓冲区、分布式租约或 exactly-once。</para>
/// <para>
/// 若调用方使用非初始的 <paramref name="expectedRevision"/>，而检查点文件已经缺失，
/// 条件写入会把缺失视为 revision -1 并抛出冲突；调用方不得把这种情况当作新订阅重置，
/// 否则可能重复消费事件。
/// </para>
/// </remarks>
public interface IStreamingSubscriptionCheckpointStore : IAsyncDisposable
{
    /// <summary>
    /// 读取指定订阅的已确认检查点。
    /// </summary>
    /// <param name="subscriptionId">订阅稳定标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已保存的检查点；尚未保存时返回 <see langword="null"/>。</returns>
    ValueTask<StreamingSubscriptionCheckpoint?> LoadAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 以 revision 条件原子保存检查点。
    /// </summary>
    /// <param name="checkpoint">待保存的检查点。</param>
    /// <param name="expectedRevision">
    /// 预期的当前 revision；首次保存使用 <c>-1</c>，后续保存使用当前检查点的 revision。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已发布的检查点。</returns>
    /// <exception cref="StreamingSubscriptionCheckpointConflictException">
    /// 当前 revision 与预期不一致时抛出；文件缺失时当前 revision 视为 -1。
    /// </exception>
    ValueTask<StreamingSubscriptionCheckpoint> SaveAsync(
        StreamingSubscriptionCheckpoint checkpoint,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Streaming 检查点的条件更新与当前状态不一致。
/// </summary>
public sealed class StreamingSubscriptionCheckpointConflictException : InvalidOperationException
{
    /// <summary>
    /// 创建 revision 冲突异常。
    /// </summary>
    /// <param name="subscriptionId">订阅标识。</param>
    /// <param name="expectedRevision">调用方预期的 revision。</param>
    /// <param name="actualRevision">存储中观察到的 revision。</param>
    public StreamingSubscriptionCheckpointConflictException(
        string subscriptionId,
        long expectedRevision,
        long actualRevision)
        : base(
            $"Streaming 订阅 '{subscriptionId}' 的检查点 revision 冲突：" +
            $"预期 {expectedRevision}，实际 {actualRevision}。")
    {
        SubscriptionId = subscriptionId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    /// <summary>发生冲突的订阅标识。</summary>
    public string SubscriptionId { get; }

    /// <summary>调用方预期的 revision。</summary>
    public long ExpectedRevision { get; }

    /// <summary>存储中观察到的 revision。</summary>
    public long ActualRevision { get; }
}

/// <summary>
/// 持久化 Streaming 检查点损坏或截断。
/// </summary>
public sealed class StreamingSubscriptionCheckpointCorruptException : IOException
{
    /// <summary>
    /// 创建检查点损坏异常。
    /// </summary>
    /// <param name="path">损坏文件路径。</param>
    /// <param name="innerException">底层解析或边界异常。</param>
    public StreamingSubscriptionCheckpointCorruptException(string path, Exception innerException)
        : base($"Streaming 检查点文件 '{path}' 损坏或无法完整解析。", innerException)
    {
        Path = path;
    }

    /// <summary>损坏文件路径。</summary>
    public string Path { get; }
}

/// <summary>
/// 使用单文件 JSON、临时文件原子替换和有界文件锁保存 Streaming 检查点。
/// </summary>
/// <remarks>
/// <para>
/// 每个订阅使用 SHA-256 派生的文件名，避免订阅标识进入路径语义；文件内容仍会校验
/// 订阅标识。写入先完成文件内容 flush，再执行原子替换和目录 fsync。损坏或截断文件
/// 会直接失败关闭，不会回退为空检查点。
/// </para>
/// <para>
/// 实例内使用单个异步闸门，实例间使用独占 lock 文件；锁等待有固定重试上限，并受
/// <paramref name="operationTimeout"/> 与调用方取消令牌共同约束。
/// </para>
/// <para>
/// <see cref="DisposeAsync"/> 只阻止尚未取得闸门的新操作；已经取得闸门的操作会完成后再
/// 释放闸门。原子替换成功但目录 fsync 失败时，调用可能抛出异常而新文件已经可见；这时
/// 提交结果未知，调用方必须重新打开或读取检查点后再决定是否重试，不能假设写入已回滚。
/// </para>
/// </remarks>
public sealed class FileStreamingSubscriptionCheckpointStore : IStreamingSubscriptionCheckpointStore
{
    /// <summary>检查点文件允许的最大 UTF-8 字节数。</summary>
    public const int MaxCheckpointBytes = 64 * 1024;

    /// <summary>订阅标识允许的最大 UTF-8 字节数。</summary>
    public const int MaxSubscriptionIdBytes = 256;

    /// <summary>默认单次操作超时时间。</summary>
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(10);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(20);
    private const int ReadBufferBytes = 8 * 1024;
    private const int MinimumTimeoutMilliseconds = 50;
    private const int MaximumTimeoutMinutes = 5;

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly TimeSpan _operationTimeout;
    private int _disposed;

    /// <summary>
    /// 创建文件检查点存储。
    /// </summary>
    /// <param name="directoryPath">检查点目录；目录不存在时会创建。</param>
    /// <param name="operationTimeout">
    /// 单次读、写或锁等待的总超时时间；未指定时使用 10 秒。
    /// </param>
    public FileStreamingSubscriptionCheckpointStore(
        string directoryPath,
        TimeSpan? operationTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        if (_operationTimeout < TimeSpan.FromMilliseconds(MinimumTimeoutMilliseconds)
            || _operationTimeout > TimeSpan.FromMinutes(MaximumTimeoutMinutes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                $"操作超时必须在 {MinimumTimeoutMilliseconds} 毫秒到 {MaximumTimeoutMinutes} 分钟之间。");
        }

        Directory.CreateDirectory(DirectoryPath);
    }

    /// <summary>检查点文件所在目录的绝对路径。</summary>
    public string DirectoryPath { get; }

    /// <summary>当前实例使用的单次操作超时时间。</summary>
    public TimeSpan OperationTimeout => _operationTimeout;

    /// <inheritdoc />
    public async ValueTask<StreamingSubscriptionCheckpoint?> LoadAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSubscriptionId(subscriptionId);
        return await ExecuteLockedAsync(
            subscriptionId,
            static (path, expectedId, token) => ReadExistingAsync(path, expectedId, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StreamingSubscriptionCheckpoint> SaveAsync(
        StreamingSubscriptionCheckpoint checkpoint,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        checkpoint.Validate();
        ValidateSubscriptionId(checkpoint.SubscriptionId);
        if (expectedRevision < -1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), "预期 revision 不能小于 -1。");

        long requiredRevision;
        try
        {
            requiredRevision = checked(expectedRevision + 1);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "预期 revision 已达到上限。");
        }

        if (checkpoint.Revision != requiredRevision)
        {
            throw new ArgumentException(
                $"检查点 revision 必须为预期 revision + 1（{requiredRevision}）。",
                nameof(checkpoint));
        }

        return await ExecuteLockedAsync(
            checkpoint.SubscriptionId,
            (path, expectedId, token) => SaveCoreAsync(
                path,
                expectedId,
                checkpoint,
                expectedRevision,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // 不在有操作进行时 Dispose SemaphoreSlim；它只包含托管状态，实例结束后由 GC 回收。
        // 这样可以保证取消/超时路径仍能释放闸门，不把并发调用转换成 ObjectDisposedException。
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private async Task<T> ExecuteLockedAsync<T>(
        string subscriptionId,
        Func<string, string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_operationTimeout);
        try
        {
            await _operationGate.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            try
            {
                // Dispose 可能在 WaitAsync 等待期间并发标记实例关闭；再次检查可避免
                // 已关闭实例在闸门释放后继续进入文件读写路径。
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                string path = GetCheckpointPath(subscriptionId);
                using FileStream lockStream = await AcquireLockAsync(
                    path + ".lock",
                    timeoutSource.Token).ConfigureAwait(false);
                return await operation(path, subscriptionId, timeoutSource.Token).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Streaming 检查点操作超过 {_operationTimeout.TotalMilliseconds:0} 毫秒超时边界。");
        }
    }

    private async Task<StreamingSubscriptionCheckpoint> SaveCoreAsync(
        string path,
        string subscriptionId,
        StreamingSubscriptionCheckpoint checkpoint,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        StreamingSubscriptionCheckpoint? current = await ReadExistingAsync(
            path,
            subscriptionId,
            cancellationToken).ConfigureAwait(false);
        long actualRevision = current?.Revision ?? -1;
        if (actualRevision != expectedRevision)
        {
            throw new StreamingSubscriptionCheckpointConflictException(
                subscriptionId,
                expectedRevision,
                actualRevision);
        }

        if (current is not null
            && (checkpoint.CommittedSequence < current.CommittedSequence
                || checkpoint.WatermarkUtc < current.WatermarkUtc))
        {
            throw new ArgumentException(
                "检查点的 committed sequence 和 watermark 只能单调前进。",
                nameof(checkpoint));
        }

        byte[] payload = StreamingSubscriptionJson.Serialize(checkpoint);
        if (payload.Length > MaxCheckpointBytes)
        {
            throw new ArgumentException(
                $"检查点 JSON 超过 {MaxCheckpointBytes} 字节上限。",
                nameof(checkpoint));
        }

        await WriteAtomicallyAsync(path, payload, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private async Task<FileStream> AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        int maxAttempts = Math.Max(
            1,
            (int)Math.Ceiling(_operationTimeout.TotalMilliseconds / LockRetryDelay.TotalMilliseconds));
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException) when (attempt + 1 < maxAttempts)
            {
                await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                // 文件系统可能先报告争用 IOException，再观察到令牌取消；优先保留
                // 调用方取消语义，只有确实耗尽操作预算时才转换为超时。
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"无法在 {_operationTimeout.TotalMilliseconds:0} 毫秒内取得检查点文件锁。",
                    exception);
            }
        }

        throw new TimeoutException($"无法在 {_operationTimeout.TotalMilliseconds:0} 毫秒内取得检查点文件锁。");
    }

    private async Task WriteAtomicallyAsync(
        string path,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                ReadBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
            SonnetDB.Wal.DirectoryFsync.FlushRequired(DirectoryPath);
        }
        finally
        {
            // 清理是 best-effort：写入、取消、原子替换或目录 fsync 已经失败时，
            // 清理异常不得覆盖真正的主异常。遗留临时文件不会被当作已发布检查点读取。
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // 清理失败不应覆盖写入或发布阶段的主异常。
            }
            catch (UnauthorizedAccessException)
            {
                // 下一次操作或运维清理可回收临时文件；保留主操作异常及其堆栈。
            }
        }
    }

    private static async Task<StreamingSubscriptionCheckpoint?> ReadExistingAsync(
        string path,
        string expectedSubscriptionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                ReadBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaxCheckpointBytes)
            {
                throw new StreamingSubscriptionCheckpointCorruptException(
                    path,
                    new InvalidDataException($"检查点文件超过 {MaxCheckpointBytes} 字节上限。"));
            }

            byte[] utf8 = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
            StreamingSubscriptionCheckpoint checkpoint;
            try
            {
                checkpoint = StreamingSubscriptionJson.DeserializeCheckpoint(utf8);
            }
            catch (Exception exception) when (exception is JsonException
                or InvalidDataException
                or ArgumentException
                or FormatException
                or OverflowException
                or NotSupportedException)
            {
                throw new StreamingSubscriptionCheckpointCorruptException(path, exception);
            }

            if (!string.Equals(checkpoint.SubscriptionId, expectedSubscriptionId, StringComparison.Ordinal))
            {
                throw new StreamingSubscriptionCheckpointCorruptException(
                    path,
                    new InvalidDataException("检查点订阅标识与请求不一致。"));
            }

            return checkpoint;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        try
        {
            using var output = new MemoryStream(capacity: Math.Min(MaxCheckpointBytes, ReadBufferBytes));
            int total = 0;
            int maxReadOperations = (MaxCheckpointBytes / ReadBufferBytes) + 2;
            for (int operation = 0; operation < maxReadOperations; operation++)
            {
                int remaining = MaxCheckpointBytes + 1 - total;
                if (remaining <= 0)
                {
                    throw new StreamingSubscriptionCheckpointCorruptException(
                        stream.Name,
                        new InvalidDataException($"检查点文件超过 {MaxCheckpointBytes} 字节上限。"));
                }

                int requested = Math.Min(rented.Length, remaining);
                int read = await stream.ReadAsync(
                    rented.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return output.ToArray();

                total += read;
                if (total > MaxCheckpointBytes)
                {
                    throw new StreamingSubscriptionCheckpointCorruptException(
                        stream.Name,
                        new InvalidDataException($"检查点文件超过 {MaxCheckpointBytes} 字节上限。"));
                }

                output.Write(rented, 0, read);
            }

            throw new StreamingSubscriptionCheckpointCorruptException(
                stream.Name,
                new InvalidDataException("检查点文件读取操作超过固定上限。"));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private string GetCheckpointPath(string subscriptionId)
        => Path.Combine(DirectoryPath, HashSubscriptionId(subscriptionId) + ".checkpoint.json");

    private static string HashSubscriptionId(string subscriptionId)
    {
        byte[] utf8 = StrictUtf8.GetBytes(subscriptionId);
        return Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
    }

    private static void ValidateSubscriptionId(string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(subscriptionId);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("订阅标识必须是有效的 UTF-8 字符串。", nameof(subscriptionId), exception);
        }

        if (byteCount > MaxSubscriptionIdBytes)
        {
            throw new ArgumentException(
                $"订阅标识不能超过 {MaxSubscriptionIdBytes} 个 UTF-8 字节。",
                nameof(subscriptionId));
        }
    }
}
