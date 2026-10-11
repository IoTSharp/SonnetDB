using System.Text.Json;
using SonnetDB.Cdc;

namespace SonnetDB.Streaming;

public sealed partial class FileStreamingSubscription
{
    private const int CdcBridgeReceiptMaxBytes = 64 * 1024;
    private string CdcBridgeReceiptPath => Path.Combine(DirectoryPath, "cdc-bridge-receipt.json");

    internal Task AssertCdcBridgeUnboundAsync(CancellationToken token)
        => ExecuteAsync(operationToken =>
        {
            operationToken.ThrowIfCancellationRequested();
            if (File.Exists(CdcBridgeReceiptPath))
                throw new InvalidDataException("CDC 桥接状态缺失或目标已绑定；已有目标凭据禁止创建新交付基线。");
            return Task.FromResult(true);
        }, token);

    internal Task BindCdcBridgeAsync(
        string bindingSha256, long expectedTail, bool allowCreate, bool hasPending, CancellationToken token)
        => ExecuteAsync(async operationToken =>
        {
            CdcStreamingBridgeTargetReceipt? receipt = await ReadCdcBridgeReceiptAsync(operationToken).ConfigureAwait(false);
            if (receipt is null)
            {
                if (!allowCreate || _state.LastAcceptedSequence != expectedTail)
                    throw new InvalidDataException("CDC 目标凭据缺失，或目标尾部不符合桥接基线。");
                receipt = new(1, bindingSha256, expectedTail, null, null, null);
                await WriteCdcBridgeReceiptAsync(receipt, operationToken).ConfigureAwait(false);
            }

            ValidateCdcBridgeReceipt(receipt, bindingSha256);
            long tail = _state.LastAcceptedSequence;
            if (tail != receipt.LastCompletedSequence && tail != receipt.Intent?.Sequence)
                throw new InvalidDataException("CDC 目标尾部已被其它发布者推进，或发布凭据与目标不一致。");
            if (!hasPending && (receipt.Intent is not null || receipt.LastCompletedSequence != expectedTail))
                throw new InvalidDataException("CDC 目标仍有不属于当前桥接 outbox 的未知发布。");
            if (hasPending && (receipt.LastCompletedSequence < expectedTail
                || receipt.LastCompletedSequence > checked(expectedTail + 1)
                || (receipt.Intent is not null && receipt.Intent.PreviousSequence != expectedTail)))
            {
                throw new InvalidDataException("CDC 目标凭据不属于桥接的下一个 outbox 事件。");
            }

            return true;
        }, token);

