using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.Cdc;
using SonnetDB.Exceptions;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class FileStreamingDeadLetterTests
{
    [Fact]
    public async Task MoveExhaustedBatchToDeadLetterAsync_WithCompleteEvents_PreservesPayloadHeadersAndTailAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 2);
        var options = Options();
        string deliveryId;
        FileStreamingDeadLetterReceipt receipt;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            await subscription.AdvanceWatermarkAsync(Utc(10));
            await subscription.PublishAsync(new StreamingEvent("original", 1, Utc(1), [0, 1, 255],
                new Dictionary<string, string> { ["Tenant"] = "测试", ["tenant"] = "alpha" }));
            await subscription.PublishAsync(Event(2));
            StreamingDeliveryBatch batch = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            deliveryId = batch.DeliveryId;
            await subscription.PublishAsync(Event(3));
            FileStreamingSubscriptionStatus status = await subscription.GetStatusAsync();
            receipt = await subscription.MoveExhaustedBatchToDeadLetterAsync(
                deliveryId, status.StateRevision, status.InFlightAttempt, "payload schema invalid");
            Assert.Equal(2, receipt.Checkpoint.CommittedSequence);
            Assert.Equal(1, receipt.DeadLetterSequence);
            Assert.Equal(status.StateRevision + 1, receipt.StateRevision);
            Assert.Equal(1, subscription.PendingEventCount);
            Assert.Equal(1, (await subscription.GetStatusAsync()).DeadLetterBatchCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.AcknowledgeAsync(deliveryId).AsTask());
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options);
        FileStreamingDeadLetterSummary summary = Assert.Single(await reopened.ListDeadLettersAsync());
        Assert.Equal(deliveryId, summary.DeliveryId);
        Assert.Equal(definition.SubscriptionId, summary.SubscriptionId);
        Assert.Equal(definition.StreamName, summary.StreamName);
        Assert.Equal("payload schema invalid", summary.Reason);
        Assert.Equal(receipt.Checkpoint, summary.Checkpoint);
        Assert.Equal(2, summary.EventCount);
        FileStreamingDeadLetterBatch deadLetter = Assert.IsType<FileStreamingDeadLetterBatch>(await reopened.ReadDeadLetterAsync(1));
        Assert.Equal([1L, 2L], deadLetter.Events.Select(static value => value.Sequence));
        Assert.Equal("original", deadLetter.Events[0].EventId);
        Assert.Equal(new byte[] { 0, 1, 255 }, deadLetter.Events[0].Payload);
        Assert.Equal("测试", deadLetter.Events[0].Headers!["Tenant"]);
        Assert.Equal("alpha", deadLetter.Events[0].Headers!["tenant"]);
        Assert.True(deadLetter.Events[0].IsLate);
        Assert.Equal(Utc(1), deadLetter.Events[0].EventTimeUtc);
        deadLetter.Events[0].Payload[0] = 99;
        ((Dictionary<string, string>)deadLetter.Events[0].Headers!)["Tenant"] = "changed";
        FileStreamingDeadLetterBatch reread = (await reopened.ReadDeadLetterAsync(1))!;
        Assert.Equal(0, reread.Events[0].Payload[0]);
        Assert.Equal("测试", reread.Events[0].Headers!["Tenant"]);
        StreamingDeliveryBatch tail = (await reopened.ReadBatchAsync())!;
        Assert.Equal(3, Assert.Single(tail.Events).Sequence);
        await reopened.AcknowledgeAsync(tail.DeliveryId);
        Assert.Equal(0, reopened.PendingEventCount);
        Assert.Equal(3, reopened.Checkpoint.CommittedSequence);
        Assert.NotEqual(deliveryId, tail.DeliveryId);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("delivery")]
    [InlineData("attempt")]
    [InlineData("not-exhausted")]
    public async Task MoveExhaustedBatchToDeadLetterAsync_WithStaleCondition_PreservesOriginalBatch(string condition)
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        var options = condition == "not-exhausted" ? Options() with { MaxDeliveryAttempts = 2 } : Options();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options);
        await subscription.PublishAsync(Event(1));
        StreamingDeliveryBatch batch = (await subscription.ReadBatchAsync())!;
        FileStreamingSubscriptionStatus before = await subscription.GetStatusAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.MoveExhaustedBatchToDeadLetterAsync(
            condition == "delivery" ? Guid.NewGuid().ToString("N") : batch.DeliveryId,
            condition == "revision" ? before.StateRevision + 1 : before.StateRevision,
            condition == "attempt" ? before.InFlightAttempt + 1 : before.InFlightAttempt,
            "invalid").AsTask());
        FileStreamingSubscriptionStatus after = await subscription.GetStatusAsync();
        Assert.Equal(before.StateRevision, after.StateRevision);
        Assert.Equal(before.InFlightDeliveryId, after.InFlightDeliveryId);
        Assert.Equal(before.InFlightAttempt, after.InFlightAttempt);
        Assert.Equal(before.StoredBytes, after.StoredBytes);
        Assert.Empty(await subscription.ListDeadLettersAsync());
        Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
    }

    [Fact]
    public async Task MoveExhaustedBatchToDeadLetterAsync_WithCancelledTokenOrInvalidReason_DoesNotWriteIntent()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), Options());
        await subscription.PublishAsync(Event(1));
        StreamingDeliveryBatch batch = (await subscription.ReadBatchAsync())!;
        FileStreamingSubscriptionStatus before = await subscription.GetStatusAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscription.MoveExhaustedBatchToDeadLetterAsync(
            batch.DeliveryId, before.StateRevision, batch.Attempt, "invalid", cancellation.Token).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => subscription.MoveExhaustedBatchToDeadLetterAsync(
            batch.DeliveryId, before.StateRevision, batch.Attempt, new string('界', 1366)).AsTask());
        Assert.Equal(before.StateRevision, subscription.StateRevision);
        Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        Assert.Empty(await subscription.ListDeadLettersAsync());
        await subscription.AcknowledgeAsync(batch.DeliveryId);
    }

    [Fact]
    public async Task MoveExhaustedBatchToDeadLetterAsync_WithBatchCapacityFull_RejectsAtomicallyAndAllowsOriginalAcknowledge()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        var options = Options();
        var deadLetterOptions = new FileStreamingDeadLetterOptions { MaxBatches = 1 };
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options, default, deadLetterOptions);
        await subscription.PublishAsync(Event(1));
        await IsolateCurrentAsync(subscription);
        await subscription.PublishAsync(Event(2));
        StreamingDeliveryBatch batch = (await subscription.ReadBatchAsync())!;
        FileStreamingSubscriptionStatus before = await subscription.GetStatusAsync();
        await Assert.ThrowsAsync<FileStreamingDeadLetterCapacityException>(() => subscription.MoveExhaustedBatchToDeadLetterAsync(
            batch.DeliveryId, before.StateRevision, batch.Attempt, "invalid").AsTask());
        FileStreamingSubscriptionStatus after = await subscription.GetStatusAsync();
        Assert.Equal(before.StateRevision, after.StateRevision);
        Assert.Equal(before.StoredBytes, after.StoredBytes);
        Assert.Equal(batch.DeliveryId, after.InFlightDeliveryId);
        Assert.Equal(batch.Attempt, after.InFlightAttempt);
        Assert.Equal(1, after.PendingEventCount);
        Assert.Equal(1, after.CommittedSequence);
        Assert.Single(await subscription.ListDeadLettersAsync());
        await subscription.AcknowledgeAsync(batch.DeliveryId);
        Assert.Equal(2, subscription.Checkpoint.CommittedSequence);
    }

    [Fact]
    public async Task MoveExhaustedBatchToDeadLetterAsync_WithByteCapacityFull_PreservesEventsRevisionAndCheckpoint()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        var options = Options();
        var deadLetterOptions = new FileStreamingDeadLetterOptions { MaxStoredBytes = 1024 };
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options, default, deadLetterOptions);
        await subscription.PublishAsync(Event(1) with { Payload = new byte[2048] });
        StreamingDeliveryBatch batch = (await subscription.ReadBatchAsync())!;
        FileStreamingSubscriptionStatus before = await subscription.GetStatusAsync();
        await Assert.ThrowsAsync<FileStreamingDeadLetterCapacityException>(() => subscription.MoveExhaustedBatchToDeadLetterAsync(
            batch.DeliveryId, before.StateRevision, batch.Attempt, "invalid").AsTask());
        Assert.Equal(before.StateRevision, subscription.StateRevision);
        Assert.Equal(before.StoredBytes, subscription.StoredBytes);
        Assert.Equal(1, subscription.PendingEventCount);
        Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        Assert.Empty(await subscription.ListDeadLettersAsync());
    }

    [Fact]
    public async Task MoveExhaustedBatchToDeadLetterAsync_WithCheckpointLockTimeout_ReopensAndCompletesIntentExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        var options = Options() with { OperationTimeoutMilliseconds = 200 };
        string deliveryId;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            await subscription.PublishAsync(Event(1));
            StreamingDeliveryBatch batch = (await subscription.ReadBatchAsync())!;
            deliveryId = batch.DeliveryId;
            await subscription.PublishAsync(Event(2));
            string lockPath = Directory.GetFiles(Path.Combine(directory.Path, "checkpoints"), "*.lock").Single();
            using var heldLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await Assert.ThrowsAsync<TimeoutException>(() => subscription.MoveExhaustedBatchToDeadLetterAsync(
                batch.DeliveryId, subscription.StateRevision, batch.Attempt, "poison").AsTask());
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
            Assert.Equal(2, subscription.PendingEventCount);
            Assert.Equal(deliveryId, (await ReadEnvelopeAsync(directory)).State.PendingDeliveryId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.GetStatusAsync().AsTask());
        }

        await using (var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            Assert.Equal(1, reopened.Checkpoint.CommittedSequence);
            Assert.Equal(1, reopened.PendingEventCount);
            Assert.Equal(deliveryId, Assert.Single(await reopened.ListDeadLettersAsync()).DeliveryId);
            Assert.Null((await ReadEnvelopeAsync(directory)).State.PendingDeliveryId);
            Assert.Equal(2, Assert.Single((await reopened.ReadBatchAsync())!.Events).Sequence);
        }

        await using var again = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options);
        Assert.Single(await again.ListDeadLettersAsync());
        Assert.Equal(1, again.Checkpoint.CommittedSequence);
    }

    [Fact]
    public async Task MoveExhaustedBatchToDeadLetterAsync_AfterCheckpointAndSpoolCommitFailure_ReopensWithoutLosingOriginalPayload()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        var options = Options();
        string deliveryId;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            await subscription.PublishAsync(Event(1) with { Payload = [0, 1, 255] });
            StreamingDeliveryBatch batch = (await subscription.ReadBatchAsync())!;
            deliveryId = batch.DeliveryId;
            await subscription.PublishAsync(Event(2));
            string statePath = Path.Combine(directory.Path, "subscription.json");
            string backupPath = statePath + ".owned-backup";
            File.Move(statePath, backupPath);
            Directory.CreateDirectory(statePath);
            try
            {
                Exception? failure = await Record.ExceptionAsync(() => subscription.MoveExhaustedBatchToDeadLetterAsync(
                    deliveryId, subscription.StateRevision, batch.Attempt, "poison").AsTask());
                Assert.True(failure is IOException or UnauthorizedAccessException);
                await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.ReadBatchAsync().AsTask());
                Assert.Equal(deliveryId, (await ReadEnvelopeAsync(directory)).State.PendingDeliveryId);
            }
            finally
            {
                Directory.Delete(statePath);
                File.Move(backupPath, statePath);
            }
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options);
        Assert.Equal(1, reopened.Checkpoint.CommittedSequence);
        Assert.Equal(1, reopened.PendingEventCount);
        Assert.Equal(new byte[] { 0, 1, 255 }, (await reopened.ReadDeadLetterAsync(1))!.Events[0].Payload);
        Assert.Equal(deliveryId, Assert.Single(await reopened.ListDeadLettersAsync()).DeliveryId);
        Assert.Equal(2, Assert.Single((await reopened.ReadBatchAsync())!.Events).Sequence);
        Assert.Null((await ReadEnvelopeAsync(directory)).State.PendingDeliveryId);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("revision")]
    [InlineData("checkpoint")]
    public async Task OpenAsync_WithResignedPendingIntentMismatch_FailsClosedAndPreservesOriginalSpool(string change)
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        var options = Options() with { OperationTimeoutMilliseconds = 200 };
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            await subscription.PublishAsync(Event(1));
            StreamingDeliveryBatch batch = (await subscription.ReadBatchAsync())!;
            string lockPath = Directory.GetFiles(Path.Combine(directory.Path, "checkpoints"), "*.lock").Single();
            using var heldLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await Assert.ThrowsAsync<TimeoutException>(() => subscription.MoveExhaustedBatchToDeadLetterAsync(
                batch.DeliveryId, subscription.StateRevision, batch.Attempt, "poison").AsTask());
        }

        FileStreamingDeadLetterState state = (await ReadEnvelopeAsync(directory)).State;
        FileStreamingDeadLetterRecord record = state.Records[0];
        FileStreamingDeadLetterRecord changed = change switch
        {
            "payload" => record with { Events = [record.Events[0] with { Payload = [99] }] },
            "revision" => record with { ExpectedStateRevision = record.ExpectedStateRevision + 1 },
            _ => record with { Checkpoint = record.Checkpoint with { WatermarkUtc = Utc(20) } },
        };
        await WriteResignedStateAsync(directory, state with { Records = [changed] });
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingSubscription.OpenAsync(directory.Path, definition, options).AsTask());
        using var spool = new CdcEventSpool(Path.Combine(directory.Path, "events.spool"));
        Assert.Single(await spool.ReplayAsync());
        Assert.Equal(-1, spool.AcknowledgedCheckpoints.GetValueOrDefault(0, -1));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("required")]
    [InlineData("checkpoint-required")]
    [InlineData("version")]
    [InlineData("identity")]
    public async Task OpenAsync_WithInvalidDeadLetterState_RejectsStrictly(string change)
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        var options = Options();
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            await subscription.PublishAsync(Event(1));
            await IsolateCurrentAsync(subscription);
        }

        string path = DeadLetterPath(directory);
        string json = await File.ReadAllTextAsync(path);
        if (change == "hash")
            await File.WriteAllTextAsync(path, json.Replace("\"reason\":\"poison\"", "\"reason\":\"changed\"", StringComparison.Ordinal));
        else if (change == "unknown")
            await File.WriteAllTextAsync(path, json.Insert(1, "\"unknown\":1,"));
        else if (change == "duplicate")
            await File.WriteAllTextAsync(path, json.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal));
        else if (change == "missing")
            File.Delete(path);
        else if (change == "required")
            await File.WriteAllTextAsync(path, json.Replace("\"reason\":\"poison\",", "", StringComparison.Ordinal));
        else if (change == "checkpoint-required")
            await File.WriteAllTextAsync(path, json.Replace(
                "\"watermarkUtc\":\"0001-01-01T00:00:00+00:00\",\"revision\":0",
                "\"watermarkUtc\":\"0001-01-01T00:00:00+00:00\"", StringComparison.Ordinal));
        else
        {
            FileStreamingDeadLetterState state = (await ReadEnvelopeAsync(directory)).State;
            await WriteResignedStateAsync(directory, change == "version"
                ? state with { FormatVersion = 99 }
                : state with { StreamName = "other-stream" });
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingSubscription.OpenAsync(directory.Path, definition, options).AsTask());
    }

    [Fact]
    public async Task ListDeadLettersAsync_WithPagination_ReturnsBoundedSummariesAndValidatesRanges()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), Options());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // 三次是完整批次数边界，deadline 提供墙钟终止；每次依赖前一批已完成隔离。
        for (int index = 1; index <= 3; index++)
        {
            await subscription.PublishAsync(Event(index), deadline.Token);
            await IsolateCurrentAsync(subscription, deadline.Token);
        }

        Assert.Equal(1, Assert.Single(await subscription.ListDeadLettersAsync(maxCount: 1)).Sequence);
        Assert.Equal(2, Assert.Single(await subscription.ListDeadLettersAsync(afterSequence: 1, maxCount: 1)).Sequence);
        Assert.Equal([2L, 3L], (await subscription.ListDeadLettersAsync(afterSequence: 1)).Select(static value => value.Sequence));
        Assert.Empty(await subscription.ListDeadLettersAsync(afterSequence: long.MaxValue));
        Assert.Null(await subscription.ReadDeadLetterAsync(4));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => subscription.ListDeadLettersAsync(maxCount: 101).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => subscription.ListDeadLettersAsync(afterSequence: -1).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => subscription.ReadDeadLetterAsync(0).AsTask());
    }

    [Fact]
    public async Task OpenAsync_WithSavedCustomDeadLetterOptions_RestoresOmittedOptionsAndRejectsMismatch()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        var options = Options();
        var deadLetterOptions = new FileStreamingDeadLetterOptions { MaxBatches = 3, MaxStoredBytes = 16 * 1024 };
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options, default, deadLetterOptions))
        {
            await subscription.PublishAsync(Event(1));
            await IsolateCurrentAsync(subscription);
        }

        await using (var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            Assert.Equal(deadLetterOptions, reopened.DeadLetterOptions);
            Assert.Single(await reopened.ListDeadLettersAsync());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingSubscription.OpenAsync(
            directory.Path, definition, options, default, new FileStreamingDeadLetterOptions()).AsTask());
        FileStreamingStateEnvelope original = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(Path.Combine(directory.Path, "subscription.json")),
            FileStreamingJsonContext.Default.FileStreamingStateEnvelope)!;
        Assert.Equal(3, original.State.FormatVersion);
    }

    [Fact]
    public async Task OpenAsync_WithFailedDeadLetterDirectoryPublication_RetriesWithoutResettingExistingRecords()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        var options = Options();
        Directory.CreateDirectory(directory.Path);
        string blocker = Path.Combine(directory.Path, "dead-letters");
        await File.WriteAllTextAsync(blocker, "owned initialization blocker");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => FileStreamingSubscription.OpenAsync(directory.Path, definition, options).AsTask());
            Assert.Empty(Directory.GetDirectories(directory.Path, ".dead-letters-create-*"));
        }
        finally
        {
            File.Delete(blocker);
        }

        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            Assert.Empty(await subscription.ListDeadLettersAsync());
            await subscription.PublishAsync(Event(1));
            await IsolateCurrentAsync(subscription);
        }

        File.Delete(DeadLetterPath(directory));
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingSubscription.OpenAsync(directory.Path, definition, options).AsTask());
    }

    private static async Task IsolateCurrentAsync(FileStreamingSubscription subscription, CancellationToken token = default)
    {
        StreamingDeliveryBatch batch = (await subscription.ReadBatchAsync(token))!;
        FileStreamingSubscriptionStatus status = await subscription.GetStatusAsync(token);
        await subscription.MoveExhaustedBatchToDeadLetterAsync(batch.DeliveryId, status.StateRevision, batch.Attempt, "poison", token);
    }

    private static FileStreamingSubscriptionOptions Options() => new() { MaxDeliveryAttempts = 1 };

    private static StreamingSubscriptionDefinition Definition(int batchSize = 1)
        => StreamingSubscriptionDefinition.Create("dead-letter-subscription", "orders", batchSize, capacity: 4);

    private static StreamingEvent Event(long sequence)
        => new("event-" + sequence, sequence, Utc(sequence), "payload"u8.ToArray());

    private static DateTimeOffset Utc(long seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private static string DeadLetterPath(TemporaryDirectory directory)
        => Path.Combine(directory.Path, "dead-letters", "state.json");

    private static async Task<FileStreamingDeadLetterEnvelope> ReadEnvelopeAsync(TemporaryDirectory directory)
        => JsonSerializer.Deserialize(await File.ReadAllBytesAsync(DeadLetterPath(directory)),
            FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterEnvelope)!;

    private static async Task WriteResignedStateAsync(TemporaryDirectory directory, FileStreamingDeadLetterState state)
    {
        byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterState);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new FileStreamingDeadLetterEnvelope(state, Convert.ToHexString(SHA256.HashData(stateBytes))),
            FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterEnvelope);
        await File.WriteAllBytesAsync(DeadLetterPath(directory), bytes);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sdb-dlq-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            string absolute = System.IO.Path.GetFullPath(Path);
            string expectedParent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (System.IO.Path.GetDirectoryName(absolute) != expectedParent.TrimEnd(System.IO.Path.DirectorySeparatorChar)
                || !System.IO.Path.GetFileName(absolute).StartsWith("sdb-dlq-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("测试临时目录归属不匹配。");
            }

            if (Directory.Exists(absolute))
                Directory.Delete(absolute, recursive: true);
        }
    }
}
