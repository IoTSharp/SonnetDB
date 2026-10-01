using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Cdc;
using SonnetDB.Exceptions;

namespace SonnetDB.Streaming;

internal sealed class FileStreamingDeadLetterStore
{
    private const int FormatVersion = 1;
    private const int MaximumFileBytes = 128 * 1024 * 1024;
    internal const int MaxReasonBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _directory;

    private FileStreamingDeadLetterStore(string directory, FileStreamingDeadLetterState state, long storedBytes)
    {
        _directory = directory;
        State = state;
        StoredBytes = storedBytes;
    }

    internal FileStreamingDeadLetterState State { get; private set; }

    internal long StoredBytes { get; private set; }

    internal FileStreamingDeadLetterRecord? Pending
        => State.PendingDeliveryId is null ? null : State.Records[^1];

    internal static async Task<FileStreamingDeadLetterStore> OpenAsync(
        string subscriptionDirectory,
        StreamingSubscriptionDefinition definition,
        FileStreamingSubscriptionOptions subscriptionOptions,
        FileStreamingDeadLetterOptions? requestedOptions,
        CancellationToken token)
    {
        string directory = Path.Combine(subscriptionDirectory, "dead-letters");
        string path = Path.Combine(directory, "state.json");
        if (!File.Exists(path))
        {
            if (Directory.Exists(directory))
                throw new InvalidDataException("死信目录已存在但持久状态缺失，不能重置死信数据。");
            var initial = new FileStreamingDeadLetterState(
                FormatVersion, definition.SubscriptionId, definition.StreamName,
                requestedOptions ?? new FileStreamingDeadLetterOptions(), [], null);
            ValidateState(initial, definition, subscriptionOptions, token);
            // 在专属临时目录内完成空 journal，再原子发布目录；失败不会留下可被误认为丢失记录的空正式目录。
            string temporaryDirectory = Path.Combine(subscriptionDirectory, ".dead-letters-create-" + Guid.NewGuid().ToString("N"));
            var created = new FileStreamingDeadLetterStore(temporaryDirectory, initial, 0);
            try
            {
                Directory.CreateDirectory(temporaryDirectory);
                await created.CommitAsync(initial, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Directory.Move(temporaryDirectory, directory);
                Wal.DirectoryFsync.FlushRequired(subscriptionDirectory);
                return new FileStreamingDeadLetterStore(directory, initial, created.StoredBytes);
            }
            finally
            {
                // 仅回收本次 Guid 命名的临时初始化目录；发布成功后该路径已经不存在。
                if (Directory.Exists(temporaryDirectory))
                {
                    try
                    {
                        File.Delete(Path.Combine(temporaryDirectory, "state.json"));
                        Directory.Delete(temporaryDirectory);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // 清理失败不能替换初始化结果未知或取消的原始异常。
                    }
                }
            }
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < 1 || stream.Length > MaximumFileBytes)
                throw new InvalidDataException("持久死信状态文件长度越界。");
            byte[] bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                throw new InvalidDataException("持久死信状态文件在读取时发生变化。");
            ValidateUniqueProperties(bytes, token);
            FileStreamingDeadLetterEnvelope envelope = JsonSerializer.Deserialize(
                bytes, FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterEnvelope)
                ?? throw new InvalidDataException("持久死信状态为空。");
            if (envelope.State is null || envelope.Sha256 is null)
                throw new InvalidDataException("持久死信状态缺少校验字段。");
            byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(
                envelope.State, FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterState);
            if (!string.Equals(envelope.Sha256, Convert.ToHexString(SHA256.HashData(stateBytes)), StringComparison.Ordinal))
                throw new InvalidDataException("持久死信状态 SHA-256 校验失败。");
            ValidateState(envelope.State, definition, subscriptionOptions, token);
            if (bytes.Length > envelope.State.Options.MaxStoredBytes
                || (requestedOptions is not null && envelope.State.Options != requestedOptions))
            {
                throw new InvalidDataException("持久死信容量或已保存配置不匹配。");
            }

            return new FileStreamingDeadLetterStore(directory, envelope.State, bytes.Length);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or EndOfStreamException or OverflowException)
        {
            throw new InvalidDataException("持久死信状态损坏或必需字段无效。", exception);
        }
    }

