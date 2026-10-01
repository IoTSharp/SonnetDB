using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Streaming;

/// <summary>为单个本地文件订阅持久化固定 UTC 滚动 COUNT 窗口及其恢复位点。</summary>
/// <remarks>
/// <para>每个批次的窗口和已应用位点写入同一个原子替换文件，之后才确认订阅。
/// 窗口提交后未完成确认的最后批次按稳定投递标识和内容校验重试，不重复计数。</para>
/// <para>只有显式推进的 watermark 才关闭窗口；调用方应先排空已接受的订阅事件。
/// 结果保留至显式删除，容量耗尽会拒绝批次而保留未确认事件。</para>
/// <para>仅支持单机单执行者与 COUNT；没有远程租约、SUM 等数值聚合或外部副作用事务。</para>
/// </remarks>
public sealed class FileStreamingWindowAggregator : IAsyncDisposable
{
    private const int FormatVersion = 1;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly FileStream _lease;
    private StreamingWindowDocument _document;
    private Exception? _fault;
    private int _closing;
    private int _closed;

    private FileStreamingWindowAggregator(string path, FileStream lease, StreamingWindowDocument document)
    {
        FilePath = path;
        _lease = lease;
        _document = document;
        _lifetimeToken = _lifetime.Token;
    }

    /// <summary>窗口与恢复位点共同保存的状态文件绝对路径。</summary>
    public string FilePath { get; }

    /// <summary>创建时持久保存的窗口定义。</summary>
    public StreamingWindowDefinition Definition => _document.Definition;

    /// <summary>创建时持久保存的容量与操作边界。</summary>
    public StreamingWindowOptions Options => _document.Options;

