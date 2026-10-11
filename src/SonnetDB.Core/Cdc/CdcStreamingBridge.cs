using System.Text.Json;
using SonnetDB.Streaming;

namespace SonnetDB.Cdc;

/// <summary>通过持久 outbox 和发布身份凭据，将专用 CDC spool 有界交付到本地持久订阅。</summary>
/// <remarks>
/// <para>一次只处理一批。先保存原 CDC 字节和目标序号，目标完成发布凭据后才确认源。</para>
/// <para>源 spool 只供本桥接确认，目标发布端也只供本桥接使用。打开或恢复桥接应先于启动目标消费者。</para>
/// <para>不同目录间没有原子事务，也不提供业务副作用 exactly-once。未知发布且目标帧已回收时拒绝恢复，必须人工交接。</para>
/// <para>容量、取消或存储失败可能留下恢复 outbox；调用方应关闭并重开。本类型不拥有源或目标句柄。</para>
/// </remarks>
public sealed class CdcStreamingBridge : IAsyncDisposable
{
    private readonly CdcEventSpool _source;
    private readonly FileStreamingSubscription _target;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly string _bindingSha256;
    private readonly Action<string>? _onStep;
    private CdcStreamingBridgeFileState _state;
    private Exception? _fault;
    private int _disposed;

    private CdcStreamingBridge(
        string statePath, CdcEventSpool source, FileStreamingSubscription target, FileStream lease,
        CdcStreamingBridgeFileState state, Action<string>? onStep)
    {
        StatePath = statePath;
        _source = source;
        _target = target;
        _lease = lease;
        _state = state;
        _onStep = onStep;
        _bindingSha256 = CdcStreamingBridgeFiles.Hash(state.Identity,
            CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeIdentity);
    }

    /// <summary>桥接状态和 outbox 的绝对文件路径。</summary>
    public string StatePath { get; }

    /// <summary>已持久保存的桥接绑定。</summary>
    public CdcStreamingBridgeBinding Binding => _state.Identity.Binding;

    /// <summary>已持久保存的一批容量和超时边界。</summary>
    public CdcStreamingBridgeOptions Options => _state.Identity.Options;

    internal CdcEventSpool SourceSpool => _source;

    internal FileStreamingSubscription TargetSubscription => _target;

    /// <summary>创建或重开桥接，并完成已有 outbox 的恢复；不会读取新的源批次。</summary>
    /// <param name="statePath">专属于本桥接的状态文件路径。</param>
    /// <param name="source">只由本桥接确认的单分区 CDC spool。</param>
    /// <param name="target">只由本桥接发布的本地持久订阅；恢复完成后再启动消费者。</param>
    /// <param name="binding">源事件 schema、实体和分区绑定。</param>
    /// <param name="options">一批容量和超时；重开时须与保存值完全相同。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>持有单执行者 lease 且已恢复已有 outbox 的桥接。</returns>
    public static ValueTask<CdcStreamingBridge> OpenAsync(
        string statePath, CdcEventSpool source, FileStreamingSubscription target,
        CdcStreamingBridgeBinding binding, CdcStreamingBridgeOptions? options = null,
        CancellationToken cancellationToken = default)
        => OpenCoreAsync(statePath, source, target, binding, options, cancellationToken, onStep: null);

