using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Cdc;
using SonnetDB.Exceptions;

namespace SonnetDB.Streaming;

internal sealed class FileStreamingDeadLetterStore
{
    private const int LegacyFormatVersion = 1;
    private const int FormatVersion = 2;
    private const int MaximumFileBytes = 128 * 1024 * 1024;
    internal const int MaxReasonBytes = 4096;
    internal const int MaxReplayAttempts = 1_000_000;
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
            byte[] bytes;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (stream.Length < 1 || stream.Length > MaximumFileBytes)
                    throw new InvalidDataException("持久死信状态文件长度越界。");
                bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("持久死信状态文件在读取时发生变化。");
            }

            // 迁移会原子替换 state.json；先关闭读取句柄，避免 Windows FileShare.Read 阻止替换。
            ValidateUniqueProperties(bytes, token);
            (FileStreamingDeadLetterEnvelope envelope, bool migrated) = Decode(bytes, token);
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

            var restored = new FileStreamingDeadLetterStore(directory, envelope.State, bytes.Length);
            if (migrated)
                await restored.CommitAsync(envelope.State, token).ConfigureAwait(false);
            return restored;
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
        if (record.Sequence != State.NextSequence)
            throw new InvalidOperationException("死信隔离序号与目录高水位不匹配。");
        var proposed = State with
        {
            Records = [.. State.Records, record],
            PendingDeliveryId = record.DeliveryId,
            NextSequence = checked(State.NextSequence + 1),
            Revision = checked(State.Revision + 1),
        };
        // 容量检查在任何写入前完成；清除 pending 字段的最终状态只会更小。
        _ = Encode(proposed);
        return proposed;
    }

    internal FileStreamingDeadLetterState PrepareOperation(FileStreamingDeadLetterRecord[] records)
    {
        if (State.PendingDeliveryId is not null)
            throw new InvalidOperationException("死信隔离意图尚未恢复完成，必须重开订阅。");
        var proposed = State with { Records = records, Revision = checked(State.Revision + 1) };
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

    private static (FileStreamingDeadLetterEnvelope Envelope, bool Migrated) Decode(byte[] bytes, CancellationToken token)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("state", out JsonElement stateElement)
            || stateElement.ValueKind != JsonValueKind.Object
            || !stateElement.TryGetProperty("formatVersion", out JsonElement versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out int version))
        {
            throw new InvalidDataException("持久死信状态缺少格式版本。");
        }

        if (version == FormatVersion)
        {
            return (JsonSerializer.Deserialize(bytes, FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterEnvelope)
                ?? throw new InvalidDataException("持久死信状态为空。"), false);
        }

        if (version != LegacyFormatVersion)
            throw new InvalidDataException($"不支持持久死信状态格式版本 {version}。");
        LegacyFileStreamingDeadLetterEnvelope legacy = JsonSerializer.Deserialize(
            bytes, FileStreamingDeadLetterJsonContext.Default.LegacyFileStreamingDeadLetterEnvelope)
            ?? throw new InvalidDataException("持久死信旧状态为空。");
        if (legacy.State is null || legacy.Sha256 is null || legacy.State.Options is null
            || legacy.State.Records is null || legacy.State.Records.Length > 10_000
            || legacy.State.Records.Length > legacy.State.Options.MaxBatches)
        {
            throw new InvalidDataException("持久死信旧状态必需字段或批次数无效。");
        }

        byte[] legacyBytes = JsonSerializer.SerializeToUtf8Bytes(
            legacy.State, FileStreamingDeadLetterJsonContext.Default.LegacyFileStreamingDeadLetterState);
        if (legacy.Sha256 != Convert.ToHexString(SHA256.HashData(legacyBytes)))
            throw new InvalidDataException("持久死信旧状态 SHA-256 校验失败。");
        legacy.State.Options.Validate();
        var records = new FileStreamingDeadLetterRecord[legacy.State.Records.Length];
        // 最多 10,000 条旧记录；OpenAsync 的操作令牌同时约束墙钟和取消。
        for (int index = 0; index < records.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            LegacyFileStreamingDeadLetterRecord record = legacy.State.Records[index]
                ?? throw new InvalidDataException("持久死信旧批次为空。");
            if (record.Sequence != index + 1L)
                throw new InvalidDataException("持久死信旧批次序号不连续。");
            records[index] = new FileStreamingDeadLetterRecord(record.Sequence, record.DeliveryId,
                record.Attempt, record.Reason, record.CreatedAtUtc, record.ExpectedStateRevision,
                record.PreviousCheckpoint, record.Checkpoint, record.Events);
        }

        var migrated = new FileStreamingDeadLetterState(FormatVersion, legacy.State.SubscriptionId,
            legacy.State.StreamName, legacy.State.Options, records, legacy.State.PendingDeliveryId)
        {
            NextSequence = records.Length + 1L,
            Revision = records.Length,
        };
        byte[] migratedBytes = JsonSerializer.SerializeToUtf8Bytes(
            migrated, FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterState);
        return (new FileStreamingDeadLetterEnvelope(migrated, Convert.ToHexString(SHA256.HashData(migratedBytes))), true);
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
        if (state.Records.Length > state.Options.MaxBatches || state.NextSequence < 1
            || state.Revision < state.NextSequence - 1)
            throw new InvalidDataException("持久死信批次数、序号高水位或目录修订号无效。");
        var deliveryIds = new HashSet<string>(StringComparer.Ordinal);
        var replayIds = new HashSet<string>(StringComparer.Ordinal);
        long lastDeadLetterSequence = 0;
        long minimumRevision = state.NextSequence - 1;
        long lastEventSequence = -1;
        long lastCheckpointRevision = -1;
        long lastStateRevision = -1;
        for (int index = 0; index < state.Records.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            FileStreamingDeadLetterRecord record = state.Records[index]
                ?? throw new InvalidDataException("持久死信批次为空。");
            if (record.Sequence <= lastDeadLetterSequence || record.Sequence >= state.NextSequence
                || !Guid.TryParseExact(record.DeliveryId, "N", out _)
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
            if (record.Replay is { } replay)
            {
                if (!Guid.TryParseExact(replay.ReplayId, "N", out _) || !replayIds.Add(replay.ReplayId)
                    || replay.Attempt < 1 || replay.Attempt > MaxReplayAttempts
                    || replay.LastClaimedAtUtc.Offset != TimeSpan.Zero
                    || record.DeliveryId == state.PendingDeliveryId)
                {
                    throw new InvalidDataException("死信重放身份、领取次数或隔离状态无效。");
                }

                minimumRevision = checked(minimumRevision + replay.Attempt);
            }
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
            lastDeadLetterSequence = record.Sequence;
        }

        if (state.Revision < minimumRevision)
            throw new InvalidDataException("死信目录修订号早于已保存的隔离和重放操作。");

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
