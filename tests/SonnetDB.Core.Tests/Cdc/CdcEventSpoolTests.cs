using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Cdc;

namespace SonnetDB.Core.Tests.Cdc;

public sealed class CdcEventSpoolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-cdc-spool-" + Guid.NewGuid().ToString("N"));

    public CdcEventSpoolTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task AppendReplay_ReopenAndPartitionCheckpoint_AreDeterministic()
    {
        string path = Path.Combine(_root, "events.log");
        await using (var spool = new CdcEventSpool(path))
        {
            await spool.AppendAsync(CreateEvent(1, 10, "a"));
            await spool.AppendAsync(CreateEvent(2, 3, "b"));
            await spool.AppendAsync(CreateEvent(1, 11, "c"));

            IReadOnlyList<CdcEvent> all = await spool.ReplayAsync();
            Assert.Equal(["a", "b", "c"], all.Select(static value => value.Key));

            IReadOnlyList<CdcEvent> partition = await spool.ReplayAsync(
                new CdcCheckpoint(1, 10));
            Assert.Single(partition);
            Assert.Equal("c", partition[0].Key);
        }

        await using var reopened = new CdcEventSpool(path);
        Assert.Equal(3, reopened.EventCount);
        Assert.Equal(
            ["a", "b", "c"],
            (await reopened.ReplayAsync()).Select(static value => value.Key));
    }

    [Fact]
    public async Task Acknowledge_TruncatesFramesAndPersistsAcrossReopen()
    {
        string path = Path.Combine(_root, "ack.log");
        long originalBytes;
        await using (var spool = new CdcEventSpool(path))
        {
            await spool.AppendAsync(CreateEvent(7, 1, "one"));
            await spool.AppendAsync(CreateEvent(7, 2, "two"));
            await spool.AppendAsync(CreateEvent(7, 3, "three"));
            originalBytes = spool.StoredBytes;

            await spool.AcknowledgeAsync(new CdcCheckpoint(7, 2));

            Assert.Equal(1, spool.EventCount);
            Assert.True(spool.StoredBytes < originalBytes);
            Assert.Equal(2, spool.AcknowledgedCheckpoints[7]);
            Assert.Equal("three", Assert.Single(await spool.ReplayAsync()).Key);
        }

        await using var reopened = new CdcEventSpool(path);
        Assert.Equal(1, reopened.EventCount);
        Assert.Equal(2, reopened.AcknowledgedCheckpoints[7]);
        await reopened.AppendAsync(CreateEvent(7, 4, "four"));
        Assert.Equal(
            ["three", "four"],
            (await reopened.ReplayAsync()).Select(static value => value.Key));
    }

    [Fact]
    public async Task Capacity_IsBoundedAndAckReleasesSpace()
    {
        string path = Path.Combine(_root, "capacity.log");
        CdcEvent first = CreateEvent(1, 1, "first");
        CdcEvent second = CreateEvent(1, 2, "second");
        CdcEvent third = CreateEvent(1, 3, "third");
        int maxBytes = 2 * CdcEventSpool.FrameHeaderSize
            + CdcEventCodec.Encode(first).Length
            + CdcEventCodec.Encode(second).Length;
        var options = new CdcEventSpoolOptions
        {
            MaxEvents = 2,
            MaxBytes = maxBytes,
            MaxReplayEvents = 2,
            MaxReplayBytes = maxBytes,
        };

        await using var spool = new CdcEventSpool(path, options);
        await spool.AppendAsync(first);
        await spool.AppendAsync(second);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => spool.AppendAsync(third).AsTask());

        await spool.AcknowledgeAsync(new CdcCheckpoint(1, 1));
        await spool.AppendAsync(third);
        Assert.Equal(["second", "third"], (await spool.ReplayAsync()).Select(static value => value.Key));
    }

    [Fact]
    public async Task CorruptOrTruncatedFrame_FailsClosedOnReopen()
    {
        string corruptPath = Path.Combine(_root, "corrupt.log");
        await using (var spool = new CdcEventSpool(corruptPath))
            await spool.AppendAsync(CreateEvent(1, 1, "corrupt"));

        byte[] corrupt = await File.ReadAllBytesAsync(corruptPath);
        corrupt[^1] ^= 0x7F;
        await File.WriteAllBytesAsync(corruptPath, corrupt);
        Assert.Throws<InvalidDataException>(() => new CdcEventSpool(corruptPath));

        string truncatedPath = Path.Combine(_root, "truncated.log");
        await using (var spool = new CdcEventSpool(truncatedPath))
            await spool.AppendAsync(CreateEvent(1, 1, "truncated"));

        byte[] truncated = await File.ReadAllBytesAsync(truncatedPath);
        await File.WriteAllBytesAsync(truncatedPath, truncated[..^1]);
        Assert.Throws<InvalidDataException>(() => new CdcEventSpool(truncatedPath));
    }

    [Fact]
    public async Task CorruptCheckpointMetadata_FailsClosedOnReopen()
    {
        string path = Path.Combine(_root, "metadata.log");
        await using (var spool = new CdcEventSpool(path))
        {
            await spool.AppendAsync(CreateEvent(4, 1, "value"));
            await spool.AcknowledgeAsync(new CdcCheckpoint(4, 1));
        }

        byte[] metadata = await File.ReadAllBytesAsync(path + ".checkpoint");
        BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(8, 4), 99);
        await File.WriteAllBytesAsync(path + ".checkpoint", metadata);
        Assert.Throws<InvalidDataException>(() => new CdcEventSpool(path));
    }

    [Fact]
    public async Task MissingDataFileWithUnacknowledgedCheckpoint_FailsClosedOnReopen()
    {
        string path = Path.Combine(_root, "missing-data.log");
        await using (var spool = new CdcEventSpool(path))
        {
            await spool.AppendAsync(CreateEvent(5, 1, "acknowledged"));
            await spool.AppendAsync(CreateEvent(5, 2, "pending"));
            await spool.AcknowledgeAsync(new CdcCheckpoint(5, 1));
        }

        File.Delete(path);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => new CdcEventSpool(path));
        Assert.Contains("未确认", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_DoesNotAppendOrReplay()
    {
        string path = Path.Combine(_root, "cancel.log");
        await using var spool = new CdcEventSpool(path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => spool.AppendAsync(CreateEvent(1, 1, "cancelled"), cancelled.Token).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => spool.ReplayAsync(cancellationToken: cancelled.Token).AsTask());
        Assert.Equal(0, spool.EventCount);
    }

    [Fact]
    public async Task ConcurrentAppends_SerializeWithoutInterleavingFrames()
    {
        string path = Path.Combine(_root, "concurrent.log");
        var options = new CdcEventSpoolOptions
        {
            MaxPartitions = 32,
            MaxEvents = 32,
            MaxReplayEvents = 32,
            MaxReplayBytes = 4 * 1024 * 1024,
        };
        await using var spool = new CdcEventSpool(path, options);

        Task[] writes = Enumerable.Range(0, 16)
            .Select(index => spool.AppendAsync(CreateEvent(index, 0, $"partition-{index}")).AsTask())
            .ToArray();
        await Task.WhenAll(writes);

        Assert.Equal(16, spool.EventCount);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.Length > 16 * CdcEventSpool.FrameHeaderSize);
        Assert.Equal(16, (await spool.ReplayAsync()).Count);
    }

    [Fact]
    public async Task CheckpointValidation_RejectsUnknownRegressionAndFutureOffsets()
    {
        string path = Path.Combine(_root, "validation.log");
        await using var spool = new CdcEventSpool(path);
        await spool.AppendAsync(CreateEvent(1, 5, "value"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => spool.AcknowledgeAsync(new CdcCheckpoint(2, 1)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => spool.AcknowledgeAsync(new CdcCheckpoint(1, 6)).AsTask());
        await spool.AcknowledgeAsync(new CdcCheckpoint(1, 5));
        await Assert.ThrowsAsync<ArgumentException>(
            () => spool.AcknowledgeAsync(new CdcCheckpoint(1, 4)).AsTask());
    }

    [Fact]
    public async Task ReplayBatch_ReportsBoundedContinuation()
    {
        string path = Path.Combine(_root, "batch.log");
        await using var spool = new CdcEventSpool(path);
        await spool.AppendAsync(CreateEvent(1, 1, "one"));
        await spool.AppendAsync(CreateEvent(1, 2, "two"));

        CdcEventSpoolBatch batch = await spool.ReplayBatchAsync(maxEvents: 1);

        Assert.Single(batch.Events);
        Assert.Equal("one", batch.Events[0].Key);
        Assert.True(batch.HasMore);
    }

    [Fact]
    public async Task MetadataHighWatermark_WithMissingUnacknowledgedFrame_FailsClosed()
    {
        string path = Path.Combine(_root, "high-watermark.log");
        await using (var spool = new CdcEventSpool(path))
        {
            await spool.AppendAsync(CreateEvent(1, 1, "one"));
            await spool.AppendAsync(CreateEvent(1, 2, "two"));
            await spool.AcknowledgeAsync(new CdcCheckpoint(1, 1));
        }

        await File.WriteAllBytesAsync(path, []);
        Assert.Throws<InvalidDataException>(() => new CdcEventSpool(path));
    }

    [Fact]
    public async Task AppendAfterAcknowledge_PersistsHighWatermarkAndMissingDataFailsClosed()
    {
        string path = Path.Combine(_root, "append-after-ack.log");
        await using (var spool = new CdcEventSpool(path))
        {
            await spool.AppendAsync(CreateEvent(9, 1, "acknowledged"));
            await spool.AcknowledgeAsync(new CdcCheckpoint(9, 1));
            await spool.AppendAsync(CreateEvent(9, 2, "pending"));
        }

        await File.WriteAllBytesAsync(path, []);
        Assert.Throws<InvalidDataException>(() => new CdcEventSpool(path));
    }

    [Fact]
    public async Task LegacyMetadataV1_WithPendingFrame_RemainsReadable()
    {
        string path = Path.Combine(_root, "legacy-metadata.log");
        await using (var spool = new CdcEventSpool(path))
        {
            await spool.AppendAsync(CreateEvent(10, 1, "acknowledged"));
            await spool.AppendAsync(CreateEvent(10, 2, "pending"));
            await spool.AcknowledgeAsync(new CdcCheckpoint(10, 1));
        }

        string metadataPath = path + ".checkpoint";
        byte[] metadata = await File.ReadAllBytesAsync(metadataPath);
        BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(8, 4), 1);
        uint crc = Crc32.HashToUInt32(metadata.AsSpan(0, metadata.Length - sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            metadata.AsSpan(metadata.Length - sizeof(uint)),
            crc);
        await File.WriteAllBytesAsync(metadataPath, metadata);

        await using var reopened = new CdcEventSpool(path);
        Assert.Equal("pending", Assert.Single(await reopened.ReplayAsync()).Key);
    }

    [Fact]
    public void Open_SamePathTwice_RejectsSecondWriterLease()
    {
        string path = Path.Combine(_root, "lease.log");
        using var first = new CdcEventSpool(path);
        Assert.ThrowsAny<IOException>(() => new CdcEventSpool(path));
    }

    private static CdcEvent CreateEvent(long partition, long offset, string key)
        => new(
            $"event-{partition}-{offset}",
            "test-source",
            "documents",
            key,
            offset,
            new DateTimeOffset(2026, 9, 22, 8, 30, 0, TimeSpan.Zero),
            new CdcEventMetadata(
                CdcEventCodec.CurrentContractVersion,
                "documents",
                CdcEventCodec.CurrentSchemaVersion,
                CdcOperation.Update,
                new CdcCheckpoint(partition, offset)),
            "{\"before\":true}",
            "{\"after\":true}");
}
