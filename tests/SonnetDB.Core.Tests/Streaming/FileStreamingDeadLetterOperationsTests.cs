using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.Exceptions;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class FileStreamingDeadLetterOperationsTests
{
    [Fact]
    public async Task ReplayDeadLetterAsync_AfterReopen_PreservesReplayIdentityAndCompletesWithConditionalDelete()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        FileStreamingDeadLetterReplayBatch first;
        StreamingSubscriptionCheckpoint checkpoint;
        await using (var subscription = await OpenAsync(directory, deadline.Token))
        {
            await subscription.AdvanceWatermarkAsync(Utc(10), deadline.Token);
            await subscription.PublishAsync(Event(1) with
            {
                Payload = [0, 1, 255],
                Headers = new Dictionary<string, string> { ["Tenant"] = "测试", ["tenant"] = "other" },
            }, deadline.Token);
            FileStreamingDeadLetterSummary summary = await IsolateAsync(subscription, deadline.Token);
            Assert.Null(summary.ReplayId);
            Assert.Equal(0, summary.ReplayAttempt);
            checkpoint = subscription.Checkpoint;
            first = await subscription.ReplayDeadLetterAsync(summary.Sequence, summary.DeliveryId,
                summary.DeadLetterRevision, deadline.Token);
            Assert.NotNull(first.Summary.ReplayId);
            Assert.Equal(1, first.Summary.ReplayAttempt);
            Assert.Equal(summary.DeadLetterRevision + 1, first.Summary.DeadLetterRevision);
            Assert.True(Assert.Single(first.Events).IsLate);
            first.Events[0].Payload[0] = 99;
            ((Dictionary<string, string>)first.Events[0].Headers!)["Tenant"] = "changed";
            Assert.Equal(checkpoint, subscription.Checkpoint);
            Assert.Equal(0, subscription.PendingEventCount);
        }

        await using (var reopened = await OpenAsync(directory, deadline.Token))
        {
            FileStreamingDeadLetterSummary saved = Assert.Single(await reopened.ListDeadLettersAsync(cancellationToken: deadline.Token));
            Assert.Equal(first.Summary.ReplayId, saved.ReplayId);
            Assert.Equal(1, saved.ReplayAttempt);
            Assert.NotNull(saved.LastReplayClaimedAtUtc);
            FileStreamingDeadLetterReplayBatch retry = await reopened.ReplayDeadLetterAsync(saved.Sequence,
                saved.DeliveryId, saved.DeadLetterRevision, deadline.Token);
            Assert.Equal(saved.ReplayId, retry.Summary.ReplayId);
            Assert.Equal(2, retry.Summary.ReplayAttempt);
            Assert.Equal(new byte[] { 0, 1, 255 }, retry.Events[0].Payload);
            Assert.Equal("测试", retry.Events[0].Headers!["Tenant"]);
            Assert.Equal("other", retry.Events[0].Headers!["tenant"]);
            Assert.Equal("event-1", retry.Events[0].EventId);
            Assert.Equal(1, retry.Events[0].Sequence);
            Assert.Equal(Utc(1), retry.Events[0].EventTimeUtc);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.DeleteDeadLetterAsync(saved.Sequence,
                saved.DeliveryId, retry.Summary.DeadLetterRevision, saved.ReplayId, saved.ReplayAttempt, deadline.Token).AsTask());
            FileStreamingDeadLetterDeletionReceipt deleted = await DeleteAsync(reopened, retry.Summary, deadline.Token);
            Assert.Equal(retry.Summary.ReplayId, deleted.ReplayId);
            Assert.Equal(retry.Summary.DeadLetterRevision + 1, deleted.DeadLetterRevision);
            Assert.Equal(checkpoint, reopened.Checkpoint);
        }

        await using var again = await OpenAsync(directory, deadline.Token);
        Assert.Empty(await again.ListDeadLettersAsync(cancellationToken: deadline.Token));
        Assert.Null(await again.ReadDeadLetterAsync(1, deadline.Token));
        Assert.Equal(checkpoint, again.Checkpoint);
    }

    [Fact]
    public async Task DeleteDeadLetterAsync_WithSequenceHoles_ReclaimsCapacityAndNeverReusesSequenceAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var limits = new FileStreamingDeadLetterOptions { MaxBatches = 3 };
        await using (var subscription = await OpenAsync(directory, deadline.Token, limits))
        {
            // 三个项目是明确边界，deadline 同时提供墙钟终止和取消；从单批用例验证过的相同操作扩展。
            for (int sequence = 1; sequence <= 3; sequence++)
            {
                await subscription.PublishAsync(Event(sequence), deadline.Token);
                await IsolateAsync(subscription, deadline.Token);
            }

            FileStreamingDeadLetterSummary middle = (await subscription.ReadDeadLetterAsync(2, deadline.Token))!.Summary;
            long beforeBytes = (await subscription.GetStatusAsync(deadline.Token)).DeadLetterStoredBytes;
            await DeleteAsync(subscription, middle, deadline.Token);
            FileStreamingSubscriptionStatus after = await subscription.GetStatusAsync(deadline.Token);
            Assert.Equal(2, after.DeadLetterBatchCount);
            Assert.True(after.DeadLetterStoredBytes < beforeBytes);
            Assert.Equal(3, Assert.Single(await subscription.ListDeadLettersAsync(1, 1, deadline.Token)).Sequence);
            Assert.Null(await subscription.ReadDeadLetterAsync(2, deadline.Token));
            Assert.Equal([1L, 3L], (await subscription.ListDeadLettersAsync(cancellationToken: deadline.Token)).Select(static value => value.Sequence));
            await DeleteAsync(subscription, (await subscription.ReadDeadLetterAsync(1, deadline.Token))!.Summary, deadline.Token);
            await DeleteAsync(subscription, (await subscription.ReadDeadLetterAsync(3, deadline.Token))!.Summary, deadline.Token);
        }

        await using var reopened = await OpenAsync(directory, deadline.Token, limits);
        await reopened.PublishAsync(Event(4), deadline.Token);
        FileStreamingDeadLetterSummary next = await IsolateAsync(reopened, deadline.Token);
        Assert.Equal(4, next.Sequence);
        Assert.Null(next.ReplayId);
        Assert.Empty(await reopened.ListDeadLettersAsync(long.MaxValue, cancellationToken: deadline.Token));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("delivery")]
    [InlineData("replay")]
    [InlineData("attempt")]
    [InlineData("unclaimed")]
    public async Task DeleteDeadLetterAsync_WithStaleCondition_PreservesClaimAndEvents(string condition)
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = await OpenAsync(directory, deadline.Token);
        await subscription.PublishAsync(Event(1), deadline.Token);
        FileStreamingDeadLetterSummary summary = await IsolateAsync(subscription, deadline.Token);
        FileStreamingDeadLetterReplayBatch replay = await subscription.ReplayDeadLetterAsync(summary.Sequence,
            summary.DeliveryId, summary.DeadLetterRevision, deadline.Token);
        FileStreamingDeadLetterSummary claimed = replay.Summary;
        await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.DeleteDeadLetterAsync(claimed.Sequence,
            condition == "delivery" ? Guid.NewGuid().ToString("N") : claimed.DeliveryId,
            condition == "revision" ? claimed.DeadLetterRevision - 1 : claimed.DeadLetterRevision,
            condition == "replay" ? Guid.NewGuid().ToString("N") : condition == "unclaimed" ? null : claimed.ReplayId,
            condition == "attempt" ? claimed.ReplayAttempt + 1 : condition == "unclaimed" ? 0 : claimed.ReplayAttempt,
            deadline.Token).AsTask());
        Assert.Equal(claimed, Assert.Single(await subscription.ListDeadLettersAsync(cancellationToken: deadline.Token)));
        Assert.Equal(claimed.DeadLetterRevision, subscription.DeadLetterRevision);
        Assert.Equal("payload"u8.ToArray(), (await subscription.ReadDeadLetterAsync(1, deadline.Token))!.Events[0].Payload);
    }

    [Fact]
    public async Task ReplayDeadLetterAsync_WithStaleRevisionOrIdentity_DoesNotCreateClaim()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = await OpenAsync(directory, deadline.Token);
        await subscription.PublishAsync(Event(1), deadline.Token);
        FileStreamingDeadLetterSummary summary = await IsolateAsync(subscription, deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.ReplayDeadLetterAsync(1,
            summary.DeliveryId, summary.DeadLetterRevision + 1, deadline.Token).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.ReplayDeadLetterAsync(1,
            Guid.NewGuid().ToString("N"), summary.DeadLetterRevision, deadline.Token).AsTask());
        Assert.Equal(summary, Assert.Single(await subscription.ListDeadLettersAsync(cancellationToken: deadline.Token)));
    }

    [Fact]
    public async Task ReplayAndDeleteDeadLetterAsync_WithCancelledToken_DoesNotCommitOperation()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = await OpenAsync(directory, deadline.Token);
        await subscription.PublishAsync(Event(1), deadline.Token);
        FileStreamingDeadLetterSummary summary = await IsolateAsync(subscription, deadline.Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscription.ReplayDeadLetterAsync(1,
            summary.DeliveryId, summary.DeadLetterRevision, cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeleteAsync(subscription, summary, cancellation.Token));
        Assert.Equal(summary, Assert.Single(await subscription.ListDeadLettersAsync(cancellationToken: deadline.Token)));
    }

    [Fact]
    public async Task ReplayDeadLetterAsync_WithStatePublicationFailure_ReopensWithoutInventingSuccessfulClaim()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var subscription = await OpenAsync(directory, deadline.Token))
        {
            await subscription.PublishAsync(Event(1), deadline.Token);
            FileStreamingDeadLetterSummary summary = await IsolateAsync(subscription, deadline.Token);
            string path = StatePath(directory);
            string saved = path + ".test-saved";
            File.Move(path, saved);
            try
            {
                Directory.CreateDirectory(path);
                Exception? failure = await Record.ExceptionAsync(() => subscription.ReplayDeadLetterAsync(1,
                    summary.DeliveryId, summary.DeadLetterRevision, deadline.Token).AsTask());
                Assert.True(failure is IOException or UnauthorizedAccessException);
                await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.ReadDeadLetterAsync(1, deadline.Token).AsTask());
            }
            finally
            {
                Directory.Delete(path);
                File.Move(saved, path);
            }

            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));
        }

        await using var reopened = await OpenAsync(directory, deadline.Token);
        FileStreamingDeadLetterSummary retained = Assert.Single(await reopened.ListDeadLettersAsync(cancellationToken: deadline.Token));
        Assert.Null(retained.ReplayId);
        Assert.Equal(0, retained.ReplayAttempt);
        Assert.Equal(1, (await reopened.ReplayDeadLetterAsync(1, retained.DeliveryId,
            retained.DeadLetterRevision, deadline.Token)).Summary.ReplayAttempt);
    }

    [Fact]
    public async Task ReplayDeadLetterAsync_WithPendingIsolation_ReopensAndFinishesIsolationBeforeClaim()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = Options() with { OperationTimeoutMilliseconds = 200 };
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), options, deadline.Token))
        {
            await subscription.PublishAsync(Event(1), deadline.Token);
            StreamingDeliveryBatch delivery = (await subscription.ReadBatchAsync(deadline.Token))!;
            string lockPath = Directory.GetFiles(Path.Combine(directory.Path, "checkpoints"), "*.lock").Single();
            using var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await Assert.ThrowsAsync<TimeoutException>(() => subscription.MoveExhaustedBatchToDeadLetterAsync(
                delivery.DeliveryId, subscription.StateRevision, delivery.Attempt, "poison", deadline.Token).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.ReplayDeadLetterAsync(1,
                delivery.DeliveryId, subscription.DeadLetterRevision, deadline.Token).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.DeleteDeadLetterAsync(1,
                delivery.DeliveryId, subscription.DeadLetterRevision, null, 0, deadline.Token).AsTask());
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), options, deadline.Token);
        FileStreamingDeadLetterSummary summary = Assert.Single(await reopened.ListDeadLettersAsync(cancellationToken: deadline.Token));
        Assert.Equal(1, reopened.Checkpoint.CommittedSequence);
        Assert.Equal(0, reopened.PendingEventCount);
        Assert.Equal(1, (await reopened.ReplayDeadLetterAsync(1, summary.DeliveryId,
            summary.DeadLetterRevision, deadline.Token)).Summary.ReplayAttempt);
    }

    [Fact]
    public async Task OpenAsync_WithValidVersion1State_MigratesHashAndPreservesOriginalEvents()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        LegacyFileStreamingDeadLetterState legacy = await CreateLegacyStateAsync(directory, deadline.Token);
        await WriteLegacyAsync(directory, legacy, deadline.Token);
        await using var reopened = await OpenAsync(directory, deadline.Token);
        FileStreamingDeadLetterSummary summary = Assert.Single(await reopened.ListDeadLettersAsync(cancellationToken: deadline.Token));
        Assert.Equal(1, summary.DeadLetterRevision);
        Assert.Equal(legacy.Records[0].DeliveryId, summary.DeliveryId);
        Assert.Equal("payload"u8.ToArray(), (await reopened.ReadDeadLetterAsync(1, deadline.Token))!.Events[0].Payload);
        FileStreamingDeadLetterEnvelope migrated = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(StatePath(directory), deadline.Token),
            FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterEnvelope)!;
        Assert.Equal(2, migrated.State.FormatVersion);
        Assert.Equal(2, migrated.State.NextSequence);
        Assert.Null(migrated.State.Records[0].Replay);
    }

    [Fact]
    public async Task OpenAsync_WithVersion1AtByteCapacity_RejectsMigrationAndPreservesOriginalFile()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        LegacyFileStreamingDeadLetterState legacy = await CreateLegacyStateAsync(directory, deadline.Token);
        legacy = legacy with { Records = [legacy.Records[0] with { Events = [legacy.Records[0].Events[0] with { Payload = new byte[1024] }] }] };
        byte[] sizing = JsonSerializer.SerializeToUtf8Bytes(new LegacyFileStreamingDeadLetterEnvelope(legacy, new string('A', 64)),
            FileStreamingDeadLetterJsonContext.Default.LegacyFileStreamingDeadLetterEnvelope);
        legacy = legacy with { Options = legacy.Options with { MaxStoredBytes = sizing.Length } };
        await WriteLegacyAsync(directory, legacy, deadline.Token);
        byte[] original = await File.ReadAllBytesAsync(StatePath(directory), deadline.Token);
        Assert.True(original.Length <= legacy.Options.MaxStoredBytes);
        await Assert.ThrowsAsync<FileStreamingDeadLetterCapacityException>(() => OpenAsync(directory, deadline.Token).AsTask());
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath(directory), deadline.Token));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StatePath(directory))!, "*.tmp-*"));
    }

    [Theory]
    [InlineData("records-null")]
    [InlineData("state-null")]
    [InlineData("record-null")]
    [InlineData("options-null")]
    [InlineData("over-capacity")]
    [InlineData("over-maximum")]
    [InlineData("events-null")]
    [InlineData("missing-required")]
    public async Task OpenAsync_WithResignedInvalidVersion1State_FailsClosed(string change)
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        LegacyFileStreamingDeadLetterState legacy = await CreateLegacyStateAsync(directory, deadline.Token);
        legacy = change switch
        {
            "state-null" => null!,
            "records-null" => legacy with { Records = null! },
            "record-null" => legacy with { Records = [null!] },
            "options-null" => legacy with { Options = null! },
            "over-capacity" => legacy with { Options = legacy.Options with { MaxBatches = 1 }, Records = [legacy.Records[0], legacy.Records[0]] },
            "over-maximum" => legacy with { Options = legacy.Options with { MaxBatches = 10_000 }, Records = new LegacyFileStreamingDeadLetterRecord[10_001] },
            "events-null" => legacy with { Records = [legacy.Records[0] with { Events = null! }] },
            _ => legacy,
        };
        await WriteLegacyAsync(directory, legacy, deadline.Token);
        if (change == "missing-required")
        {
            string json = await File.ReadAllTextAsync(StatePath(directory), deadline.Token);
            await File.WriteAllTextAsync(StatePath(directory), json.Replace("\"reason\":\"poison\",", "", StringComparison.Ordinal), deadline.Token);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(directory, deadline.Token).AsTask());
    }

    [Theory]
    [InlineData("next-sequence")]
    [InlineData("revision")]
    [InlineData("high-watermark-revision")]
    [InlineData("replay-identity")]
    [InlineData("replay-attempt")]
    public async Task OpenAsync_WithResignedInvalidReplayState_FailsClosed(string change)
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var subscription = await OpenAsync(directory, deadline.Token))
        {
            await subscription.PublishAsync(Event(1), deadline.Token);
            FileStreamingDeadLetterSummary summary = await IsolateAsync(subscription, deadline.Token);
            await subscription.ReplayDeadLetterAsync(1, summary.DeliveryId, summary.DeadLetterRevision, deadline.Token);
        }

        FileStreamingDeadLetterState state = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(StatePath(directory), deadline.Token),
            FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterEnvelope)!.State;
        state = change switch
        {
            "next-sequence" => state with { NextSequence = 1 },
            "revision" => state with { Revision = 1 },
            "high-watermark-revision" => state with { NextSequence = 4 },
            "replay-identity" => state with { Records = [state.Records[0] with { Replay = state.Records[0].Replay! with { ReplayId = "invalid" } }] },
            _ => state with { Records = [state.Records[0] with { Replay = state.Records[0].Replay! with { Attempt = 0 } }] },
        };
        byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterState);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new FileStreamingDeadLetterEnvelope(state, Convert.ToHexString(SHA256.HashData(stateBytes))),
            FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterEnvelope);
        await File.WriteAllBytesAsync(StatePath(directory), bytes, deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(directory, deadline.Token).AsTask());
    }

    [Fact]
    public async Task ReplayDeadLetterAsync_WithExhaustedReplayLimit_PreservesEventsAndAllowsConditionalDelete()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var subscription = await OpenAsync(directory, deadline.Token))
        {
            await subscription.PublishAsync(Event(1), deadline.Token);
            FileStreamingDeadLetterSummary summary = await IsolateAsync(subscription, deadline.Token);
            await subscription.ReplayDeadLetterAsync(1, summary.DeliveryId, summary.DeadLetterRevision, deadline.Token);
        }

        FileStreamingDeadLetterState state = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(StatePath(directory), deadline.Token),
            FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterEnvelope)!.State;
        state = state with
        {
            Revision = FileStreamingDeadLetterStore.MaxReplayAttempts + 1L,
            Records = [state.Records[0] with { Replay = state.Records[0].Replay! with { Attempt = FileStreamingDeadLetterStore.MaxReplayAttempts } }],
        };
        byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterState);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new FileStreamingDeadLetterEnvelope(state, Convert.ToHexString(SHA256.HashData(stateBytes))),
            FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterEnvelope);
        await File.WriteAllBytesAsync(StatePath(directory), bytes, deadline.Token);
        await using var reopened = await OpenAsync(directory, deadline.Token);
        FileStreamingDeadLetterSummary exhausted = Assert.Single(await reopened.ListDeadLettersAsync(cancellationToken: deadline.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReplayDeadLetterAsync(1,
            exhausted.DeliveryId, exhausted.DeadLetterRevision, deadline.Token).AsTask());
        Assert.Equal(exhausted, Assert.Single(await reopened.ListDeadLettersAsync(cancellationToken: deadline.Token)));
        await DeleteAsync(reopened, exhausted, deadline.Token);
        Assert.Null(await reopened.ReadDeadLetterAsync(1, deadline.Token));
    }

    private static ValueTask<FileStreamingSubscription> OpenAsync(TemporaryDirectory directory, CancellationToken token,
        FileStreamingDeadLetterOptions? limits = null)
        => FileStreamingSubscription.OpenAsync(directory.Path, Definition(), Options(), token, limits);

    private static StreamingSubscriptionDefinition Definition()
        => StreamingSubscriptionDefinition.Create("dead-letter-operations", "orders", batchSize: 1, capacity: 4);

    private static FileStreamingSubscriptionOptions Options() => new() { MaxDeliveryAttempts = 1 };

    private static StreamingEvent Event(long sequence)
        => new("event-" + sequence, sequence, Utc(sequence), "payload"u8.ToArray());

    private static DateTimeOffset Utc(long seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private static async Task<FileStreamingDeadLetterSummary> IsolateAsync(FileStreamingSubscription subscription, CancellationToken token)
    {
        StreamingDeliveryBatch batch = (await subscription.ReadBatchAsync(token))!;
        FileStreamingDeadLetterReceipt receipt = await subscription.MoveExhaustedBatchToDeadLetterAsync(
            batch.DeliveryId, subscription.StateRevision, batch.Attempt, "poison", token);
        return (await subscription.ReadDeadLetterAsync(receipt.DeadLetterSequence, token))!.Summary;
    }

    private static Task<FileStreamingDeadLetterDeletionReceipt> DeleteAsync(FileStreamingSubscription subscription,
        FileStreamingDeadLetterSummary summary, CancellationToken token)
        => subscription.DeleteDeadLetterAsync(summary.Sequence, summary.DeliveryId, summary.DeadLetterRevision,
            summary.ReplayId, summary.ReplayAttempt, token).AsTask();

    private static string StatePath(TemporaryDirectory directory) => Path.Combine(directory.Path, "dead-letters", "state.json");

    private static async Task<LegacyFileStreamingDeadLetterState> CreateLegacyStateAsync(TemporaryDirectory directory, CancellationToken token)
    {
        await using var subscription = await OpenAsync(directory, token);
        await subscription.PublishAsync(Event(1), token);
        await IsolateAsync(subscription, token);
        FileStreamingDeadLetterState state = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(StatePath(directory), token),
            FileStreamingDeadLetterJsonContext.Default.FileStreamingDeadLetterEnvelope)!.State;
        FileStreamingDeadLetterRecord record = state.Records[0];
        return new LegacyFileStreamingDeadLetterState(1, state.SubscriptionId, state.StreamName, state.Options,
            [new LegacyFileStreamingDeadLetterRecord(record.Sequence, record.DeliveryId, record.Attempt, record.Reason,
                record.CreatedAtUtc, record.ExpectedStateRevision, record.PreviousCheckpoint, record.Checkpoint, record.Events)], null);
    }

    private static async Task WriteLegacyAsync(TemporaryDirectory directory, LegacyFileStreamingDeadLetterState state, CancellationToken token)
    {
        byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, FileStreamingDeadLetterJsonContext.Default.LegacyFileStreamingDeadLetterState);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new LegacyFileStreamingDeadLetterEnvelope(state, Convert.ToHexString(SHA256.HashData(stateBytes))),
            FileStreamingDeadLetterJsonContext.Default.LegacyFileStreamingDeadLetterEnvelope);
        await File.WriteAllBytesAsync(StatePath(directory), bytes, token);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _ownerRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SonnetDB-dlq-operations-tests"));

        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(_ownerRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(_ownerRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试目录超出本任务独占临时根目录。");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