    internal Task PublishCdcBridgeAsync(
        string bindingSha256, long expectedPreviousSequence, StreamingEvent value,
        CancellationToken token, Action<string>? onStep = null)
        => ExecuteAsync(async operationToken =>
        {
            ValidateEvent(value, Options);
            string eventHash = CdcStreamingBridgeFiles.Hash(value, CdcStreamingBridgeJsonContext.Default.StreamingEvent);
            if (value.Sequence != checked(expectedPreviousSequence + 1) || value.IsLate)
                throw new InvalidDataException("CDC 桥接目标序号必须连续，迟到标记由目标设置。");
            CdcStreamingBridgeTargetReceipt receipt = await ReadCdcBridgeReceiptAsync(operationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("CDC 目标凭据缺失，不能重建发布身份。");
            ValidateCdcBridgeReceipt(receipt, bindingSha256);

            // 完成凭据在目标 ACK 和事件帧回收后仍保留，因此可以安全完成源 ACK。
            if (receipt.LastCompletedSequence == value.Sequence)
            {
                if (receipt.Intent is not null || receipt.LastCompletedEventId != value.EventId
                    || receipt.LastCompletedEventSha256 != eventHash || _state.LastAcceptedSequence != value.Sequence)
                    throw new InvalidDataException("CDC 目标完成凭据与重试事件身份不匹配。");
                return true;
            }

            if (receipt.LastCompletedSequence != expectedPreviousSequence)
                throw new InvalidDataException("CDC 目标发布凭据的条件序号不匹配。");
            var intent = new CdcStreamingBridgeTargetIntent(expectedPreviousSequence, value.Sequence, value.EventId, eventHash);
            if (receipt.Intent is not null)
            {
                if (receipt.Intent != intent)
                    throw new InvalidDataException("CDC 目标有另一条尚未完成的发布意图。");
                if (_state.LastAcceptedSequence == value.Sequence)
                {
                    // 未完成凭据不能把序号当作事件身份证明；已回收时明确交给人工恢复。
                    if (_state.Checkpoint.CommittedSequence >= value.Sequence)
                        throw new InvalidDataException("CDC 发布结果未知且目标事件已回收；需要恢复交接，禁止重发或源 ACK。");
                    CdcEventSpoolBatch replay = await _spool.ReplayBatchAsync(
                        expectedPreviousSequence < 0 ? null : new CdcCheckpoint(0, expectedPreviousSequence),
                        maxEvents: 1, maxBytes: Options.MaxBatchBytes, cancellationToken: operationToken).ConfigureAwait(false);
                    if (replay.Events.Count != 1)
                        throw new InvalidDataException("CDC 未知发布缺少可验证的目标事件。");
                    StreamingEvent persisted = DecodeEvent(replay.Events[0]) with { IsLate = false };
                    if (persisted.Sequence != value.Sequence || persisted.EventId != value.EventId
                        || CdcStreamingBridgeFiles.Hash(persisted, CdcStreamingBridgeJsonContext.Default.StreamingEvent) != eventHash)
                        throw new InvalidDataException("CDC 未知发布的目标事件与 outbox 身份不匹配。");
                    await CompleteCdcBridgeReceiptAsync(receipt, intent, operationToken).ConfigureAwait(false);
                    return true;
                }
            }

            if (_state.LastAcceptedSequence != expectedPreviousSequence || _state.PublishingCompleted)
                throw new InvalidDataException("CDC 目标尾部条件不匹配，或目标已结束发布。");
            if (Definition.LateEventPolicy != StreamingLateEventPolicy.Deliver)
                throw new InvalidOperationException("CDC 桥接目标必须投递迟到事件，不能丢弃源变更。");
            bool isLate = IsLate(value.EventTimeUtc, _state.WatermarkUtc, Definition.AllowedLateness);
            CdcEvent envelope = EncodeEvent(Clone(value) with { IsLate = isLate });
            int encodedBytes = CdcEventCodec.Encode(envelope).Length;
            long frameBytes = CdcEventSpool.FrameHeaderSize + (long)encodedBytes;
            if (encodedBytes > Options.MaxBatchBytes || frameBytes > Options.MaxStoredBytes)
                throw new ArgumentException("CDC 目标单事件超过批次或磁盘容量边界。", nameof(value));
            if (_state.PendingEventCount >= Definition.Capacity || _spool.StoredBytes > Options.MaxStoredBytes - frameBytes)
                throw new InvalidOperationException("CDC 目标容量已满；保留 outbox，消费回收后重开桥接再推进。");

            try
            {
                if (receipt.Intent is null)
                {
                    receipt = receipt with { Intent = intent };
                    await WriteCdcBridgeReceiptAsync(receipt, operationToken).ConfigureAwait(false);
                }

                onStep?.Invoke("TargetIntentSaved");
                await _spool.AppendAsync(envelope, operationToken).ConfigureAwait(false);
                await PersistAsync(_state with
                {
                    LastAcceptedSequence = value.Sequence,
                    PendingEventCount = _state.PendingEventCount + 1,
                }, operationToken).ConfigureAwait(false);
                onStep?.Invoke("TargetAccepted");
                await CompleteCdcBridgeReceiptAsync(receipt, intent, operationToken).ConfigureAwait(false);
                onStep?.Invoke("TargetReceiptCompleted");
                Pulse();
                return true;
            }
            catch (Exception exception)
            {
                Fault(exception);
                throw;
            }
        }, token);

    internal Task VerifyCdcBridgeCompletedAsync(string bindingSha256, StreamingEvent value, CancellationToken token)
        => ExecuteAsync(async operationToken =>
        {
            CdcStreamingBridgeTargetReceipt receipt = await ReadCdcBridgeReceiptAsync(operationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("CDC 目标完成凭据缺失。");
            ValidateCdcBridgeReceipt(receipt, bindingSha256);
            if (receipt.Intent is not null || receipt.LastCompletedSequence != value.Sequence
                || receipt.LastCompletedEventId != value.EventId || _state.LastAcceptedSequence != value.Sequence
                || receipt.LastCompletedEventSha256 != CdcStreamingBridgeFiles.Hash(value,
                    CdcStreamingBridgeJsonContext.Default.StreamingEvent))
                throw new InvalidDataException("CDC 源确认前目标完成凭据与 outbox 最后一条事件不匹配。");
            return true;
        }, token);

    private Task CompleteCdcBridgeReceiptAsync(
        CdcStreamingBridgeTargetReceipt receipt, CdcStreamingBridgeTargetIntent intent, CancellationToken token)
        => WriteCdcBridgeReceiptAsync(receipt with
        {
            LastCompletedSequence = intent.Sequence,
            LastCompletedEventId = intent.EventId,
            LastCompletedEventSha256 = intent.EventSha256,
            Intent = null,
        }, token);

    private async Task<CdcStreamingBridgeTargetReceipt?> ReadCdcBridgeReceiptAsync(CancellationToken token)
    {
        if (!File.Exists(CdcBridgeReceiptPath))
            return null;
        byte[] bytes = await CdcStreamingBridgeFiles.ReadAsync(CdcBridgeReceiptPath, CdcBridgeReceiptMaxBytes, token)
            .ConfigureAwait(false);
        try
        {
            CdcStreamingBridgeTargetEnvelope envelope = JsonSerializer.Deserialize(bytes,
                CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeTargetEnvelope)
                ?? throw new InvalidDataException("CDC 目标凭据 JSON 为空。");
            if (envelope.Receipt is null || envelope.Sha256 != CdcStreamingBridgeFiles.Hash(envelope.Receipt,
                CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeTargetReceipt))
                throw new InvalidDataException("CDC 目标凭据 SHA-256 校验失败。");
            return envelope.Receipt;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("CDC 目标凭据 JSON 无效。", exception);
        }
    }

    private Task WriteCdcBridgeReceiptAsync(CdcStreamingBridgeTargetReceipt receipt, CancellationToken token)
    {
        var envelope = new CdcStreamingBridgeTargetEnvelope(receipt, CdcStreamingBridgeFiles.Hash(receipt,
            CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeTargetReceipt));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope,
            CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeTargetEnvelope);
        return CdcStreamingBridgeFiles.WriteAsync(CdcBridgeReceiptPath, bytes, CdcBridgeReceiptMaxBytes, token);
    }

    private static void ValidateCdcBridgeReceipt(CdcStreamingBridgeTargetReceipt receipt, string bindingSha256)
    {
        if (receipt.FormatVersion != 1 || receipt.BindingSha256 != bindingSha256
            || receipt.LastCompletedSequence < -1
            || (receipt.LastCompletedEventId is null) != (receipt.LastCompletedEventSha256 is null)
            || (receipt.LastCompletedEventId is not null && (string.IsNullOrWhiteSpace(receipt.LastCompletedEventId)
                || receipt.LastCompletedEventSha256?.Length != 64))
            || (receipt.Intent is { } intent && (intent.PreviousSequence != receipt.LastCompletedSequence
                || intent.PreviousSequence == long.MaxValue || intent.Sequence != intent.PreviousSequence + 1
                || string.IsNullOrWhiteSpace(intent.EventId) || intent.EventSha256?.Length != 64)))
            throw new InvalidDataException("CDC 目标凭据版本、绑定或发布序号结构无效。");
    }
}
