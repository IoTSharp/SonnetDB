using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Streaming;

/// <summary>为单个本地文件订阅持久化固定 UTC 滚动或滑动 COUNT、decimal 数值及分组窗口和恢复位点。</summary>
/// <remarks>
/// <para>每个批次的窗口和已应用位点写入同一个原子替换文件，之后才确认订阅。
/// 窗口提交后未完成确认的最后批次按稳定投递标识和内容校验重试，不重复计数。</para>
/// <para>只有显式推进的 watermark 才关闭窗口；调用方应先排空已接受的订阅事件。
/// 结果保留至显式删除，容量耗尽会拒绝批次而保留未确认事件。</para>
/// <para>数值窗口显式选择顶层 JSON 属性，支持 SUM/MIN/MAX/AVG；仅支持单机单执行者，没有远程租约或外部副作用事务。</para>
/// </remarks>
public sealed partial class FileStreamingWindowAggregator : IAsyncDisposable
{
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
    /// <param name="definition">固定 UTC 滚动或滑动窗口定义。</param>
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
        ArgumentNullException.ThrowIfNull(definition);
        ValidateArguments(definition, options ??= DefaultOptions(definition));
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
                definition.FormatVersion, definition, options, initialCheckpoint.CommittedSequence,
                initialCheckpoint.Revision, initialCheckpoint.WatermarkUtc, 0, null, null, [])
            {
                NumericWindows = definition.GroupField is null && definition.NumericField is not null ? [] : null,
                GroupedWindows = definition.GroupField is null ? null : [],
            };
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
        ArgumentNullException.ThrowIfNull(definition);
        ValidateArguments(definition, options ??= DefaultOptions(definition));
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
            if (Definition.GroupField is not null)
                throw new InvalidOperationException("分组窗口必须使用 ReadGroupedWindowsAsync 读取，分页位置包含起点与键。");
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

    /// <summary>按 UTC 起点排序有界读取显式数值窗口，返回 COUNT、SUM/MIN/MAX/AVG 与排他起点。</summary>
    /// <param name="maxWindows">最多返回的窗口数量，不能超过配置的最大窗口数。</param>
    /// <param name="afterStartUtc">排他起点；为空时从第一个窗口开始。</param>
    /// <param name="closedOnly">是否只返回已关闭窗口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>数值结果窗口及读取时对应的一致状态。</returns>
    /// <remarks>必须使用 CreateNumeric 定义；SUM、MIN、MAX 无舍入，AVG 使用 decimal 除法舍入。</remarks>
    public async ValueTask<StreamingWindowNumericBatch> ReadNumericWindowsAsync(
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
            if (_document.NumericWindows is not { } numericWindows)
                throw new InvalidOperationException("COUNT 定义没有数值聚合结果；必须显式创建数值窗口。");
            var results = new List<StreamingWindowNumeric>(maxWindows);
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

                StreamingWindowNumericAccumulator numeric = numericWindows[startTicks];
                results.Add(new StreamingWindowNumeric(start, end, count,
                    numeric.Sum, numeric.Min, numeric.Max, numeric.Sum / count, closed));
            }

            return ValueTask.FromResult(new StreamingWindowNumericBatch(
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
            if (_document.GroupedWindows is not null)
                return await RemoveClosedGroupedWindowsAsync(throughStartUtc, token).ConfigureAwait(false);
            var windows = new Dictionary<long, long>(_document.Windows);
            Dictionary<long, StreamingWindowNumericAccumulator>? numericWindows = _document.NumericWindows is { } numeric
                ? new(numeric) : null;
            int removed = 0;
            foreach (long startTicks in _document.Windows.Keys)
            {
                token.ThrowIfCancellationRequested();
                if (startTicks <= throughStartUtc.UtcTicks && IsClosed(WindowEnd(startTicks), _document.WatermarkUtc))
                {
                    windows.Remove(startTicks);
                    numericWindows?.Remove(startTicks);
                    removed++;
                }
            }

            if (removed > 0)
                await CommitAsync(_document with { Windows = windows, NumericWindows = numericWindows }, token).ConfigureAwait(false);
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
        int eventCount = batch.Events.Count;
        if (eventCount < 1 || eventCount > Options.MaxBatchEvents)
            throw new ArgumentException("窗口批次事件数量超过边界或为空。", nameof(batch));
        var events = new StreamingEvent[eventCount];

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long previousSequence = -1;
        int bytes = 0;
        int copiedPayloadBytes = 0;
        for (int eventIndex = 0; eventIndex < eventCount; eventIndex++)
        {
            token.ThrowIfCancellationRequested();
            StreamingEvent value = batch.Events[eventIndex];
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            if (value.Sequence <= previousSequence)
                throw new ArgumentException("批次事件序号必须严格递增。", nameof(batch));
            previousSequence = value.Sequence;
            if (value.Payload.Length > Options.MaxBatchBytes || value.EventId.Length > 4096
                || value.Headers?.Count > 64)
                throw new ArgumentException("窗口事件超过单批容量边界。", nameof(batch));
            copiedPayloadBytes = checked(copiedPayloadBytes + value.Payload.Length);
            if (copiedPayloadBytes > Options.MaxBatchBytes)
                throw new ArgumentException("窗口批次 payload 快照超过总复制字节上限。", nameof(batch));
            // 先复制有界 payload，再枚举调用方 headers；枚举器可能修改原 payload，不能让哈希和数值解析看到不同内容。
            byte[] payload = value.Payload.ToArray();
            Dictionary<string, string>? headers = null;
            if (value.Headers is not null)
            {
                headers = new Dictionary<string, string>(StringComparer.Ordinal);
                using IEnumerator<KeyValuePair<string, string>> iterator = value.Headers.GetEnumerator();
                for (int headerIndex = 0; headerIndex <= 64 && iterator.MoveNext(); headerIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    if (headerIndex == 64)
                        throw new ArgumentException("窗口事件头超过数量上限。", nameof(batch));
                    (string key, string header) = iterator.Current;
                    if (key is null || header is null || key.Length > 4096 || header.Length > 4096)
                        throw new ArgumentException("窗口事件头超过单批容量边界。", nameof(batch));
                    if (!headers.TryAdd(key, header))
                        throw new ArgumentException("窗口事件头包含重复键。", nameof(batch));
                }
            }

            value = value with { Payload = payload, Headers = headers };
            value.Validate();
            events[eventIndex] = value;
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

        if (_document.GroupedWindows is not null)
        {
            await ApplyGroupedCoreAsync(batch, checkpoint, events, batchHash, token).ConfigureAwait(false);
            return;
        }

        var windows = new Dictionary<long, long>(_document.Windows);
        Dictionary<long, StreamingWindowNumericAccumulator>? numericWindows = _document.NumericWindows is { } numeric
            ? new(numeric) : null;
        long dropped = _document.DroppedLateEvents;
        foreach (StreamingEvent value in events)
        {
            token.ThrowIfCancellationRequested();
            List<long> starts = OpenWindowStarts(value, token);
            if (starts.Count == 0)
            {
                dropped = checked(dropped + 1);
                continue;
            }

            decimal numericValue = Definition.NumericField is { } field ? ReadNumericValue(value.Payload, field, token) : 0;
            foreach (long startTicks in starts)
            {
                token.ThrowIfCancellationRequested();
                if (windows.TryGetValue(startTicks, out long count))
                {
                    windows[startTicks] = checked(count + 1);
                    if (numericWindows is not null)
                    {
                        StreamingWindowNumericAccumulator accumulator = numericWindows[startTicks];
                        numericWindows[startTicks] = new StreamingWindowNumericAccumulator(
                            AddExact(accumulator.Sum, numericValue),
                            Math.Min(accumulator.Min, numericValue), Math.Max(accumulator.Max, numericValue));
                    }
                }
                else
                {
                    if (windows.Count >= Options.MaxWindows)
                        throw new InvalidOperationException("持久窗口容量已满；读取并移除已关闭结果后重试批次。");
                    windows.Add(startTicks, 1);
                    numericWindows?.Add(startTicks, new StreamingWindowNumericAccumulator(numericValue, numericValue, numericValue));
                }
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
            NumericWindows = numericWindows,
        }, token).ConfigureAwait(false);
    }

    private static decimal ReadNumericValue(byte[] payload, string field, CancellationToken token)
    {
        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("数值窗口 payload 必须为 JSON 对象。");
            bool found = false;
            decimal value = 0;
            // payload 已受 MaxBatchBytes 限制；每个 token 都观察操作 deadline 和调用方取消。
            for (int tokenCount = 0; tokenCount < payload.Length && reader.Read(); tokenCount++)
            {
                token.ThrowIfCancellationRequested();
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    if (reader.Read())
                        throw new InvalidDataException("数值窗口 payload 不能包含多个 JSON 根值。");
                    if (!found)
                        throw new InvalidDataException("数值窗口选择的顶层属性缺失。");
                    return value;
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals(field))
                    continue;
                if (found)
                    throw new InvalidDataException("数值窗口选择的顶层属性重复。");
                found = true;
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number
                    || !TryReadExactDecimal(reader.ValueSpan, out value))
                    throw new InvalidDataException("数值窗口属性必须为可精确表示的 decimal JSON 数字，数字 token 最长为一百二十八字节且指数绝对值不能超过一千。");
            }

            throw new InvalidDataException("数值窗口 payload 不完整。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("数值窗口 payload JSON 无效。", exception);
        }
    }

    private static bool TryReadExactDecimal(ReadOnlySpan<byte> utf8, out decimal value)
    {
        value = 0;
        if (utf8.Length is < 1 or > 128)
            return false;
        bool negative = utf8[0] == (byte)'-';
        BigInteger coefficient = BigInteger.Zero;
        int fractionalDigits = 0;
        bool fractional = false;
        int exponentStart = utf8.Length;
        for (int index = negative ? 1 : 0; index < utf8.Length; index++)
        {
            byte current = utf8[index];
            if (current is (byte)'e' or (byte)'E')
            {
                exponentStart = index + 1;
                break;
            }

            if (current == (byte)'.')
                fractional = true;
            else
            {
                coefficient = coefficient * 10 + current - (byte)'0';
                if (fractional)
                    fractionalDigits++;
            }
        }

        int exponent = 0;
        bool negativeExponent = false;
        if (exponentStart < utf8.Length && utf8[exponentStart] is (byte)'+' or (byte)'-')
        {
            negativeExponent = utf8[exponentStart] == (byte)'-';
            exponentStart++;
        }

        for (int index = exponentStart; index < utf8.Length; index++)
        {
            exponent = exponent * 10 + utf8[index] - (byte)'0';
            if (exponent > 1000)
                return false;
        }

        if (negativeExponent)
            exponent = -exponent;
        int scale = fractionalDigits - exponent;
        if (coefficient.IsZero)
            return true;
        if (scale < 0)
        {
            if (scale < -28)
                return false;
            coefficient *= BigInteger.Pow(10, -scale);
            scale = 0;
        }

        return TryCreateDecimal(negative ? -coefficient : coefficient, scale, out value);
    }

    private static decimal AddExact(decimal left, decimal right)
    {
        (BigInteger leftCoefficient, int leftScale) = DecimalParts(left);
        (BigInteger rightCoefficient, int rightScale) = DecimalParts(right);
        int scale = Math.Max(leftScale, rightScale);
        BigInteger sum = leftCoefficient * BigInteger.Pow(10, scale - leftScale)
            + rightCoefficient * BigInteger.Pow(10, scale - rightScale);
        if (!TryCreateDecimal(sum, scale, out decimal value))
            throw new OverflowException("窗口 SUM 不能以 decimal 精确表示；整个批次保持未应用。");
        return value;
    }

    private static (BigInteger Coefficient, int Scale) DecimalParts(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        BigInteger coefficient = (uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        return ((bits[3] & int.MinValue) != 0 ? -coefficient : coefficient, (bits[3] >> 16) & 0xFF);
    }

    private static bool TryCreateDecimal(BigInteger coefficient, int scale, out decimal value)
    {
        value = 0;
        bool negative = coefficient.Sign < 0;
        coefficient = BigInteger.Abs(coefficient);
        if (coefficient.IsZero)
            return true;
        // 最多 128 位输入数字；SUM 的对齐系数最多 57 位，故去尾零始终受固定迭代上限约束。
        for (int removed = 0; removed < 128 && scale > 0 && coefficient % 10 == 0; removed++)
        {
            coefficient /= 10;
            scale--;
        }

        if (scale is < 0 or > 28 || coefficient > (BigInteger.One << 96) - 1)
            return false;
        uint low = (uint)(coefficient & uint.MaxValue);
        uint middle = (uint)((coefficient >> 32) & uint.MaxValue);
        uint high = (uint)(coefficient >> 64);
        value = new decimal(unchecked((int)low), unchecked((int)middle), unchecked((int)high), negative, (byte)scale);
        return true;
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
        if (document.FormatVersion == StreamingWindowDefinition.CurrentFormatVersion)
        {
            StreamingWindowDefinition definition = document.Definition;
            var legacy = new LegacyStreamingWindowDocument(document.FormatVersion,
                new LegacyStreamingWindowDefinition(definition.FormatVersion, definition.SubscriptionId,
                    definition.StreamName, definition.WindowSizeMilliseconds, definition.AllowedLatenessMilliseconds, definition.LateEventPolicy),
                document.Options, document.AppliedSequence, document.AppliedRevision, document.WatermarkUtc,
                document.DroppedLateEvents, document.LastDeliveryId, document.LastBatchHash, document.Windows);
            byte[] legacyState = JsonSerializer.SerializeToUtf8Bytes(legacy, StreamingWindowJsonContext.Default.LegacyStreamingWindowDocument);
            byte[] legacyEncoded = JsonSerializer.SerializeToUtf8Bytes(
                new LegacyStreamingWindowEnvelope(legacy, Convert.ToHexString(SHA256.HashData(legacyState))),
                StreamingWindowJsonContext.Default.LegacyStreamingWindowEnvelope);
            if (legacyEncoded.Length > maxBytes)
                throw new InvalidDataException("持久窗口状态超过字节容量上限。");
            return legacyEncoded;
        }

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
            using JsonDocument json = JsonDocument.Parse(bytes);
            token.ThrowIfCancellationRequested();
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("state", out JsonElement stateElement)
                || stateElement.ValueKind != JsonValueKind.Object
                || !stateElement.TryGetProperty("formatVersion", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int formatVersion))
                throw new InvalidDataException("持久窗口状态缺少格式版本。");
            if (formatVersion is StreamingWindowDefinition.GroupedFormatVersion or StreamingWindowDefinition.SlidingFormatVersion)
            {
                RejectDuplicateGroupedJsonProperties(bytes, token);
                if (formatVersion == StreamingWindowDefinition.SlidingFormatVersion)
                    ValidateSlidingJsonRequiredFields(stateElement);
                else
                    ValidateGroupedJsonRequiredFields(stateElement);
            }
            if (formatVersion is not (StreamingWindowDefinition.GroupedFormatVersion or StreamingWindowDefinition.SlidingFormatVersion)
                && (stateElement.TryGetProperty("groupedWindows", out _)
                    || (stateElement.TryGetProperty("definition", out JsonElement rawDefinition)
                        && rawDefinition.ValueKind == JsonValueKind.Object && rawDefinition.TryGetProperty("groupField", out _))
                    || (stateElement.TryGetProperty("options", out JsonElement rawOptions)
                        && rawOptions.ValueKind == JsonValueKind.Object && rawOptions.TryGetProperty("maxGroups", out _))))
                throw new InvalidDataException("旧版窗口状态不能包含分组字段，即使字段值为空。");
            if (formatVersion != StreamingWindowDefinition.SlidingFormatVersion
                && stateElement.TryGetProperty("definition", out JsonElement slidingDefinition)
                && slidingDefinition.ValueKind == JsonValueKind.Object
                && slidingDefinition.TryGetProperty("slideMilliseconds", out _))
                throw new InvalidDataException("旧版窗口状态不能包含滑动步长，即使字段值为空。");
            if (formatVersion == StreamingWindowDefinition.CurrentFormatVersion)
            {
                LegacyStreamingWindowEnvelope legacy = JsonSerializer.Deserialize(bytes, StreamingWindowJsonContext.Default.LegacyStreamingWindowEnvelope)
                    ?? throw new InvalidDataException("持久 COUNT 窗口状态 JSON 为空。");
                if (legacy.State is null || legacy.State.Definition is null || legacy.Sha256 is null || legacy.Sha256.Length != 64)
                    throw new InvalidDataException("持久 COUNT 窗口状态或校验和无效。");
                byte[] legacyBytes = JsonSerializer.SerializeToUtf8Bytes(legacy.State, StreamingWindowJsonContext.Default.LegacyStreamingWindowDocument);
                if (legacy.Sha256 != Convert.ToHexString(SHA256.HashData(legacyBytes)))
                    throw new InvalidDataException("持久 COUNT 窗口原格式 SHA-256 不匹配。");
                LegacyStreamingWindowDefinition definition = legacy.State.Definition;
                var restored = new StreamingWindowDocument(legacy.State.FormatVersion,
                    new StreamingWindowDefinition(definition.FormatVersion, definition.SubscriptionId, definition.StreamName,
                        definition.WindowSizeMilliseconds, definition.AllowedLatenessMilliseconds, definition.LateEventPolicy),
                    legacy.State.Options, legacy.State.AppliedSequence, legacy.State.AppliedRevision, legacy.State.WatermarkUtc,
                    legacy.State.DroppedLateEvents, legacy.State.LastDeliveryId, legacy.State.LastBatchHash, legacy.State.Windows);
                ValidateDocument(restored, token);
                token.ThrowIfCancellationRequested();
                return restored;
            }

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
        if (document.FormatVersion is not (StreamingWindowDefinition.CurrentFormatVersion or StreamingWindowDefinition.NumericFormatVersion or StreamingWindowDefinition.GroupedFormatVersion or StreamingWindowDefinition.SlidingFormatVersion)
            || document.Definition is null || document.Options is null
            || document.Windows is null || document.AppliedSequence < -1 || document.AppliedRevision < 0
            || document.DroppedLateEvents < 0 || document.WatermarkUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("持久窗口恢复状态无效。");
        bool grouped = document.Definition.GroupField is not null;
        if (document.FormatVersion != document.Definition.FormatVersion
            || grouped != (document.GroupedWindows is not null)
            || (grouped && (document.Windows.Count != 0 || document.NumericWindows is not null))
            || (!grouped && (document.Definition.NumericField is null) != (document.NumericWindows is null))
            || (document.NumericWindows is { } numericWindows && numericWindows.Count != document.Windows.Count))
            throw new InvalidDataException("持久数值窗口状态与定义或 COUNT 不一致。");
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
        if (grouped)
        {
            ValidateGroupedDocument(document, token);
            return;
        }
        long windowTicks = checked(document.Definition.WindowSizeMilliseconds * TimeSpan.TicksPerMillisecond);
        long alignmentTicks = checked((document.Definition.SlideMilliseconds ?? document.Definition.WindowSizeMilliseconds) * TimeSpan.TicksPerMillisecond);
        foreach ((long startTicks, long count) in document.Windows)
        {
            token.ThrowIfCancellationRequested();
            if (startTicks < DateTime.MinValue.Ticks || startTicks > DateTime.MaxValue.Ticks - windowTicks
                || (startTicks - DateTime.UnixEpoch.Ticks) % alignmentTicks != 0 || count < 1)
                throw new InvalidDataException("持久窗口边界或 COUNT 无效。");
            if (document.NumericWindows is { } numeric
                && (!numeric.TryGetValue(startTicks, out StreamingWindowNumericAccumulator? accumulator)
                    || accumulator is null || accumulator.Min > accumulator.Max
                    || accumulator.Sum / count < accumulator.Min || accumulator.Sum / count > accumulator.Max
                    || (count == 1 && (accumulator.Sum != accumulator.Min || accumulator.Sum != accumulator.Max))))
                throw new InvalidDataException("持久窗口数值聚合状态无效。");
        }
    }

    private long WindowStart(DateTimeOffset eventTimeUtc)
    {
        long windowTicks = checked(Definition.WindowSizeMilliseconds * TimeSpan.TicksPerMillisecond);
        long alignmentTicks = checked((Definition.SlideMilliseconds ?? Definition.WindowSizeMilliseconds) * TimeSpan.TicksPerMillisecond);
        long epochTicks = eventTimeUtc.UtcTicks - DateTime.UnixEpoch.Ticks;
        long bucket = Math.DivRem(epochTicks, alignmentTicks, out long remainder);
        if (remainder < 0)
            bucket--;
        long startTicks = checked(DateTime.UnixEpoch.Ticks + bucket * alignmentTicks);
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
            document.WatermarkUtc, document.DroppedLateEvents, document.GroupedWindows?.Count ?? document.Windows.Count);
    }

    private static void ValidateArguments(StreamingWindowDefinition definition, StreamingWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        options.Validate();
        if ((definition.GroupField is null) != (options.MaxGroups is null))
            throw new ArgumentException("分组版本必须配置 MaxGroups，未分组版本不能配置分组容量。", nameof(options));
    }

    private static StreamingWindowOptions DefaultOptions(StreamingWindowDefinition definition)
    {
        return new StreamingWindowOptions { MaxGroups = definition.GroupField is null ? null : 1000 };
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
