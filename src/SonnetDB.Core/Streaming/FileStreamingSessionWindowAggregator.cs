using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Streaming;

/// <summary>为本地文件订阅提供持久、可恢复的会话 COUNT 窗口。</summary>
/// <remarks>
/// <para>相邻事件时间差小于或等于 inactivity gap 时属于同一会话；乱序事件可以桥接两个尚未关闭的会话。</para>
/// <para>窗口状态、位点、最近批次身份和 SHA-256 在一次原子提交中写入，之后才确认订阅批次。</para>
/// <para>单机单执行者由锁文件保证；状态提交成功而 ACK 尚未完成时，重投批次按 delivery 身份去重。</para>
/// </remarks>
public sealed class FileStreamingSessionWindowAggregator : IAsyncDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly FileStream _lease;
    private readonly string _path;
    private StreamingSessionWindowDocument _document;
    private Exception? _fault;
    private int _closing;
    private int _closed;

    private FileStreamingSessionWindowAggregator(string path, FileStream lease, StreamingSessionWindowDocument document)
    {
        _path = path;
        _lease = lease;
        _document = document;
    }

    /// <summary>会话状态文件绝对路径。</summary>
    public string FilePath => _path;

    /// <summary>持久保存的会话窗口定义。</summary>
    public StreamingSessionWindowDefinition Definition => _document.Definition;

    /// <summary>持久保存的容量及操作边界。</summary>
    public StreamingSessionWindowOptions Options => _document.Options;

    /// <summary>创建新的会话窗口状态文件。</summary>
    /// <param name="path">状态文件路径。</param>
    /// <param name="definition">会话窗口定义。</param>
    /// <param name="initialCheckpoint">初始订阅检查点。</param>
    /// <param name="options">容量与超时边界。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已持久化并持有本地锁的聚合器。</returns>
    public static async ValueTask<FileStreamingSessionWindowAggregator> CreateAsync(
        string path,
        StreamingSessionWindowDefinition definition,
        StreamingSubscriptionCheckpoint? initialCheckpoint = null,
        StreamingSessionWindowOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        options ??= new StreamingSessionWindowOptions();
        options.Validate();
        initialCheckpoint ??= StreamingSubscriptionCheckpoint.Create(definition.SubscriptionId);
        initialCheckpoint.Validate();
        if (initialCheckpoint.SubscriptionId != definition.SubscriptionId)
            throw new ArgumentException("初始检查点属于其他订阅。", nameof(initialCheckpoint));
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = FullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        FileStream lease = AcquireLease(fullPath);
        FileStreamingSessionWindowAggregator? aggregator = null;
        try
        {
            if (File.Exists(fullPath) || File.Exists(fullPath + ".pending"))
                throw new IOException("会话窗口状态已经存在，请使用 OpenAsync 恢复。");
            var document = new StreamingSessionWindowDocument(
                StreamingSessionWindowDefinition.CurrentFormatVersion,
                definition,
                options,
                initialCheckpoint.CommittedSequence,
                initialCheckpoint.Revision,
                initialCheckpoint.WatermarkUtc,
                0,
                null,
                null,
                null,
                []);
            aggregator = new FileStreamingSessionWindowAggregator(fullPath, lease, document);
            await aggregator.CommitBytesAsync(Encode(document, options.MaxStateBytes), overwrite: false, cancellationToken).ConfigureAwait(false);
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

    /// <summary>重开并校验已有会话状态文件。</summary>
    /// <param name="path">状态文件路径。</param>
    /// <param name="definition">必须与保存定义完全一致。</param>
    /// <param name="options">必须与保存边界完全一致。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>恢复后的聚合器。</returns>
    public static async ValueTask<FileStreamingSessionWindowAggregator> OpenAsync(
        string path,
        StreamingSessionWindowDefinition definition,
        StreamingSessionWindowOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        options ??= new StreamingSessionWindowOptions();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = FullPath(path);
        FileStream lease = AcquireLease(fullPath);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.OperationTimeoutMilliseconds);
            StreamingSessionWindowDocument document = await LoadAsync(fullPath, options, deadline.Token).ConfigureAwait(false);
            if (document.Definition != definition || document.Options != options)
                throw new InvalidDataException("会话窗口定义或容量边界与持久状态不一致。");
            if (File.Exists(fullPath + ".pending"))
            {
                if (!await ValidateOwnerAsync(fullPath, deadline.Token).ConfigureAwait(false))
                    throw new InvalidDataException("会话窗口待提交文件缺少有效所有权标记。");
                File.Delete(fullPath + ".pending");
                Wal.DirectoryFsync.FlushRequired(Path.GetDirectoryName(fullPath)!);
            }
            return new FileStreamingSessionWindowAggregator(fullPath, lease, document);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException("会话窗口状态恢复超过操作超时边界。", exception);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>应用一个订阅批次并持久保存会话结果。</summary>
    /// <param name="batch">完整有界的订阅批次。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用后的状态快照。</returns>
    public async ValueTask<StreamingSessionWindowState> ApplyBatchAsync(
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

    /// <summary>从真实文件订阅读取、提交会话状态并确认一个批次。</summary>
    /// <param name="subscription">定义匹配的持久文件订阅。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>处理了批次时为真；订阅已结束且排空时为假。</returns>
    public async ValueTask<bool> PumpOnceAsync(FileStreamingSubscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return await ExecuteAsync(async token =>
        {
            if (subscription.Definition.SubscriptionId != Definition.SubscriptionId
                || subscription.Definition.StreamName != Definition.StreamName)
                throw new ArgumentException("订阅与会话窗口定义不匹配。", nameof(subscription));
            StreamingSubscriptionCheckpoint checkpoint = subscription.Checkpoint;
            if (checkpoint.CommittedSequence > _document.AppliedSequence || checkpoint.Revision > _document.AppliedRevision)
                throw new InvalidDataException("订阅已经确认窗口聚合器尚未应用的事件。");
            StreamingDeliveryBatch? batch = await subscription.ReadBatchAsync(token).ConfigureAwait(false);
            if (batch is null)
            {
                if (checkpoint.CommittedSequence != _document.AppliedSequence || checkpoint.Revision != _document.AppliedRevision)
                    throw new InvalidDataException("订阅已排空但会话窗口位点不一致。");
                return false;
            }

            await ApplyCoreAsync(batch, token).ConfigureAwait(false);
            await subscription.AcknowledgeAsync(batch.DeliveryId, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>单调持久推进 UTC watermark；终点加允许迟到时间不晚于 watermark 时会话关闭。</summary>
    /// <param name="watermarkUtc">新的 UTC watermark。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask AdvanceWatermarkAsync(DateTimeOffset watermarkUtc, CancellationToken cancellationToken = default)
    {
        ValidateUtc(watermarkUtc, nameof(watermarkUtc));
        await ExecuteAsync(async token =>
        {
            if (watermarkUtc < _document.WatermarkUtc)
                throw new ArgumentOutOfRangeException(nameof(watermarkUtc), "watermark 不能回退。");
            if (watermarkUtc != _document.WatermarkUtc)
                await CommitAsync(_document with { WatermarkUtc = watermarkUtc }, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取按会话起点排序的有界结果页。</summary>
    /// <param name="maxWindows">最多返回数量。</param>
    /// <param name="afterStartUtc">排他起点。</param>
    /// <param name="closedOnly">是否只返回已关闭会话。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果页和状态快照。</returns>
    public async ValueTask<StreamingSessionWindowBatch> ReadSessionsAsync(
        int maxWindows = 100,
        DateTimeOffset? afterStartUtc = null,
        bool closedOnly = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWindows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxWindows, Options.MaxSessions);
        if (afterStartUtc is { } after)
            ValidateUtc(after, nameof(afterStartUtc));
        return await ExecuteAsync(token =>
        {
            var result = new List<StreamingSessionWindow>(maxWindows);
            bool hasMore = false;
            foreach (StreamingSessionMember member in _document.Sessions)
            {
                token.ThrowIfCancellationRequested();
                DateTimeOffset start = new(member.StartTicks, TimeSpan.Zero);
                if (afterStartUtc is { } boundary && start <= boundary)
                    continue;
                bool closed = IsClosed(member, _document.WatermarkUtc);
                if (closedOnly && !closed)
                    continue;
                if (result.Count == maxWindows)
                {
                    hasMore = true;
                    break;
                }
                result.Add(new StreamingSessionWindow(start, new DateTimeOffset(member.EndTicks, TimeSpan.Zero), member.Count, closed));
            }
            return ValueTask.FromResult(new StreamingSessionWindowBatch(
                result.AsReadOnly(), result.Count == 0 ? null : result[^1].StartUtc, hasMore, State(_document)));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取会话结果的窗口别名，便于与固定窗口读取代码共用调用路径。</summary>
    /// <param name="maxWindows">最多返回数量。</param>
    /// <param name="afterStartUtc">排他起点。</param>
    /// <param name="closedOnly">是否只返回已关闭会话。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果页和状态快照。</returns>
    public ValueTask<StreamingSessionWindowBatch> ReadWindowsAsync(
        int maxWindows = 100,
        DateTimeOffset? afterStartUtc = null,
        bool closedOnly = false,
        CancellationToken cancellationToken = default)
        => ReadSessionsAsync(maxWindows, afterStartUtc, closedOnly, cancellationToken);

    /// <summary>读取会话窗口结果的显式命名入口。</summary>
    /// <param name="maxWindows">最多返回数量。</param>
    /// <param name="afterStartUtc">排他起点。</param>
    /// <param name="closedOnly">是否只返回已关闭会话。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果页和状态快照。</returns>
    public ValueTask<StreamingSessionWindowBatch> ReadSessionWindowsAsync(
        int maxWindows = 100,
        DateTimeOffset? afterStartUtc = null,
        bool closedOnly = false,
        CancellationToken cancellationToken = default)
        => ReadSessionsAsync(maxWindows, afterStartUtc, closedOnly, cancellationToken);

    /// <summary>移除指定起点以前已关闭的会话，并保留回收边界避免迟到事件复活。</summary>
    /// <param name="throughStartUtc">包含的最大会话起点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>移除的会话数量。</returns>
    public async ValueTask<int> RemoveClosedSessionsAsync(DateTimeOffset throughStartUtc, CancellationToken cancellationToken = default)
    {
        ValidateUtc(throughStartUtc, nameof(throughStartUtc));
        return await ExecuteAsync(async token =>
        {
            var remaining = new List<StreamingSessionMember>(_document.Sessions.Count);
            int removed = 0;
            long retiredTicks = _document.RetiredUntilUtc?.UtcTicks ?? long.MinValue;
            foreach (StreamingSessionMember member in _document.Sessions)
            {
                token.ThrowIfCancellationRequested();
                DateTimeOffset start = new(member.StartTicks, TimeSpan.Zero);
                if (start <= throughStartUtc && IsClosed(member, _document.WatermarkUtc))
                {
                    removed++;
                    retiredTicks = Math.Max(retiredTicks, member.EndTicks);
                }
                else
                    remaining.Add(member);
            }
            if (removed == 0)
                return 0;
            DateTimeOffset? retired = retiredTicks == long.MinValue ? null : new DateTimeOffset(retiredTicks, TimeSpan.Zero);
            await CommitAsync(_document with { Sessions = remaining, RetiredUntilUtc = retired }, token).ConfigureAwait(false);
            return removed;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>移除指定起点以前已关闭的会话窗口；与固定窗口聚合器保持同名调用入口。</summary>
    /// <param name="throughStartUtc">包含的最大会话起点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>移除的会话数量。</returns>
    public ValueTask<int> RemoveClosedWindowsAsync(DateTimeOffset throughStartUtc, CancellationToken cancellationToken = default)
        => RemoveClosedSessionsAsync(throughStartUtc, cancellationToken);

    /// <summary>读取最近持久状态快照。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>状态快照。</returns>
    public async ValueTask<StreamingSessionWindowState> GetStateAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(State(_document));
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>关闭并释放本地 lease，持久状态保留供重开。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _closed) != 0)
            return;
        if (Interlocked.Exchange(ref _closing, 1) == 0)
            await _lifetime.CancelAsync().ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(Options.OperationTimeoutMilliseconds);
        bool entered = false;
        bool completed = false;
        try
        {
            await _operationGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            if (Interlocked.Exchange(ref _closed, 1) == 0)
                await _lease.DisposeAsync().ConfigureAwait(false);
            completed = true;
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("会话窗口关闭超过操作时限。", exception);
        }
        finally
        {
            if (entered)
                _operationGate.Release();
            if (completed)
            {
                _lifetime.Dispose();
                _operationGate.Dispose();
            }
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
            throw new ArgumentException("会话窗口批次的订阅身份或状态不正确。", nameof(batch));
        ArgumentNullException.ThrowIfNull(batch.Events);
        int count = batch.Events.Count;
        if (count < 1 || count > Options.MaxBatchEvents)
            throw new ArgumentException("会话窗口批次事件数量超过边界或为空。", nameof(batch));

        var events = new StreamingEvent[count];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long previousSequence = -1;
        int batchBytes = 0;
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            StreamingEvent value = batch.Events[index] ?? throw new ArgumentException("批次包含空事件。", nameof(batch));
            value.Validate();
            if (value.Sequence <= previousSequence)
                throw new ArgumentException("批次事件序号必须严格递增。", nameof(batch));
            previousSequence = value.Sequence;
            if (value.EventId.Length > 4096 || value.Payload.Length > Options.MaxBatchBytes)
                throw new ArgumentException("会话事件超过单批容量边界。", nameof(batch));
            byte[] payload = value.Payload.ToArray();
            Dictionary<string, string>? headers = null;
            if (value.Headers is not null)
            {
                headers = new Dictionary<string, string>(StringComparer.Ordinal);
                using IEnumerator<KeyValuePair<string, string>> iterator = value.Headers.GetEnumerator();
                for (int headerIndex = 0; headerIndex <= 64 && iterator.MoveNext(); headerIndex++)
                {
                    if (headerIndex == 64)
                        throw new ArgumentException("会话事件头超过数量上限。", nameof(batch));
                    (string key, string header) = iterator.Current;
                    if (key is null || header is null || key.Length > 4096 || header.Length > 4096 || !headers.TryAdd(key, header))
                        throw new ArgumentException("会话事件头无效或包含重复键。", nameof(batch));
                }
            }
            value = value with { Payload = payload, Headers = headers };
            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(value, StreamingJsonContext.Default.StreamingEvent);
            batchBytes = checked(batchBytes + encoded.Length);
            if (batchBytes > Options.MaxBatchBytes)
                throw new ArgumentException("会话窗口批次 JSON 超过字节边界。", nameof(batch));
            hash.AppendData(encoded);
            events[index] = value;
        }
        if (checkpoint.CommittedSequence != previousSequence)
            throw new ArgumentException("批次检查点必须指向批次最后事件。", nameof(batch));
        string batchHash = Convert.ToHexString(hash.GetHashAndReset());
        if (checkpoint.Revision == _document.AppliedRevision && checkpoint.CommittedSequence == _document.AppliedSequence)
        {
            if (batch.DeliveryId != _document.LastDeliveryId || batchHash != _document.LastBatchHash)
                throw new InvalidDataException("已应用批次的重投标识或内容不一致。");
            return;
        }
        if (checkpoint.Revision != checked(_document.AppliedRevision + 1) || events[0].Sequence <= _document.AppliedSequence)
            throw new InvalidDataException("会话窗口批次位点倒退、缺少前置批次或重复已应用前缀。");

        var sessions = new List<StreamingSessionMember>(_document.Sessions);
        long dropped = _document.DroppedLateEvents;
        foreach (StreamingEvent value in events)
        {
            token.ThrowIfCancellationRequested();
            long eventTicks = value.EventTimeUtc.UtcTicks;
            int insertion = FindInsertion(sessions, eventTicks);
            int leftIndex = insertion - 1;
            int rightIndex = insertion;
            bool leftMatch = leftIndex >= 0 && Matches(sessions[leftIndex], eventTicks);
            bool rightMatch = rightIndex < sessions.Count && Matches(sessions[rightIndex], eventTicks);
            bool retired = _document.RetiredUntilUtc is { } retiredUntil && eventTicks <= retiredUntil.UtcTicks;
            if (retired || (leftMatch && IsClosed(sessions[leftIndex], _document.WatermarkUtc))
                || (rightMatch && IsClosed(sessions[rightIndex], _document.WatermarkUtc)))
            {
                if (Definition.LateEventPolicy == StreamingSessionLateEventPolicy.Reject)
                    throw new StreamingLateEventException(value.EventId);
                dropped = checked(dropped + 1);
                continue;
            }

            if (leftMatch && rightMatch)
            {
                StreamingSessionMember left = sessions[leftIndex];
                StreamingSessionMember right = sessions[rightIndex];
                sessions[leftIndex] = new StreamingSessionMember(
                    Math.Min(left.StartTicks, eventTicks), Math.Max(right.EndTicks, eventTicks), checked(left.Count + right.Count + 1));
                sessions.RemoveAt(rightIndex);
            }
            else if (leftMatch)
            {
                StreamingSessionMember left = sessions[leftIndex];
                sessions[leftIndex] = left with
                {
                    StartTicks = Math.Min(left.StartTicks, eventTicks),
                    EndTicks = Math.Max(left.EndTicks, eventTicks),
                    Count = checked(left.Count + 1),
                };
            }
            else if (rightMatch)
            {
                StreamingSessionMember right = sessions[rightIndex];
                sessions[rightIndex] = right with
                {
                    StartTicks = Math.Min(right.StartTicks, eventTicks),
                    EndTicks = Math.Max(right.EndTicks, eventTicks),
                    Count = checked(right.Count + 1),
                };
            }
            else
            {
                if (sessions.Count >= Options.MaxSessions)
                    throw new InvalidOperationException("持久会话数量已满；回收已关闭会话后重试批次。");
                sessions.Insert(insertion, new StreamingSessionMember(eventTicks, eventTicks, 1));
            }
        }

        await CommitAsync(_document with
        {
            AppliedSequence = checkpoint.CommittedSequence,
            AppliedRevision = checkpoint.Revision,
            DroppedLateEvents = dropped,
            LastDeliveryId = batch.DeliveryId,
            LastBatchHash = batchHash,
            Sessions = sessions,
        }, token).ConfigureAwait(false);
    }

    private async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken callerToken)
    {
        EnsureOperational();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
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
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("会话窗口操作超过配置的超时边界。", exception);
        }
        finally
        {
            if (entered)
                _operationGate.Release();
        }
    }

    private async ValueTask CommitAsync(StreamingSessionWindowDocument document, CancellationToken token)
    {
        byte[] bytes = Encode(document, Options.MaxStateBytes);
        try
        {
            await CommitBytesAsync(bytes, overwrite: true, token).ConfigureAwait(false);
            _document = document;
        }
        catch (Exception exception)
        {
            _fault = exception;
            throw;
        }
    }

    private async ValueTask CommitBytesAsync(byte[] bytes, bool overwrite, CancellationToken token)
    {
        await EnsureOwnerAsync(token).ConfigureAwait(false);
        string pending = _path + ".pending";
        try
        {
            await using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(pending, _path, overwrite);
            Wal.DirectoryFsync.FlushRequired(Path.GetDirectoryName(_path)!);
        }
        finally
        {
            try { File.Delete(pending); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private async ValueTask EnsureOwnerAsync(CancellationToken token)
    {
        if (await ValidateOwnerAsync(_path, token).ConfigureAwait(false))
            return;
        if (File.Exists(_path + ".pending"))
            throw new InvalidDataException("会话窗口待提交文件所有权标记无效。");
        string owner = _path + ".pending.owner";
        await using var stream = new FileStream(owner, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128, FileOptions.Asynchronous);
        byte[] bytes = OwnerBytes(_path);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        stream.Flush(true);
        Wal.DirectoryFsync.FlushRequired(Path.GetDirectoryName(_path)!);
    }

    private static async ValueTask<bool> ValidateOwnerAsync(string path, CancellationToken token)
    {
        string owner = path + ".pending.owner";
        if (!File.Exists(owner))
            return false;
        byte[] expected = OwnerBytes(path);
        await using var stream = new FileStream(owner, FileMode.Open, FileAccess.Read, FileShare.Read, 128, FileOptions.Asynchronous);
        if (stream.Length != expected.Length)
            throw new InvalidDataException("会话窗口所有权标记长度无效。");
        var actual = new byte[expected.Length];
        await stream.ReadExactlyAsync(actual, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException("会话窗口所有权标记不匹配。");
        return true;
    }

    private static byte[] OwnerBytes(string path)
    {
        string identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Encoding.ASCII.GetBytes("SONNETDB-STREAM-SESSION-PENDING-V1\n" + digest + "\n");
    }

    private static async ValueTask<StreamingSessionWindowDocument> LoadAsync(string path, StreamingSessionWindowOptions options, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 or > int.MaxValue || stream.Length > options.MaxStateBytes)
            throw new InvalidDataException("会话窗口状态文件为空或超过字节边界。");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        StreamingSessionWindowEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(bytes, StreamingSessionWindowJsonContext.Default.StreamingSessionWindowEnvelope)
                ?? throw new InvalidDataException("会话窗口状态 JSON 为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("会话窗口状态 JSON 损坏。", exception);
        }
        if (envelope.State is null || envelope.Sha256 is null || envelope.Sha256.Length != 64)
            throw new InvalidDataException("会话窗口状态缺少 SHA-256。");
        byte[] state = JsonSerializer.SerializeToUtf8Bytes(envelope.State, StreamingSessionWindowJsonContext.Default.StreamingSessionWindowDocument);
        if (!string.Equals(envelope.Sha256, Convert.ToHexString(SHA256.HashData(state)), StringComparison.Ordinal))
            throw new InvalidDataException("会话窗口状态 SHA-256 不匹配。");
        ValidateDocument(envelope.State, options, token);
        return envelope.State;
    }

    private static byte[] Encode(StreamingSessionWindowDocument document, int maxBytes)
    {
        byte[] state = JsonSerializer.SerializeToUtf8Bytes(document, StreamingSessionWindowJsonContext.Default.StreamingSessionWindowDocument);
        string hash = Convert.ToHexString(SHA256.HashData(state));
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(new StreamingSessionWindowEnvelope(document, hash), StreamingSessionWindowJsonContext.Default.StreamingSessionWindowEnvelope);
        if (state.Length > maxBytes || encoded.Length > maxBytes)
            throw new InvalidDataException("会话窗口状态超过字节容量上限。");
        return encoded;
    }

    private static void ValidateDocument(StreamingSessionWindowDocument document, StreamingSessionWindowOptions expectedOptions, CancellationToken token)
    {
        if (document.FormatVersion != StreamingSessionWindowDefinition.CurrentFormatVersion || document.Definition is null || document.Options is null
            || document.Sessions is null || document.AppliedSequence < -1 || document.AppliedRevision < 0
            || document.DroppedLateEvents < 0 || document.WatermarkUtc.Offset != TimeSpan.Zero
            || document.Options != expectedOptions)
            throw new InvalidDataException("会话窗口状态元数据无效。");
        document.Definition.Validate();
        document.Options.Validate();
        if (document.Definition.FormatVersion != document.FormatVersion || document.Sessions.Count > document.Options.MaxSessions
            || (document.LastDeliveryId is null) != (document.LastBatchHash is null)
            || document.LastDeliveryId?.Length > 256
            || (document.LastBatchHash is not null && document.LastBatchHash.Length != 64))
            throw new InvalidDataException("会话窗口状态容量或重投身份无效。");
        if (document.LastBatchHash is not null
            && (document.AppliedSequence < 0 || document.AppliedRevision < 1 || string.IsNullOrWhiteSpace(document.LastDeliveryId)))
            throw new InvalidDataException("会话窗口已应用位点与重投身份不一致。");
        if (document.RetiredUntilUtc is { } retiredBoundary && retiredBoundary.Offset != TimeSpan.Zero)
            throw new InvalidDataException("会话窗口回收边界必须为 UTC。");
        long previousStart = long.MinValue;
        foreach (StreamingSessionMember member in document.Sessions)
        {
            token.ThrowIfCancellationRequested();
            if (member.StartTicks < DateTime.MinValue.Ticks || member.EndTicks < member.StartTicks
                || member.EndTicks > DateTime.MaxValue.Ticks || member.Count < 1 || member.StartTicks <= previousStart)
                throw new InvalidDataException("会话窗口边界或 COUNT 无效。");
            previousStart = member.StartTicks;
        }
    }

    private static StreamingSessionWindowState State(StreamingSessionWindowDocument document)
        => new(document.AppliedSequence, document.AppliedRevision, document.WatermarkUtc, document.DroppedLateEvents, document.Sessions.Count);

    private static int FindInsertion(List<StreamingSessionMember> sessions, long eventTicks)
    {
        int low = 0;
        int high = sessions.Count;
        int iterations = 0;
        while (low < high && iterations++ < 32)
        {
            int middle = low + ((high - low) / 2);
            if (sessions[middle].StartTicks < eventTicks)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private bool Matches(StreamingSessionMember member, long eventTicks)
    {
        long gap = checked(Definition.InactivityGapMilliseconds * TimeSpan.TicksPerMillisecond);
        if (eventTicks >= member.StartTicks && eventTicks <= member.EndTicks)
            return true;
        if (eventTicks > member.EndTicks)
            return eventTicks - member.EndTicks <= gap;
        return member.StartTicks - eventTicks <= gap;
    }

    private bool IsClosed(StreamingSessionMember member, DateTimeOffset watermark)
    {
        long gap = checked(Definition.InactivityGapMilliseconds * TimeSpan.TicksPerMillisecond);
        long lateness = checked(Definition.AllowedLatenessMilliseconds * TimeSpan.TicksPerMillisecond);
        long closeTicks = checked(member.EndTicks + gap + lateness);
        return watermark.UtcTicks >= closeTicks;
    }

    private void EnsureOperational()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this);
        if (_fault is not null)
            throw new InvalidOperationException("会话窗口持久化失败；必须关闭并重开以核对结果。", _fault);
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("会话窗口时间必须为 UTC。", parameterName);
    }

    private static string FullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static FileStream AcquireLease(string path)
        => new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
}