    internal FileStreamingDeadLetterState Prepare(FileStreamingDeadLetterRecord record)
    {
        if (State.PendingDeliveryId is not null)
            throw new InvalidOperationException("死信隔离意图尚未恢复完成，必须重开订阅。");
        if (State.Records.Length >= State.Options.MaxBatches)
            throw new FileStreamingDeadLetterCapacityException();
        var proposed = State with
        {
            Records = [.. State.Records, record],
            PendingDeliveryId = record.DeliveryId,
        };
        // 容量检查在任何写入前完成；清除 pending 字段的最终状态只会更小。
        _ = Encode(proposed);
        return proposed;
    }

    internal async Task CommitAsync(FileStreamingDeadLetterState state, CancellationToken token)
    {
        byte[] bytes = Encode(state);
        string path = Path.Combine(_directory, "state.json");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            token.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            Wal.DirectoryFsync.FlushRequired(_directory);
            State = state;
            StoredBytes = bytes.Length;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 本次提交生成的临时文件清理失败不能覆盖原始提交或取消异常。
            }
        }
    }

    private static byte[] Encode(FileStreamingDeadLetterState state)
    {
        byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(
            state, FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterState);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new FileStreamingDeadLetterEnvelope(state, Convert.ToHexString(SHA256.HashData(stateBytes))),
            FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterEnvelope);
        if (bytes.LongLength > state.Options.MaxStoredBytes)
            throw new FileStreamingDeadLetterCapacityException();
        return bytes;
    }

    internal static void ValidateReason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > MaxReasonBytes || StrictUtf8.GetByteCount(reason) > MaxReasonBytes)
            throw new ArgumentException("死信原因超过 4096 UTF-8 字节上限。", nameof(reason));
    }

    private static void ValidateState(
        FileStreamingDeadLetterState state,
        StreamingSubscriptionDefinition definition,
        FileStreamingSubscriptionOptions subscriptionOptions,
        CancellationToken token)
    {
        if (state.FormatVersion != FormatVersion || state.SubscriptionId != definition.SubscriptionId
            || state.StreamName != definition.StreamName || state.Options is null || state.Records is null)
        {
            throw new InvalidDataException("死信状态的版本、订阅身份或必需字段无效。");
        }

        state.Options.Validate();
        if (state.Records.Length > state.Options.MaxBatches)
            throw new InvalidDataException("持久死信批次数超过配置上限。");
        var deliveryIds = new HashSet<string>(StringComparer.Ordinal);
        long lastEventSequence = -1;
        long lastCheckpointRevision = -1;
        long lastStateRevision = -1;
        for (int index = 0; index < state.Records.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            FileStreamingDeadLetterRecord record = state.Records[index]
                ?? throw new InvalidDataException("持久死信批次为空。");
            if (record.Sequence != index + 1L || !Guid.TryParseExact(record.DeliveryId, "N", out _)
                || !deliveryIds.Add(record.DeliveryId) || record.Attempt != subscriptionOptions.MaxDeliveryAttempts
                || record.CreatedAtUtc.Offset != TimeSpan.Zero || record.ExpectedStateRevision < 0
                || record.ExpectedStateRevision <= lastStateRevision
                || record.PreviousCheckpoint is null || record.Checkpoint is null || record.Events is null
                || record.Events.Length < 1 || record.Events.Length > definition.BatchSize
                || record.Events[^1] is null)
            {
                throw new InvalidDataException("死信批次身份、投递次数或事件边界无效。");
            }

            ValidateReason(record.Reason);
            record.PreviousCheckpoint.Validate();
            record.Checkpoint.Validate();
            if (record.PreviousCheckpoint.SubscriptionId != definition.SubscriptionId
                || record.Checkpoint != record.PreviousCheckpoint.Advance(
                    record.Events[^1].Sequence, record.Checkpoint.WatermarkUtc)
                || record.PreviousCheckpoint.CommittedSequence < lastEventSequence
                || record.PreviousCheckpoint.Revision < lastCheckpointRevision)
            {
                throw new InvalidDataException("死信批次的前后检查点或保存顺序无效。");
            }

            long previousSequence = record.PreviousCheckpoint.CommittedSequence;
            long batchBytes = 0;
            foreach (FileStreamingDeadLetterEvent value in record.Events)
            {
                token.ThrowIfCancellationRequested();
                if (value is null)
                    throw new InvalidDataException("持久死信事件为空。");
                StreamingEvent original = value.ToEvent();
                FileStreamingSubscription.ValidateEvent(original, subscriptionOptions);
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(original, FileStreamingJsonContext.Default.StreamingEvent);
                if (payload.Length > subscriptionOptions.MaxEventBytes || value.Sequence <= previousSequence)
                    throw new InvalidDataException("持久死信事件序号或 JSON 字节边界无效。");
                var wrapper = new CdcEvent(value.EventId, state.SubscriptionId, state.StreamName, value.EventId,
                    value.Sequence, value.EventTimeUtc,
                    new CdcEventMetadata(CdcEventCodec.CurrentContractVersion, "sonnetdb.streaming-event",
                        CdcEventCodec.CurrentSchemaVersion, CdcOperation.Insert, new CdcCheckpoint(0, value.Sequence)),
                    null, StrictUtf8.GetString(payload));
                batchBytes = checked(batchBytes + CdcEventCodec.Encode(wrapper).Length);
                if (batchBytes > subscriptionOptions.MaxBatchBytes)
                    throw new InvalidDataException("持久死信批次超过原订阅的批次字节上限。");
                previousSequence = value.Sequence;
            }

            lastEventSequence = previousSequence;
            lastCheckpointRevision = record.Checkpoint.Revision;
            lastStateRevision = record.ExpectedStateRevision;
        }

        if (state.PendingDeliveryId is not null
            && (state.Records.Length == 0 || state.Records[^1].DeliveryId != state.PendingDeliveryId))
        {
            throw new InvalidDataException("死信隔离意图不是目录中的最后一个批次。");
        }
    }

    private static void ValidateUniqueProperties(byte[] bytes, CancellationToken token)
    {
        var reader = new Utf8JsonReader(bytes);
        var objects = new Stack<(HashSet<string> Names, bool IsCheckpoint)>();
        string? propertyName = null;
        // Utf8JsonReader 每次 Read 必须消耗输入，令牌数最多为文件字节数；操作令牌提供墙钟超时。
        for (int count = 0; count <= bytes.Length && reader.Read(); count++)
        {
            if (count % 1024 == 0)
                token.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.StartObject)
                objects.Push((new HashSet<string>(StringComparer.Ordinal), propertyName is "checkpoint" or "previousCheckpoint"));
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                (HashSet<string> names, bool isCheckpoint) = objects.Pop();
                // 原公共 checkpoint DTO 没有 JsonRequired；独立 DLQ 必须仍拒绝用默认 0 补缺失字段。
                if (isCheckpoint && (!names.Contains("formatVersion") || !names.Contains("subscriptionId")
                    || !names.Contains("committedSequence") || !names.Contains("watermarkUtc") || !names.Contains("revision")))
                {
                    throw new InvalidDataException("持久死信检查点缺少必需字段。");
                }
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                propertyName = reader.GetString()!;
                if (!objects.Peek().Names.Add(propertyName))
                    throw new InvalidDataException("持久死信状态包含重复 JSON 字段。");
            }
        }
    }
}