    /// <summary>创建新的持久窗口聚合器；不会覆盖已存在的状态文件。</summary>
    /// <param name="path">专属于该聚合器的状态文件路径。</param>
    /// <param name="definition">固定 UTC 滚动窗口定义。</param>
    /// <param name="initialCheckpoint">开始消费前已确认的订阅位点；默认从空订阅开始。</param>
    /// <param name="options">容量、单批处理与操作超时边界。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已经保存初始状态并持有本地独占 lease 的聚合器。</returns>
    public static async ValueTask<FileStreamingWindowAggregator> CreateAsync(
        string path,
        StreamingWindowDefinition definition,
        StreamingSubscriptionCheckpoint? initialCheckpoint = null,
        StreamingWindowOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(definition, options ??= new());
        initialCheckpoint ??= StreamingSubscriptionCheckpoint.Create(definition.SubscriptionId);
        initialCheckpoint.Validate();
        if (initialCheckpoint.SubscriptionId != definition.SubscriptionId)
            throw new ArgumentException("初始检查点属于其他订阅。", nameof(initialCheckpoint));
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = GetPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var lease = AcquireLease(fullPath);
        FileStreamingWindowAggregator? aggregator = null;
        try
        {
            if (File.Exists(fullPath))
                throw new IOException("窗口状态文件已经存在，请使用 OpenAsync 恢复。");
            if (File.Exists(fullPath + ".pending"))
                throw new IOException("窗口初始状态缺失但仍有待提交侧文件；不能创建并覆盖结果未知的初始提交。");
            var document = new StreamingWindowDocument(
                FormatVersion, definition, options, initialCheckpoint.CommittedSequence,
                initialCheckpoint.Revision, initialCheckpoint.WatermarkUtc, 0, null, null, []);
            aggregator = new FileStreamingWindowAggregator(fullPath, lease, document);
            await aggregator.ExecuteAsync(async token =>
            {
                await aggregator.SaveAsync(document, overwrite: false, token).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            return aggregator;
        }
        catch
        {
            if (aggregator is not null)
                await aggregator.DisposeAsync().ConfigureAwait(false);
            else
                await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>重开已有聚合器；状态缺失、损坏或定义不一致时拒绝恢复。</summary>
    /// <param name="path">已有状态文件路径。</param>
    /// <param name="definition">必须与保存状态完全一致的窗口定义。</param>
    /// <param name="options">必须与保存状态完全一致的容量和操作边界。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已校验且持有本地独占 lease 的聚合器。</returns>
    public static async ValueTask<FileStreamingWindowAggregator> OpenAsync(
        string path,
        StreamingWindowDefinition definition,
        StreamingWindowOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(definition, options ??= new());
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = GetPath(path);
        var lease = AcquireLease(fullPath);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.OperationTimeoutMilliseconds);
            StreamingWindowDocument document = await LoadAsync(fullPath, options, deadline.Token).ConfigureAwait(false);
            if (document.Definition != definition || document.Options != options)
                throw new InvalidDataException("窗口定义或容量边界与持久状态不一致。");
            bool temporaryOwned = await ValidateTemporaryOwnerAsync(fullPath, deadline.Token).ConfigureAwait(false);
            if (File.Exists(fullPath + ".pending"))
            {
                if (!temporaryOwned)
                    throw new InvalidDataException("窗口待提交侧文件没有有效所有权标记，必须保留并人工核对。");
                deadline.Token.ThrowIfCancellationRequested();
                File.Delete(fullPath + ".pending");
                Wal.DirectoryFsync.FlushRequired(Path.GetDirectoryName(fullPath)!);
            }
            return new FileStreamingWindowAggregator(fullPath, lease, document);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException("窗口状态恢复超过操作超时边界。", exception);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>读取一个持久订阅批次，先提交窗口状态和位点，再确认订阅。</summary>
    /// <param name="subscription">定义中指定的本地持久订阅。</param>
    /// <param name="cancellationToken">取消令牌；等待事件和整个单步调用均受操作超时约束。</param>
    /// <returns>已处理批次时为真；订阅结束且已排空时为假。</returns>
    public async ValueTask<bool> PumpOnceAsync(
        FileStreamingSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return await ExecuteAsync(async token =>
        {
            if (subscription.Definition.SubscriptionId != Definition.SubscriptionId
                || subscription.Definition.StreamName != Definition.StreamName)
                throw new ArgumentException("窗口聚合器不能消费其他订阅或事件流。", nameof(subscription));
            StreamingSubscriptionCheckpoint checkpoint = subscription.Checkpoint;
            if (checkpoint.CommittedSequence > _document.AppliedSequence
                || checkpoint.Revision > _document.AppliedRevision)
                throw new InvalidDataException("订阅已经确认窗口聚合器尚未应用的事件，不能继续并静默跳过。");
            StreamingDeliveryBatch? batch = await subscription.ReadBatchAsync(token).ConfigureAwait(false);
            if (batch is null)
            {
                if (checkpoint.CommittedSequence != _document.AppliedSequence
                    || checkpoint.Revision != _document.AppliedRevision)
                    throw new InvalidDataException("订阅已排空但窗口位点与确认位点不一致。");
                return false;
            }

            await ApplyCoreAsync(batch, token).ConfigureAwait(false);
            await subscription.AcknowledgeAsync(batch.DeliveryId, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>原子应用一个订阅批次；最近已提交批次的相同重投不会重复计数。</summary>
    /// <param name="batch">完整有界批次，检查点版本必须为下一版本或最后提交版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用或重投核对后的持久状态。</returns>
    public async ValueTask<StreamingWindowState> ApplyBatchAsync(
        StreamingDeliveryBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return await ExecuteAsync(async token =>
        {
            await ApplyCoreAsync(batch, token).ConfigureAwait(false);
            return State(_document);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>持久推进关闭窗口的单调 UTC watermark；关闭条件为终点加允许迟到时间不晚于 watermark。</summary>
    /// <param name="watermarkUtc">新的 UTC watermark。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask AdvanceWatermarkAsync(DateTimeOffset watermarkUtc, CancellationToken cancellationToken = default)
    {
        ValidateUtc(watermarkUtc, nameof(watermarkUtc));
        await ExecuteAsync(async token =>
        {
            if (watermarkUtc < _document.WatermarkUtc)
                throw new ArgumentOutOfRangeException(nameof(watermarkUtc), "窗口 watermark 不能回退。");
            if (watermarkUtc != _document.WatermarkUtc)
                await CommitAsync(_document with { WatermarkUtc = watermarkUtc }, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取窗口与恢复位点的一致状态快照。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最近提交的状态快照。</returns>
    public async ValueTask<StreamingWindowState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(State(_document));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 UTC 起点排序有界读取窗口，返回可继续读取的排他起点。</summary>
    /// <param name="maxWindows">最多返回的窗口数量，不能超过配置的最大窗口数。</param>
    /// <param name="afterStartUtc">排他起点；为空时从第一个窗口开始。</param>
    /// <param name="closedOnly">是否只返回已关闭窗口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果窗口及读取时对应的一致状态。</returns>
    public async ValueTask<StreamingWindowBatch> ReadWindowsAsync(
        int maxWindows = 100,
        DateTimeOffset? afterStartUtc = null,
        bool closedOnly = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWindows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxWindows, Options.MaxWindows);
        if (afterStartUtc is { } after)
            ValidateUtc(after, nameof(afterStartUtc));
        return await ExecuteAsync(token =>
        {
            var results = new List<StreamingWindowCount>(maxWindows);
            bool hasMore = false;
            foreach ((long startTicks, long count) in _document.Windows.OrderBy(static item => item.Key))
            {
                token.ThrowIfCancellationRequested();
                var start = new DateTimeOffset(startTicks, TimeSpan.Zero);
                DateTimeOffset end = WindowEnd(startTicks);
                bool closed = IsClosed(end, _document.WatermarkUtc);
                if ((afterStartUtc is { } boundary && start <= boundary) || (closedOnly && !closed))
                    continue;
                if (results.Count == maxWindows)
                {
                    hasMore = true;
                    break;
                }

                results.Add(new StreamingWindowCount(start, end, count, closed));
            }

            return ValueTask.FromResult(new StreamingWindowBatch(
                results.AsReadOnly(), results.Count == 0 ? null : results[^1].StartUtc,
                hasMore, State(_document)));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>持久移除指定起点及以前的已关闭窗口，保留恢复位点与迟到丢弃计数。</summary>
    /// <param name="throughStartUtc">包含的最大窗口起点，必须为 UTC。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次移除的窗口数量。</returns>
    public async ValueTask<int> RemoveClosedWindowsAsync(
        DateTimeOffset throughStartUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(throughStartUtc, nameof(throughStartUtc));
        return await ExecuteAsync(async token =>
        {
            var windows = new Dictionary<long, long>(_document.Windows);
            int removed = 0;
            foreach (long startTicks in _document.Windows.Keys)
            {
                token.ThrowIfCancellationRequested();
                if (startTicks <= throughStartUtc.UtcTicks && IsClosed(WindowEnd(startTicks), _document.WatermarkUtc))
                {
                    windows.Remove(startTicks);
                    removed++;
                }
            }

            if (removed > 0)
                await CommitAsync(_document with { Windows = windows }, token).ConfigureAwait(false);
            return removed;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取消当前操作并释放本地 lease；持久状态保留供后续重开。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _closed) != 0)
            return;
        if (Interlocked.Exchange(ref _closing, 1) == 0)
            await _lifetime.CancelAsync().ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(Options.OperationTimeoutMilliseconds);
        try
        {
            await _operationGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("窗口聚合器关闭超时，lease 仍保留；操作结束后应再次关闭。", exception);
        }

        try
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                await _lease.DisposeAsync().ConfigureAwait(false);
                _lifetime.Dispose();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask ApplyCoreAsync(StreamingDeliveryBatch batch, CancellationToken token)
    {
        StreamingSubscriptionCheckpoint checkpoint = batch.CandidateCheckpoint;
        ArgumentNullException.ThrowIfNull(checkpoint);
        checkpoint.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.DeliveryId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batch.Attempt, 1);
        if (batch.DeliveryId.Length > 256 || checkpoint.SubscriptionId != Definition.SubscriptionId
            || batch.Status is not (StreamingDeliveryStatus.InFlight or StreamingDeliveryStatus.Redelivered))
            throw new ArgumentException("窗口批次的订阅身份或投递状态不正确。", nameof(batch));
        ArgumentNullException.ThrowIfNull(batch.Events);
        if (batch.Events.Count < 1 || batch.Events.Count > Options.MaxBatchEvents)
            throw new ArgumentException("窗口批次事件数量超过边界或为空。", nameof(batch));
        StreamingEvent[] events = batch.Events.ToArray();
        if (events.Length < 1 || events.Length > Options.MaxBatchEvents)
            throw new ArgumentException("窗口批次事件数量超过边界或为空。", nameof(batch));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long previousSequence = -1;
        int bytes = 0;
        foreach (StreamingEvent value in events)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            if (value.Sequence <= previousSequence)
                throw new ArgumentException("批次事件序号必须严格递增。", nameof(batch));
            previousSequence = value.Sequence;
            if (value.Payload.Length > Options.MaxBatchBytes || value.EventId.Length > 4096
                || value.Headers?.Count > 64)
                throw new ArgumentException("窗口事件超过单批容量边界。", nameof(batch));
            if (value.Headers is not null)
            {
                foreach ((string key, string header) in value.Headers)
                {
                    token.ThrowIfCancellationRequested();
                    if (key is null || header is null || key.Length > 4096 || header.Length > 4096)
                        throw new ArgumentException("窗口事件头超过单批容量边界。", nameof(batch));
                }
            }

            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(value, StreamingJsonContext.Default.StreamingEvent);
            bytes = checked(bytes + encoded.Length);
            if (bytes > Options.MaxBatchBytes)
                throw new ArgumentException("窗口批次 JSON 超过字节上限。", nameof(batch));
            hash.AppendData(encoded);
        }

        if (checkpoint.CommittedSequence != previousSequence)
            throw new ArgumentException("批次检查点必须指向批次最后事件。", nameof(batch));
        string batchHash = Convert.ToHexString(hash.GetHashAndReset());
        if (checkpoint.Revision == _document.AppliedRevision
            && checkpoint.CommittedSequence == _document.AppliedSequence)
        {
            if (batch.DeliveryId != _document.LastDeliveryId || batchHash != _document.LastBatchHash)
                throw new InvalidDataException("已应用批次的重投标识或内容不一致。");
            return;
        }

        if (checkpoint.Revision != checked(_document.AppliedRevision + 1)
            || events[0].Sequence <= _document.AppliedSequence)
            throw new InvalidDataException("窗口批次位点倒退、缺失前置批次或重复了已应用前缀。");

        var windows = new Dictionary<long, long>(_document.Windows);
        long dropped = _document.DroppedLateEvents;
        foreach (StreamingEvent value in events)
        {
            token.ThrowIfCancellationRequested();
            long startTicks = WindowStart(value.EventTimeUtc);
            if (IsClosed(WindowEnd(startTicks), _document.WatermarkUtc))
            {
                if (Definition.LateEventPolicy == StreamingWindowLateEventPolicy.Reject)
                    throw new StreamingLateEventException(value.EventId);
                dropped = checked(dropped + 1);
                continue;
            }

            if (windows.TryGetValue(startTicks, out long count))
                windows[startTicks] = checked(count + 1);
            else
            {
                if (windows.Count >= Options.MaxWindows)
                    throw new InvalidOperationException("持久窗口容量已满；读取并移除已关闭结果后重试批次。");
                windows.Add(startTicks, 1);
            }
        }

        await CommitAsync(_document with
        {
            AppliedSequence = checkpoint.CommittedSequence,
            AppliedRevision = checkpoint.Revision,
            DroppedLateEvents = dropped,
            LastDeliveryId = batch.DeliveryId,
            LastBatchHash = batchHash,
            Windows = windows,
        }, token).ConfigureAwait(false);
    }

    private async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken callerToken)
    {
        EnsureOperational();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetimeToken);
        deadline.CancelAfter(Options.OperationTimeoutMilliseconds);
        bool entered = false;
        try
        {
            await _operationGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            EnsureOperational();
            deadline.Token.ThrowIfCancellationRequested();
            return await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested && !_lifetimeToken.IsCancellationRequested)
        {
            throw new TimeoutException("窗口操作超过配置的超时边界。", exception);
        }
        finally
        {
            if (entered)
                _operationGate.Release();
        }
    }

    private async ValueTask CommitAsync(StreamingWindowDocument document, CancellationToken token)
    {
        // 在文件提交之前完成容量校验；容量拒绝不会使当前实例失效。
        byte[] bytes = Encode(document, Options.MaxStateBytes);
        try
        {
            await SaveBytesAsync(bytes, overwrite: true, token).ConfigureAwait(false);
            _document = document;
        }
        catch (Exception exception)
        {
            _fault = exception;
            throw;
        }
    }

    private async ValueTask SaveAsync(StreamingWindowDocument document, bool overwrite, CancellationToken token)
    {
        await SaveBytesAsync(Encode(document, document.Options.MaxStateBytes), overwrite, token).ConfigureAwait(false);
    }

    private async ValueTask SaveBytesAsync(byte[] bytes, bool overwrite, CancellationToken token)
    {
        string temporary = FilePath + ".pending";
        await EnsureTemporaryOwnerAsync(token).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite);
            Wal.DirectoryFsync.FlushRequired(Path.GetDirectoryName(FilePath)!);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 临时清理失败不能掩盖原始提交、取消或恢复错误。
            }
        }
    }

    private async ValueTask EnsureTemporaryOwnerAsync(CancellationToken token)
    {
        if (await ValidateTemporaryOwnerAsync(FilePath, token).ConfigureAwait(false))
            return;
        if (File.Exists(FilePath + ".pending"))
            throw new InvalidDataException("窗口待提交侧文件没有有效所有权标记，不能覆盖未知文件。");
        // owner 先独立刷盘，再触碰固定 pending 路径；中途终止后只有这个已标记侧文件可被回收。
        await using (var stream = new FileStream(FilePath + ".pending.owner", FileMode.CreateNew,
            FileAccess.Write, FileShare.None, bufferSize: 128, options: FileOptions.Asynchronous))
        {
            await stream.WriteAsync(TemporaryOwnerBytes(FilePath), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            stream.Flush(flushToDisk: true);
        }

        Wal.DirectoryFsync.FlushRequired(Path.GetDirectoryName(FilePath)!);
    }

    private static async ValueTask<bool> ValidateTemporaryOwnerAsync(string path, CancellationToken token)
    {
        string owner = path + ".pending.owner";
        if (!File.Exists(owner))
            return false;
        byte[] expected = TemporaryOwnerBytes(path);
        await using var stream = new FileStream(owner, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128, options: FileOptions.Asynchronous);
        if (stream.Length != expected.Length)
            throw new InvalidDataException("窗口待提交侧文件的所有权标记无效，不能回收或覆盖该文件。");
        var actual = new byte[expected.Length];
        await stream.ReadExactlyAsync(actual, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException("窗口待提交侧文件的所有权标记不匹配，不能回收或覆盖该文件。");
        return true;
    }

    private static byte[] TemporaryOwnerBytes(string path)
    {
        string identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Encoding.ASCII.GetBytes("SONNETDB-STREAM-WINDOW-PENDING-V1\n" + digest + "\n");
    }

    private static byte[] Encode(StreamingWindowDocument document, int maxBytes)
    {
        byte[] state = JsonSerializer.SerializeToUtf8Bytes(document, StreamingWindowJsonContext.Default.StreamingWindowDocument);
        if (state.Length > maxBytes)
            throw new InvalidDataException("持久窗口状态超过字节容量上限。");
        var envelope = new StreamingWindowEnvelope(document, Convert.ToHexString(SHA256.HashData(state)));
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(envelope, StreamingWindowJsonContext.Default.StreamingWindowEnvelope);
        if (encoded.Length > maxBytes)
            throw new InvalidDataException("持久窗口状态超过字节容量上限。");
        return encoded;
    }

    private static async ValueTask<StreamingWindowDocument> LoadAsync(string path, StreamingWindowOptions options, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < 1 || stream.Length > options.MaxStateBytes)
            throw new InvalidDataException("持久窗口状态文件为空或超过字节容量上限。");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        StreamingWindowEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(bytes, StreamingWindowJsonContext.Default.StreamingWindowEnvelope)
                ?? throw new InvalidDataException("持久窗口状态 JSON 为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("持久窗口状态 JSON 损坏。", exception);
        }

        if (envelope.State is null || envelope.Sha256 is null || envelope.Sha256.Length != 64)
            throw new InvalidDataException("持久窗口状态缺失或校验和无效。");
        byte[] state = JsonSerializer.SerializeToUtf8Bytes(envelope.State, StreamingWindowJsonContext.Default.StreamingWindowDocument);
        if (envelope.Sha256 != Convert.ToHexString(SHA256.HashData(state)))
            throw new InvalidDataException("持久窗口状态 SHA-256 不匹配。");
        ValidateDocument(envelope.State, token);
        return envelope.State;
    }

    private static void ValidateDocument(StreamingWindowDocument document, CancellationToken token)
    {
        if (document.FormatVersion != FormatVersion || document.Definition is null || document.Options is null
            || document.Windows is null || document.AppliedSequence < -1 || document.AppliedRevision < 0
            || document.DroppedLateEvents < 0 || document.WatermarkUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("持久窗口恢复状态无效。");
        try
        {
            ValidateArguments(document.Definition, document.Options);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("持久窗口定义或容量边界无效。", exception);
        }

        if (document.Windows.Count > document.Options.MaxWindows
            || (document.LastDeliveryId is null) != (document.LastBatchHash is null)
            || document.LastDeliveryId?.Length > 256
            || (document.LastBatchHash is not null && document.LastBatchHash.Length != 64))
            throw new InvalidDataException("持久窗口容量或重投身份无效。");
        if (document.LastBatchHash is not null
            && (document.AppliedSequence < 0 || document.AppliedRevision < 1 || string.IsNullOrWhiteSpace(document.LastDeliveryId)))
            throw new InvalidDataException("持久窗口已应用位点与重投身份不一致。");
        long windowTicks = checked(document.Definition.WindowSizeMilliseconds * TimeSpan.TicksPerMillisecond);
        foreach ((long startTicks, long count) in document.Windows)
        {
            token.ThrowIfCancellationRequested();
            if (startTicks < DateTime.MinValue.Ticks || startTicks > DateTime.MaxValue.Ticks - windowTicks
                || (startTicks - DateTime.UnixEpoch.Ticks) % windowTicks != 0 || count < 1)
                throw new InvalidDataException("持久窗口边界或 COUNT 无效。");
        }
    }

    private long WindowStart(DateTimeOffset eventTimeUtc)
    {
        long windowTicks = checked(Definition.WindowSizeMilliseconds * TimeSpan.TicksPerMillisecond);
        long epochTicks = eventTimeUtc.UtcTicks - DateTime.UnixEpoch.Ticks;
        long bucket = Math.DivRem(epochTicks, windowTicks, out long remainder);
        if (remainder < 0)
            bucket--;
        long startTicks = checked(DateTime.UnixEpoch.Ticks + bucket * windowTicks);
        if (startTicks < DateTime.MinValue.Ticks || startTicks > DateTime.MaxValue.Ticks - windowTicks)
            throw new ArgumentOutOfRangeException(nameof(eventTimeUtc), "事件所属完整窗口超出 UTC 可表示范围。");
        return startTicks;
    }

    private DateTimeOffset WindowEnd(long startTicks)
    {
        return new DateTimeOffset(checked(startTicks + Definition.WindowSizeMilliseconds * TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private bool IsClosed(DateTimeOffset endUtc, DateTimeOffset watermarkUtc)
    {
        long latenessTicks = checked(Definition.AllowedLatenessMilliseconds * TimeSpan.TicksPerMillisecond);
        return watermarkUtc.UtcTicks >= latenessTicks && endUtc.UtcTicks <= watermarkUtc.UtcTicks - latenessTicks;
    }

    private void EnsureOperational()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this);
        if (_fault is not null)
            throw new InvalidOperationException("窗口持久化操作失败；必须关闭并重开以核对提交结果。", _fault);
    }

    private static StreamingWindowState State(StreamingWindowDocument document)
    {
        return new StreamingWindowState(document.AppliedSequence, document.AppliedRevision,
            document.WatermarkUtc, document.DroppedLateEvents, document.Windows.Count);
    }

    private static void ValidateArguments(StreamingWindowDefinition definition, StreamingWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        options.Validate();
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("窗口时间必须为 UTC。", parameterName);
    }

    private static string GetPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static FileStream AcquireLease(string path)
    {
        return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
    }
}