    internal static async ValueTask<CdcStreamingBridge> OpenCoreAsync(
        string statePath, CdcEventSpool source, FileStreamingSubscription target,
        CdcStreamingBridgeBinding binding, CdcStreamingBridgeOptions? options,
        CancellationToken cancellationToken, Action<string>? onStep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        options ??= new();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Definition.LateEventPolicy != StreamingLateEventPolicy.Deliver)
            throw new ArgumentException("CDC 桥接目标必须保留迟到事件。", nameof(target));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.OperationTimeoutMilliseconds);
        CancellationToken token = deadline.Token;
        string path = Path.GetFullPath(statePath);
        string directory = Path.GetDirectoryName(path)!;
        ValidateStoragePaths(path, source, target, token);
        Directory.CreateDirectory(directory);
        bool existingState = File.Exists(path);
        string leasePath = path + ".lock";
        if (!existingState && File.Exists(leasePath))
            throw new InvalidDataException("CDC 桥接状态缺失但初创 lease 标记仍在；禁止重新初始化交付基线。");
        var lease = new FileStream(leasePath, existingState ? FileMode.OpenOrCreate : FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
        CdcStreamingBridge? bridge = null;
        try
        {
            var identity = new CdcStreamingBridgeIdentity(binding, path, source.FilePath, target.DirectoryPath,
                target.Definition, target.Options, options);
            CdcStreamingBridgeFileState? state = await ReadStateAsync(path, options, token).ConfigureAwait(false);
            if (state is null)
            {
                if (existingState)
                    throw new InvalidDataException("CDC 桥接状态在打开期间消失，禁止重置交付基线。");
                ValidateSourcePartition(source, binding.Partition);
                await target.AssertCdcBridgeUnboundAsync(token).ConfigureAwait(false);
                state = new(1, identity, 0, SourceAcknowledgedOffset(source, binding.Partition), target.LastAcceptedSequence, null);
                await WriteStateAsync(path, state, token).ConfigureAwait(false);
            }
            else
            {
                ValidateState(state, token);
                if (state.Identity != identity)
                    throw new InvalidDataException("CDC 桥接的 schema、源路径、目标定义或容量绑定与保存状态不匹配。");
            }

            bridge = new(path, source, target, lease, state, onStep);
            SemaphoreSlim sourceGate = CdcLocalPipelineGate.For(source);
            await sourceGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                bridge.ValidateSourceBoundary();
                long expectedTail = state.Outbox is { } outbox
                    ? checked(outbox.BaseTargetSequence + outbox.PublishedCount) : state.LastTargetSequence;
                await target.BindCdcBridgeAsync(bridge._bindingSha256, expectedTail,
                    allowCreate: state.Revision == 0 && state.Outbox is null,
                    hasPending: state.Outbox is not null, token).ConfigureAwait(false);
                if (state.Outbox is not null)
                    await bridge.ResumeOutboxAsync(token).ConfigureAwait(false);
            }
            finally
            {
                sourceGate.Release();
            }

            return bridge;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            lease.Dispose();
            throw new TimeoutException("CDC 桥接打开或恢复超过总操作时间边界。", exception);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>读取桥接状态的一致快照。</summary>
    /// <returns>已完成的位点、目标序号和 outbox 进度。</returns>
    public CdcStreamingBridgeState GetState()
    {
        CdcStreamingBridgeFileState state = Volatile.Read(ref _state);
        return new(state.Revision, state.CommittedSourceOffset, state.LastTargetSequence,
            state.Outbox?.Events.Length ?? 0, state.Outbox?.PublishedCount ?? 0);
    }

    /// <summary>以桥接 revision 条件推进一批；容量不足时立即保留 outbox 并拒绝，不等待消费者。</summary>
    /// <param name="expectedRevision">调用方最近观察到的桥接状态修订号。</param>
    /// <param name="cancellationToken">取消令牌；另受总操作超时约束。</param>
    /// <returns>本批发布数量、两端位点及是否还有后续源事件。</returns>
    public async ValueTask<CdcStreamingBridgeResult> PumpOnceAsync(
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Options.OperationTimeoutMilliseconds);
        CancellationToken token = deadline.Token;
        bool entered = false;
        try
        {
            await _operationGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_fault is not null)
                throw new InvalidOperationException("CDC 桥接操作失败，必须关闭并重开以恢复 outbox。", _fault);
            if (_state.Revision != expectedRevision)
                throw new InvalidOperationException("CDC 桥接 revision 条件不匹配。");
            SemaphoreSlim sourceGate = CdcLocalPipelineGate.For(_source);
            await sourceGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ValidateSourceBoundary();
                await _target.BindCdcBridgeAsync(_bindingSha256, _state.LastTargetSequence,
                    allowCreate: false, hasPending: _state.Outbox is not null, token).ConfigureAwait(false);
                if (_state.Outbox is null && _source.EventCount == 0)
                    return new(0, _state.CommittedSourceOffset, _state.LastTargetSequence, false);
                if (_state.Outbox is null)
                {
                    CdcEventSpoolBatch batch = await _source.ReplayBatchAsync(
                        maxEvents: (int)Math.Min(Options.MaxBatchEvents, _source.EventCount),
                        maxBytes: Math.Min(Options.MaxBatchBytes, _source.StoredBytes),
                        cancellationToken: token).ConfigureAwait(false);
                    if (batch.Events.Count == 0)
                        throw new InvalidOperationException("CDC 批次字节预算无法容纳下一条源事件。");
                    var events = new CdcStreamingBridgeOutboxEvent[batch.Events.Count];
                    for (int index = 0; index < events.Length; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        CdcEvent value = batch.Events[index];
                        ValidateSourceEvent(value, Binding);
                        var mapped = new StreamingEvent(value.EventId, checked(_state.LastTargetSequence + index + 1),
                            value.OccurredAtUtc, CdcEventCodec.Encode(value));
                        FileStreamingSubscription.ValidateEvent(mapped, _target.Options);
                        events[index] = new(value.Metadata.Checkpoint.Offset, mapped);
                    }

                    await PersistAsync(_state with
                    {
                        Outbox = new(_state.CommittedSourceOffset, _state.LastTargetSequence, events, 0),
                    }, token).ConfigureAwait(false);
                    _onStep?.Invoke("OutboxCreated");
                }

                int published = _state.Outbox!.Events.Length;
                await ResumeOutboxAsync(token).ConfigureAwait(false);
                return new(published, _state.CommittedSourceOffset, _state.LastTargetSequence, _source.EventCount != 0);
            }
            catch (Exception exception)
            {
                _fault = exception;
                throw;
            }
            finally
            {
                sourceGate.Release();
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("CDC 桥接推进超过总操作时间边界。", exception);
        }
        finally
        {
            if (entered)
                _operationGate.Release();
        }
    }

    /// <summary>有界关闭桥接 lease；保留 outbox 和两端数据，不关闭源或目标。</summary>
    /// <returns>关闭操作。</returns>
    public async ValueTask DisposeAsync()
    {
        if (!await _operationGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            throw new TimeoutException("CDC 桥接关闭等待超过五秒。");
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _lease.Dispose();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ResumeOutboxAsync(CancellationToken token)
    {
        CdcStreamingBridgeOutbox outbox = _state.Outbox
            ?? throw new InvalidOperationException("CDC 桥接不存在恢复 outbox。");
        long sourceAcknowledged = SourceAcknowledgedOffset(_source, Binding.Partition);
        if (sourceAcknowledged == outbox.BaseSourceOffset)
        {
            CdcEventSpoolBatch replay = await _source.ReplayBatchAsync(
                maxEvents: outbox.Events.Length,
                maxBytes: Math.Min(Options.MaxBatchBytes, _source.StoredBytes),
                cancellationToken: token).ConfigureAwait(false);
            if (replay.Events.Count != outbox.Events.Length)
                throw new InvalidDataException("CDC outbox 对应的未确认源事件已缺失。");
            for (int index = 0; index < outbox.Events.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (replay.Events[index].Metadata.Checkpoint.Offset != outbox.Events[index].SourceOffset
                    || !CdcEventCodec.Encode(replay.Events[index]).AsSpan().SequenceEqual(outbox.Events[index].Event.Payload))
                    throw new InvalidDataException("CDC outbox 原始正文与源 spool 事件不匹配。");
            }
        }

        for (int index = outbox.PublishedCount; index < outbox.Events.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            await _target.PublishCdcBridgeAsync(_bindingSha256, checked(outbox.BaseTargetSequence + index),
                outbox.Events[index].Event, token, _onStep).ConfigureAwait(false);
            outbox = outbox with { PublishedCount = index + 1 };
            await PersistAsync(_state with { Outbox = outbox }, token).ConfigureAwait(false);
            _onStep?.Invoke("OutboxProgressSaved");
        }

        long finalOffset = outbox.Events[^1].SourceOffset;
        await _target.VerifyCdcBridgeCompletedAsync(_bindingSha256, outbox.Events[^1].Event, token).ConfigureAwait(false);
        ValidateSourceBoundary();
        if (SourceAcknowledgedOffset(_source, Binding.Partition) < finalOffset)
            await _source.AcknowledgeAsync(new(Binding.Partition, finalOffset), token).ConfigureAwait(false);
        _onStep?.Invoke("SourceAcknowledged");
        await PersistAsync(_state with
        {
            CommittedSourceOffset = finalOffset,
            LastTargetSequence = outbox.Events[^1].Event.Sequence,
            Outbox = null,
        }, token).ConfigureAwait(false);
        _onStep?.Invoke("BridgeCommitted");
    }

    private void ValidateSourceBoundary()
    {
        ValidateSourcePartition(_source, Binding.Partition);
        long acknowledged = SourceAcknowledgedOffset(_source, Binding.Partition);
        CdcStreamingBridgeOutbox? outbox = _state.Outbox;
        if (acknowledged != _state.CommittedSourceOffset
            && (outbox is null || outbox.PublishedCount != outbox.Events.Length
                || acknowledged != outbox.Events[^1].SourceOffset))
            throw new InvalidDataException("CDC 源 ACK 不符合桥接基线；专用 spool 被其它消费者确认或倒退。");
        if (_state.CommittedSourceOffset >= 0
            && _source.PartitionHighWatermarks.GetValueOrDefault(Binding.Partition, -1) < _state.CommittedSourceOffset)
            throw new InvalidDataException("CDC 源高水位低于桥接已交付位点。");
    }

    private static long SourceAcknowledgedOffset(CdcEventSpool source, long partition)
        => source.AcknowledgedCheckpoints.GetValueOrDefault(partition, -1);

    private static void ValidateSourcePartition(CdcEventSpool source, long partition)
    {
        if (source.PartitionHighWatermarks.Keys.Any(value => value != partition)
            || source.AcknowledgedCheckpoints.Keys.Any(value => value != partition))
            throw new InvalidDataException("CDC 桥接只能消费专用单分区 spool。");
    }

    private static void ValidateSourceEvent(CdcEvent value, CdcStreamingBridgeBinding binding)
    {
        if (value.Source != binding.Source || value.Entity != binding.Entity
            || value.Metadata.Schema != binding.Schema || value.Metadata.SchemaVersion != binding.SchemaVersion
            || value.Metadata.Checkpoint.Partition != binding.Partition)
            throw new InvalidDataException("CDC 源事件的 source、entity、schema 或 partition 与桥接绑定不一致。");
    }

    private static void ValidateStoragePaths(
        string statePath, CdcEventSpool source, FileStreamingSubscription target, CancellationToken token)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string targetPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.DirectoryPath));
        string targetPrefix = targetPath + Path.DirectorySeparatorChar;
        string[] sourcePaths = [Path.GetFullPath(source.FilePath), Path.GetFullPath(source.CheckpointPath), Path.GetFullPath(source.LeasePath)];
        string[] bridgePaths = [statePath, statePath + ".lock"];
        foreach (string candidate in bridgePaths)
        {
            token.ThrowIfCancellationRequested();
            if (sourcePaths.Any(protectedPath => string.Equals(candidate, protectedPath, comparison))
                || string.Equals(candidate, targetPath, comparison) || candidate.StartsWith(targetPrefix, comparison))
                throw new ArgumentException("CDC 桥接状态及 lease 必须独立于源文件与目标订阅目录。", nameof(statePath));
        }

        foreach (string sourcePath in sourcePaths)
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(sourcePath, targetPath, comparison) || sourcePath.StartsWith(targetPrefix, comparison))
                throw new ArgumentException("CDC 源文件及 sidecar 不得位于目标订阅目录。", nameof(source));
        }

        // 仅核对三个明确命名路径的父链，不搜索目录内容；最多 64 层并受打开总超时/取消约束。
        foreach (string candidate in new[] { statePath, source.FilePath, targetPath })
        {
            string? ancestor = Path.GetFullPath(candidate);
            for (int depth = 0; depth < 64 && ancestor is not null; depth++)
            {
                token.ThrowIfCancellationRequested();
                if ((File.Exists(ancestor) || Directory.Exists(ancestor))
                    && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("CDC 桥接存储路径不得包含链接或 reparse 别名。", nameof(statePath));
                ancestor = Path.GetDirectoryName(ancestor);
            }

            if (ancestor is not null)
                throw new ArgumentException("CDC 桥接路径父链超过 64 层边界。", nameof(statePath));
        }
    }

    private async Task PersistAsync(CdcStreamingBridgeFileState next, CancellationToken token)
    {
        next = next with { Revision = checked(_state.Revision + 1) };
        await WriteStateAsync(StatePath, next, token).ConfigureAwait(false);
        Volatile.Write(ref _state, next);
    }

    private static async Task<CdcStreamingBridgeFileState?> ReadStateAsync(
        string path, CdcStreamingBridgeOptions options, CancellationToken token)
    {
        if (!File.Exists(path))
            return null;
        byte[] bytes = await CdcStreamingBridgeFiles.ReadAsync(path, options.MaxStateBytes, token).ConfigureAwait(false);
        try
        {
            CdcStreamingBridgeStateEnvelope envelope = JsonSerializer.Deserialize(bytes,
                CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeStateEnvelope)
                ?? throw new InvalidDataException("CDC 桥接状态 JSON 为空。");
            if (envelope.State is null || envelope.Sha256 != CdcStreamingBridgeFiles.Hash(envelope.State,
                CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeFileState))
                throw new InvalidDataException("CDC 桥接状态 SHA-256 校验失败。");
            return envelope.State;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("CDC 桥接状态 JSON 无效。", exception);
        }
    }

    private static Task WriteStateAsync(string path, CdcStreamingBridgeFileState state, CancellationToken token)
    {
        ValidateState(state, token);
        var envelope = new CdcStreamingBridgeStateEnvelope(state, CdcStreamingBridgeFiles.Hash(state,
            CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeFileState));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope,
            CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeStateEnvelope);
        return CdcStreamingBridgeFiles.WriteAsync(path, bytes, state.Identity.Options.MaxStateBytes, token);
    }

    private static void ValidateState(CdcStreamingBridgeFileState state, CancellationToken token)
    {
        try
        {
            if (state.FormatVersion != 1 || state.Identity is null || state.Identity.Binding is null
                || state.Identity.Options is null || state.Identity.TargetDefinition is null || state.Identity.TargetOptions is null
                || state.Revision < 0 || state.CommittedSourceOffset < -1 || state.LastTargetSequence < -1
                || !Path.IsPathFullyQualified(state.Identity.BridgeStatePath)
                || !Path.IsPathFullyQualified(state.Identity.SourcePath) || !Path.IsPathFullyQualified(state.Identity.TargetPath))
                throw new InvalidDataException("CDC 桥接状态的版本、路径或位点结构无效。");
            state.Identity.Binding.Validate();
            state.Identity.Options.Validate();
            state.Identity.TargetDefinition.Validate();
            state.Identity.TargetOptions.Validate();
            if (state.Outbox is not { } outbox)
                return;
            if (outbox.Events is null || outbox.Events.Length < 1 || outbox.Events.Length > state.Identity.Options.MaxBatchEvents
                || outbox.PublishedCount < 0 || outbox.PublishedCount > outbox.Events.Length
                || outbox.BaseSourceOffset != state.CommittedSourceOffset || outbox.BaseTargetSequence != state.LastTargetSequence)
                throw new InvalidDataException("CDC 桥接 outbox 的基线、容量或进度无效。");
            long previousOffset = outbox.BaseSourceOffset;
            long bytes = 0;
            for (int index = 0; index < outbox.Events.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                CdcStreamingBridgeOutboxEvent item = outbox.Events[index];
                if (item is null || item.Event is null || item.SourceOffset <= previousOffset
                    || item.Event.Sequence != checked(outbox.BaseTargetSequence + index + 1) || item.Event.IsLate
                    || item.Event.Headers is not null)
                    throw new InvalidDataException("CDC 桥接 outbox 的事件映射无效。");
                FileStreamingSubscription.ValidateEvent(item.Event, state.Identity.TargetOptions);
                CdcEvent decoded = CdcEventCodec.Decode(item.Event.Payload);
                ValidateSourceEvent(decoded, state.Identity.Binding);
                if (decoded.EventId != item.Event.EventId || decoded.Metadata.Checkpoint.Offset != item.SourceOffset
                    || decoded.OccurredAtUtc != item.Event.EventTimeUtc)
                    throw new InvalidDataException("CDC 桥接 outbox 外层映射与原 CDC 正文不一致。");
                bytes = checked(bytes + item.Event.Payload.Length);
                if (bytes > state.Identity.Options.MaxBatchBytes)
                    throw new InvalidDataException("CDC 桥接 outbox 超过正文容量边界。");
                previousOffset = item.SourceOffset;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("CDC 桥接状态不能按固定绑定安全恢复。", exception);
        }
    }
}
