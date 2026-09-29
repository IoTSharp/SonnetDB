using System.Collections;
using SonnetDB.Cdc;

namespace SonnetDB.Core.Tests.Cdc;

public sealed class CdcSnapshotReplicaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-cdc-snapshot-" + Guid.NewGuid().ToString("N"));

    public CdcSnapshotReplicaTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task SnapshotAndSpool_PartialReopenAndIncrementalResume_PreserveMaterializedResult()
    {
        string path = Path.Combine(_root, "replica.bin");
        string spoolPath = Path.Combine(_root, "events.log");
        CdcSnapshotDescriptor descriptor = Descriptor(2);
        await using (var spool = new CdcEventSpool(spoolPath))
        {
            await spool.AppendAsync(Event(11, "b", CdcOperation.Insert, "{\"value\":1}"));
            await spool.AppendAsync(Event(12, "a", CdcOperation.Update, "{\"value\":2}"));
            await spool.AppendAsync(Event(13, "c", CdcOperation.Delete));
            await using (var replica = await CdcSnapshotReplica.CreateAsync(path, descriptor))
            {
                await replica.WriteSnapshotPageAsync(0, [Row("a", 1)]);
                await Assert.ThrowsAsync<InvalidOperationException>(() => replica.CompleteSnapshotAsync().AsTask());
                await Assert.ThrowsAsync<InvalidOperationException>(() => replica.ReadRowsAsync().AsTask());
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    replica.ApplyIncrementalAsync([Event(11, "b", CdcOperation.Insert, "{}")]).AsTask());
            }

            await using var recovered = CdcSnapshotReplica.Open(path, descriptor);
            CdcSnapshotReplicaState resume = recovered.GetState();
            Assert.Equal(1, resume.SnapshotRowsCopied);
            Assert.Equal("a", resume.LastSnapshotKey);
            Assert.Equal(descriptor.Checkpoint, resume.AppliedCheckpoint);
            await recovered.WriteSnapshotPageAsync(resume.SnapshotRowsCopied, [Row("c", 1)]);
            await recovered.CompleteSnapshotAsync();
            CdcEventSpoolBatch first = await spool.ReplayBatchAsync(descriptor.Checkpoint, maxEvents: 2);
            CdcSnapshotReplicaState applied = await recovered.ApplyIncrementalAsync(first.Events);
            await spool.AcknowledgeAsync(applied.AppliedCheckpoint);
            Assert.Equal(new CdcCheckpoint(7, 12), applied.AppliedCheckpoint);
        }

        await using var reopenedReplica = CdcSnapshotReplica.Open(path, descriptor);
        await using var reopenedSpool = new CdcEventSpool(spoolPath);
        CdcEventSpoolBatch remaining = await reopenedSpool.ReplayBatchAsync(reopenedReplica.GetState().AppliedCheckpoint);
        Assert.Equal(13, Assert.Single(remaining.Events).Metadata.Checkpoint.Offset);
        CdcSnapshotReplicaState final = await reopenedReplica.ApplyIncrementalAsync(remaining.Events);
        await reopenedSpool.AcknowledgeAsync(final.AppliedCheckpoint);
        CdcSnapshotRowBatch result = await reopenedReplica.ReadRowsAsync();
        Assert.Equal([Row("a", 2), Row("b", 1)], result.Rows);
        Assert.Equal(new CdcCheckpoint(7, 13), result.AppliedCheckpoint);
        Assert.Empty(await reopenedSpool.ReplayAsync());
    }

    [Fact]
    public async Task SnapshotAndSpool_LastMaterializedEventBeforeAck_ReopenConfirmsTailAndReleasesCapacity()
    {
        string path = Path.Combine(_root, "replica-tail.bin");
        string spoolPath = Path.Combine(_root, "events-tail.log");
        CdcSnapshotDescriptor descriptor = Descriptor(0);
        var spoolOptions = new CdcEventSpoolOptions { MaxEvents = 1 };
        await using (var replica = await CdcSnapshotReplica.CreateAsync(path, descriptor))
        await using (var spool = new CdcEventSpool(spoolPath, spoolOptions))
        {
            await replica.CompleteSnapshotAsync();
            await spool.AppendAsync(Event(11, "a", CdcOperation.Insert, "{\"value\":1}"));
            CdcEventSpoolBatch first = await spool.ReplayBatchAsync(descriptor.Checkpoint);
            await replica.ApplyIncrementalAsync(first.Events);
            Assert.Empty(spool.AcknowledgedCheckpoints);
            Assert.Equal(1, spool.EventCount);
        }

        await using var reopenedReplica = CdcSnapshotReplica.Open(path, descriptor);
        await using var reopenedSpool = new CdcEventSpool(spoolPath, spoolOptions);
        CdcSnapshotReplicaState state = reopenedReplica.GetState();
        Assert.Equal(new CdcCheckpoint(7, 11), state.AppliedCheckpoint);
        Assert.Empty((await reopenedSpool.ReplayBatchAsync(state.AppliedCheckpoint)).Events);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopenedSpool.AppendAsync(Event(12, "b", CdcOperation.Insert, "{\"value\":2}")).AsTask());

        long acknowledgedOffset = reopenedSpool.AcknowledgedCheckpoints.GetValueOrDefault(
            state.AppliedCheckpoint.Partition, -1);
        Assert.True(acknowledgedOffset < state.AppliedCheckpoint.Offset);
        if (reopenedSpool.EventCount > 0)
            await reopenedSpool.AcknowledgeAsync(state.AppliedCheckpoint);
        Assert.Equal(0, reopenedSpool.EventCount);
        Assert.Equal(state.AppliedCheckpoint, reopenedSpool.AcknowledgedCheckpoint);

        await reopenedSpool.AppendAsync(Event(12, "b", CdcOperation.Insert, "{\"value\":2}"));
        CdcEventSpoolBatch next = await reopenedSpool.ReplayBatchAsync(state.AppliedCheckpoint);
        Assert.Equal(12, Assert.Single(next.Events).Metadata.Checkpoint.Offset);
        CdcSnapshotReplicaState final = await reopenedReplica.ApplyIncrementalAsync(next.Events);
        await reopenedSpool.AcknowledgeAsync(final.AppliedCheckpoint);
        Assert.Equal(["a", "b"], (await reopenedReplica.ReadRowsAsync()).Rows.Select(static row => row.Key));
    }

    [Fact]
    public async Task CompleteSnapshot_EmptySnapshotAndRepeatedCompletion_AreDurable()
    {
        string path = Path.Combine(_root, "empty.bin");
        CdcSnapshotDescriptor descriptor = Descriptor(0);
        await using (var replica = await CdcSnapshotReplica.CreateAsync(path, descriptor))
        {
            CdcSnapshotReplicaState first = await replica.CompleteSnapshotAsync();
            Assert.Equal(first, await replica.CompleteSnapshotAsync());
            Assert.Empty((await replica.ReadRowsAsync()).Rows);
        }
        await using var reopened = CdcSnapshotReplica.Open(path, descriptor);
        Assert.Equal(CdcSnapshotPhase.Incremental, reopened.GetState().Phase);
        await reopened.ApplyIncrementalAsync([Event(11, "a", CdcOperation.Insert, "{\"value\":1}")]);
        Assert.Equal(Row("a", 1), Assert.Single((await reopened.ReadRowsAsync()).Rows));
    }

    [Fact]
    public async Task WriteSnapshotPage_WrongOrdinalDuplicateAndUnorderedKeys_DoNotAdvanceProgress()
    {
        string path = Path.Combine(_root, "order.bin");
        await using var replica = await CdcSnapshotReplica.CreateAsync(path, Descriptor(3));
        await replica.WriteSnapshotPageAsync(0, [Row("b", 1)]);
        CdcSnapshotReplicaState before = replica.GetState();
        await Assert.ThrowsAsync<ArgumentException>(() => replica.WriteSnapshotPageAsync(0, [Row("c", 1)]).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => replica.WriteSnapshotPageAsync(1, [Row("b", 2)]).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => replica.WriteSnapshotPageAsync(1, [Row("d", 1), Row("c", 1)]).AsTask());
        Assert.Equal(before, replica.GetState());
        await replica.WriteSnapshotPageAsync(1, [Row("c", 1), Row("d", 1)]);
        await replica.CompleteSnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => replica.WriteSnapshotPageAsync(3, [Row("e", 1)]).AsTask());
    }

    [Fact]
    public async Task WriteSnapshotPage_TooManyRowsOrInvalidJson_PreservesRecoverableState()
    {
        string path = Path.Combine(_root, "invalid-page.bin");
        await using var replica = await CdcSnapshotReplica.CreateAsync(path, Descriptor(1));
        await Assert.ThrowsAsync<ArgumentException>(() => replica.WriteSnapshotPageAsync(0, [Row("a", 1), Row("b", 1)]).AsTask());
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() =>
            replica.WriteSnapshotPageAsync(0, [new CdcSnapshotRow("a", "{invalid}")]).AsTask());
        Assert.Equal(0, replica.GetState().SnapshotRowsCopied);
        await replica.WriteSnapshotPageAsync(0, [Row("a", 1)]);
        await replica.CompleteSnapshotAsync();
    }

    [Fact]
    public async Task Cancellation_PreCancelledOperations_DoNotCreateOrMutateState()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        string absentPath = Path.Combine(_root, "absent.bin");
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CdcSnapshotReplica.CreateAsync(absentPath, Descriptor(1), cancellationToken: cancelled.Token).AsTask());
        Assert.False(File.Exists(absentPath));
        string path = Path.Combine(_root, "cancel.bin");
        await using var replica = await CdcSnapshotReplica.CreateAsync(path, Descriptor(1));
        byte[] initial = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            replica.WriteSnapshotPageAsync(0, [Row("a", 1)], cancelled.Token).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => replica.CompleteSnapshotAsync(cancelled.Token).AsTask());
        Assert.Equal(initial, await File.ReadAllBytesAsync(path));
        await replica.WriteSnapshotPageAsync(0, [Row("a", 1)]);
        await replica.CompleteSnapshotAsync();
        CdcSnapshotReplicaState before = replica.GetState();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            replica.ApplyIncrementalAsync([Event(11, "a", CdcOperation.Update, "{}")], cancelled.Token).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => replica.ReadRowsAsync(cancellationToken: cancelled.Token).AsTask());
        Assert.Equal(before, replica.GetState());
    }

    [Fact]
    public async Task WriteSnapshotPage_CancelledDuringEnumeration_ReopensAtPreviousOrdinal()
    {
        string path = Path.Combine(_root, "mid-page-cancel.bin");
        CdcSnapshotDescriptor descriptor = Descriptor(2);
        using var cancelled = new CancellationTokenSource();
        await using (var replica = await CdcSnapshotReplica.CreateAsync(path, descriptor))
        {
            var rows = new CancellingList<CdcSnapshotRow>([Row("a", 1), Row("b", 1)], cancelled);
            await Assert.ThrowsAsync<OperationCanceledException>(() => replica.WriteSnapshotPageAsync(0, rows, cancelled.Token).AsTask());
            Assert.Equal(0, replica.GetState().SnapshotRowsCopied);
        }
        await using var reopened = CdcSnapshotReplica.Open(path, descriptor);
        Assert.Equal(0, reopened.GetState().SnapshotRowsCopied);
        await reopened.WriteSnapshotPageAsync(0, [Row("a", 1), Row("b", 1)]);
        await reopened.CompleteSnapshotAsync();
    }

    [Fact]
    public async Task ApplyIncremental_CancelledDuringBatch_DoesNotAdvanceRowsOrCheckpoint()
    {
        string path = Path.Combine(_root, "mid-batch-cancel.bin");
        CdcSnapshotDescriptor descriptor = Descriptor(0);
        using var cancelled = new CancellationTokenSource();
        await using (var replica = await CdcSnapshotReplica.CreateAsync(path, descriptor))
        {
            await replica.CompleteSnapshotAsync();
            var events = new CancellingList<CdcEvent>([
                Event(11, "a", CdcOperation.Insert, "{}"),
                Event(12, "b", CdcOperation.Insert, "{}")], cancelled);
            await Assert.ThrowsAsync<OperationCanceledException>(() => replica.ApplyIncrementalAsync(events, cancelled.Token).AsTask());
            Assert.Empty((await replica.ReadRowsAsync()).Rows);
        }
        await using var reopened = CdcSnapshotReplica.Open(path, descriptor);
        Assert.Equal(descriptor.Checkpoint, reopened.GetState().AppliedCheckpoint);
        Assert.Empty((await reopened.ReadRowsAsync()).Rows);
    }

    [Fact]
    public async Task ApplyIncremental_IdentitySchemaPartitionAndOffsetMismatch_FailAtomically()
    {
        string path = Path.Combine(_root, "identity.bin");
        await using var replica = await CdcSnapshotReplica.CreateAsync(path, Descriptor(0));
        await replica.CompleteSnapshotAsync();
        CdcEvent valid = Event(11, "a", CdcOperation.Insert, "{}");
        CdcEvent[] invalid = [
            valid with { Source = "other-source" },
            valid with { Entity = "other-entity" },
            valid with { Metadata = valid.Metadata with { Schema = "other-schema" } },
            valid with { Metadata = valid.Metadata with { Checkpoint = new CdcCheckpoint(8, 11) } },
            Event(12, "a", CdcOperation.Insert, "{}"),
            Event(10, "a", CdcOperation.Insert, "{}"),
            valid with { AfterJson = null },
        ];
        foreach (CdcEvent value in invalid)
            await Assert.ThrowsAsync<ArgumentException>(() => replica.ApplyIncrementalAsync([value]).AsTask());
        await Assert.ThrowsAsync<CdcUnsupportedSchemaVersionException>(() =>
            replica.ApplyIncrementalAsync([valid with { Metadata = valid.Metadata with { SchemaVersion = 2 } }]).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => replica.ApplyIncrementalAsync([valid, Event(13, "b", CdcOperation.Insert, "{}")]).AsTask());
        Assert.Equal(new CdcCheckpoint(7, 10), replica.GetState().AppliedCheckpoint);
        Assert.Empty((await replica.ReadRowsAsync()).Rows);
    }

    [Fact]
    public async Task ApplyIncremental_LastIdenticalEventRetryAndNextEvent_AreDeterministicAfterReopen()
    {
        string path = Path.Combine(_root, "retry.bin");
        CdcSnapshotDescriptor descriptor = Descriptor(0);
        CdcEvent first = Event(11, "a", CdcOperation.Insert, "{\"value\":1}");
        await using (var replica = await CdcSnapshotReplica.CreateAsync(path, descriptor))
        {
            await replica.CompleteSnapshotAsync();
            await replica.ApplyIncrementalAsync([first]);
        }
        await using var reopened = CdcSnapshotReplica.Open(path, descriptor);
        byte[] before = await File.ReadAllBytesAsync(path);
        await reopened.ApplyIncrementalAsync([first]);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reopened.ApplyIncrementalAsync([first with { AfterJson = "{\"value\":2}" }]).AsTask());
        await reopened.ApplyIncrementalAsync([first, Event(12, "a", CdcOperation.Update, "{\"value\":3}")]);
        Assert.Equal(Row("a", 3), Assert.Single((await reopened.ReadRowsAsync()).Rows));
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.ApplyIncrementalAsync([first]).AsTask());
    }

    [Fact]
    public async Task ApplyIncremental_MaximumCheckpoint_ReopensAndRetriesWithoutOverflow()
    {
        string path = Path.Combine(_root, "maximum-checkpoint.bin");
        CdcSnapshotDescriptor descriptor = Descriptor(0) with { Checkpoint = new CdcCheckpoint(7, long.MaxValue - 1) };
        CdcEvent final = Event(long.MaxValue, "a", CdcOperation.Insert, "{}");
        await using (var replica = await CdcSnapshotReplica.CreateAsync(path, descriptor))
        {
            await replica.CompleteSnapshotAsync();
            await replica.ApplyIncrementalAsync([final]);
        }
        await using var reopened = CdcSnapshotReplica.Open(path, descriptor);
        Assert.Equal(long.MaxValue, reopened.GetState().AppliedCheckpoint.Offset);
        await reopened.ApplyIncrementalAsync([final]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reopened.ApplyIncrementalAsync([final with { AfterJson = "{\"different\":true}" }]).AsTask());
        Assert.Single((await reopened.ReadRowsAsync()).Rows);
    }

    [Fact]
    public async Task Capacity_RowLimitRejectsEntireBatch_AndDeleteReleasesCapacity()
    {
        string path = Path.Combine(_root, "row-capacity.bin");
        var options = new CdcSnapshotReplicaOptions { MaxRows = 2, MaxBatchRows = 2 };
        await using var replica = await CdcSnapshotReplica.CreateAsync(path, Descriptor(2), options);
        await replica.WriteSnapshotPageAsync(0, [Row("a", 1), Row("b", 1)]);
        await replica.CompleteSnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            replica.ApplyIncrementalAsync([Event(11, "a", CdcOperation.Update, "{\"value\":2}"), Event(12, "c", CdcOperation.Insert, "{}")]).AsTask());
        Assert.Equal(new CdcCheckpoint(7, 10), replica.GetState().AppliedCheckpoint);
        Assert.Equal([Row("a", 1), Row("b", 1)], (await replica.ReadRowsAsync()).Rows);
        await replica.ApplyIncrementalAsync([Event(11, "a", CdcOperation.Delete), Event(12, "c", CdcOperation.Insert, "{}")]);
        Assert.Equal(["b", "c"], (await replica.ReadRowsAsync()).Rows.Select(static row => row.Key));
    }

    [Fact]
    public async Task Capacity_StateByteLimitDoesNotPublishPartialSnapshot()
    {
        string path = Path.Combine(_root, "byte-capacity.bin");
        var options = new CdcSnapshotReplicaOptions { MaxRows = 2, MaxBatchRows = 2, MaxBytes = 1400, MaxBatchBytes = 1400 };
        CdcSnapshotDescriptor descriptor = Descriptor(1);
        await using (var replica = await CdcSnapshotReplica.CreateAsync(path, descriptor, options))
        {
            byte[] before = await File.ReadAllBytesAsync(path);
            string escaped = "{\"text\":\"" + string.Concat(Enumerable.Repeat("\\u0061", 160)) + "\"}";
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                replica.WriteSnapshotPageAsync(0, [new CdcSnapshotRow("a", escaped)]).AsTask());
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.Equal(0, replica.GetState().SnapshotRowsCopied);
        }
        await using var reopened = CdcSnapshotReplica.Open(path, descriptor, options);
        await reopened.WriteSnapshotPageAsync(0, [Row("a", 1)]);
        await reopened.CompleteSnapshotAsync();
    }

    [Fact]
    public async Task Capacity_BatchRowAndByteLimits_DoNotAdvanceProgress()
    {
        string path = Path.Combine(_root, "batch-capacity.bin");
        var options = new CdcSnapshotReplicaOptions { MaxRows = 2, MaxBatchRows = 1, MaxBatchBytes = 64 };
        await using var replica = await CdcSnapshotReplica.CreateAsync(path, Descriptor(1), options);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => replica.WriteSnapshotPageAsync(0, [Row("a", 1), Row("b", 1)]).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            replica.WriteSnapshotPageAsync(0, [new CdcSnapshotRow("a", "\"" + new string('x', 64) + "\"")]).AsTask());
        Assert.Equal(0, replica.GetState().SnapshotRowsCopied);
        await replica.WriteSnapshotPageAsync(0, [Row("a", 1)]);
        await replica.CompleteSnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => replica.ApplyIncrementalAsync([Event(11, "a", CdcOperation.Delete)]).AsTask());
        Assert.Equal(new CdcCheckpoint(7, 10), replica.GetState().AppliedCheckpoint);
    }

    [Fact]
    public async Task ReadRows_BoundedPaginationAndTooSmallFirstRow_UseStableKeysAndCheckpoint()
    {
        string path = Path.Combine(_root, "read.bin");
        await using var replica = await CdcSnapshotReplica.CreateAsync(path, Descriptor(3));
        await replica.WriteSnapshotPageAsync(0, [Row("a", 1), Row("b", 1), Row("c", 1)]);
        await replica.CompleteSnapshotAsync();
        CdcSnapshotRowBatch first = await replica.ReadRowsAsync(maxRows: 1);
        Assert.Equal(Row("a", 1), Assert.Single(first.Rows));
        Assert.True(first.HasMore);
        CdcSnapshotRowBatch second = await replica.ReadRowsAsync(first.NextKey, maxRows: 1);
        Assert.Equal(Row("b", 1), Assert.Single(second.Rows));
        CdcSnapshotRowBatch last = await replica.ReadRowsAsync(second.NextKey, maxRows: 1);
        Assert.Equal(Row("c", 1), Assert.Single(last.Rows));
        Assert.False(last.HasMore);
        Assert.Equal(first.AppliedCheckpoint, last.AppliedCheckpoint);
        Assert.Empty((await replica.ReadRowsAsync(last.NextKey)).Rows);
        await Assert.ThrowsAsync<InvalidOperationException>(() => replica.ReadRowsAsync(maxBytes: 1).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => replica.ReadRowsAsync(maxRows: 1001).AsTask());
    }

    [Fact]
    public async Task Open_MismatchedSnapshotIdentitySchemaAndCheckpoint_RejectsAndReleasesLease()
    {
        string path = Path.Combine(_root, "open-identity.bin");
        CdcSnapshotDescriptor descriptor = Descriptor(1);
        await using (var replica = await CdcSnapshotReplica.CreateAsync(path, descriptor))
            await replica.WriteSnapshotPageAsync(0, [Row("a", 1)]);
        CdcSnapshotDescriptor[] invalid = [
            descriptor with { SnapshotId = "different-view" },
            descriptor with { Source = "different-source" },
            descriptor with { Entity = "different-entity" },
            descriptor with { Schema = "different-schema" },
            descriptor with { RowCount = 2 },
            descriptor with { Checkpoint = new CdcCheckpoint(7, 11) },
            descriptor with { Checkpoint = new CdcCheckpoint(8, 10) },
        ];
        foreach (CdcSnapshotDescriptor expected in invalid)
            Assert.Throws<InvalidDataException>(() => CdcSnapshotReplica.Open(path, expected));
        Assert.Throws<CdcUnsupportedSchemaVersionException>(() => CdcSnapshotReplica.Open(path, descriptor with { SchemaVersion = 2 }));
        await using var reopened = CdcSnapshotReplica.Open(path, descriptor);
        Assert.Equal(1, reopened.GetState().SnapshotRowsCopied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_CorruptOrTruncatedState_FailsClosed(bool truncate)
    {
        string path = Path.Combine(_root, "corrupt.bin");
        CdcSnapshotDescriptor descriptor = Descriptor(1);
        await using (var replica = await CdcSnapshotReplica.CreateAsync(path, descriptor))
            await replica.WriteSnapshotPageAsync(0, [Row("a", 1)]);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        if (truncate)
            bytes = bytes[..^1];
        else
            bytes[^1] ^= 0x7F;
        await File.WriteAllBytesAsync(path, bytes);
        Assert.Throws<InvalidDataException>(() => CdcSnapshotReplica.Open(path, descriptor));
        Assert.Throws<InvalidDataException>(() => CdcSnapshotReplica.Open(path, descriptor));
    }

    [Fact]
    public async Task Open_MissingState_DoesNotReplaceCommittedSnapshotWithEmptyState()
    {
        string path = Path.Combine(_root, "missing.bin");
        CdcSnapshotDescriptor descriptor = Descriptor(0);
        await using (var replica = await CdcSnapshotReplica.CreateAsync(path, descriptor))
            await replica.CompleteSnapshotAsync();
        File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => CdcSnapshotReplica.Open(path, descriptor));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CreateAndOpen_ExistingFileAndSecondWriter_RejectWithoutChangingData()
    {
        string path = Path.Combine(_root, "lease.bin");
        CdcSnapshotDescriptor descriptor = Descriptor(0);
        await using (var first = await CdcSnapshotReplica.CreateAsync(path, descriptor))
        {
            Assert.ThrowsAny<IOException>(() => CdcSnapshotReplica.Open(path, descriptor));
            await Assert.ThrowsAnyAsync<IOException>(() => CdcSnapshotReplica.CreateAsync(path, descriptor).AsTask());
            await first.CompleteSnapshotAsync();
        }
        byte[] before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<IOException>(() => CdcSnapshotReplica.CreateAsync(path, descriptor).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        await using var reopened = CdcSnapshotReplica.Open(path, descriptor);
        Assert.Equal(CdcSnapshotPhase.Incremental, reopened.GetState().Phase);
    }

    private static CdcSnapshotDescriptor Descriptor(long rows)
        => new("fixed-view-1", "test-source", "documents", "documents", 1, new CdcCheckpoint(7, 10), rows);

    private static CdcSnapshotRow Row(string key, int value) => new(key, $"{{\"value\":{value}}}");

    private static CdcEvent Event(long offset, string key, CdcOperation operation, string? after = null)
        => new($"event-{offset}", "test-source", "documents", key, offset,
            new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
            new CdcEventMetadata(1, "documents", 1, operation, new CdcCheckpoint(7, offset)), null, after);

    private sealed class CancellingList<T>(IReadOnlyList<T> items, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => items.Count;
        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator()
        {
            yield return items[0];
            cancellation.Cancel();
            for (int index = 1; index < items.Count; index++)
                yield return items[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
