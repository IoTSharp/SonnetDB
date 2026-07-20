using System.Buffers.Binary;
using System.IO.Hashing;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvAtomicBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-kv-atomic-batch-tests",
        Guid.NewGuid().ToString("N"));

    public KvAtomicBatchTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ApplyBatch_MixedPutDelete_SyncsOnceAndRecoversAllMutations()
    {
        var options = Options();
        using var keyspace = KvKeyspace.Open("mixed", _root, options);
        keyspace.Put("removed", [1]);
        long previousSequence = keyspace.LastSequence;
        int syncCount = 0;
        keyspace.WalSyncTestHook = () => syncCount++;

        long batchSequence = keyspace.ApplyBatch(
        [
            KvBatchMutation.Put("first"u8.ToArray(), [2]),
            KvBatchMutation.Delete("removed"u8.ToArray()),
            KvBatchMutation.Put("second"u8.ToArray(), [3]),
        ]);

        Assert.Equal(previousSequence + 1, batchSequence);
        Assert.Equal(1, syncCount);
        KvWalRecord record = KvWalFile.Replay(KvKeyspace.ActiveWalPath(_root)).Last();
        Assert.Equal(KvWalRecordKind.MutationBatch, record.Kind);
        Assert.Equal(3, KvWalFile.DecodeMutationBatch(record).Count);

        keyspace.WalSyncTestHook = null;
        keyspace.Dispose();

        using var recovered = KvKeyspace.Open("mixed", _root, options);
        Assert.Equal([2], recovered.Get("first"));
        Assert.Null(recovered.Get("removed"));
        Assert.Equal([3], recovered.Get("second"));
        Assert.Equal(batchSequence, recovered.LastSequence);
    }

    [Fact]
    public void ApplyBatch_OverlayBudget_UsesFinalCanonicalKeySet()
    {
        using var keyspace = KvKeyspace.Open("overlay-final-set", _root, OverlayBudgetOptions(4));
        keyspace.Put("overwrite", [1]);
        keyspace.Put("remove", [2]);
        long previousSequence = keyspace.LastSequence;

        long batchSequence = keyspace.ApplyBatch(
        [
            KvBatchMutation.Put("overwrite"u8.ToArray(), [3]),
            KvBatchMutation.Delete("remove"u8.ToArray()),
            KvBatchMutation.Put("new"u8.ToArray(), [4]),
            KvBatchMutation.Put("transient"u8.ToArray(), [5]),
            KvBatchMutation.Delete("transient"u8.ToArray()),
        ]);

        Assert.Equal(previousSequence + 1, batchSequence);
        Assert.Equal([3], keyspace.Get("overwrite"));
        Assert.Null(keyspace.Get("remove"));
        Assert.Equal([4], keyspace.Get("new"));
        Assert.Null(keyspace.Get("transient"));
        Assert.Equal(2, keyspace.MutableOverlayEntryCount);

        keyspace.SyncWalForMaintenance();
        KvWalRecord record = KvWalFile.Replay(KvKeyspace.ActiveWalPath(_root)).Last();
        Assert.Equal(KvWalRecordKind.MutationBatch, record.Kind);
        Assert.Equal(4, KvWalFile.DecodeMutationBatch(record).Count);
    }

    [Fact]
    public void ApplyBatch_OverlayBudget_CountsOnlyDeletesThatRequireTombstones()
    {
        using (var preparation = KvKeyspace.Open("overlay-deletes", _root, Options()))
        {
            preparation.Put("durable", [1]);
            preparation.Compact();
        }

        long batchSequence;
        using (var keyspace = KvKeyspace.Open("overlay-deletes", _root, OverlayBudgetOptions(2)))
        {
            batchSequence = keyspace.ApplyBatch(
            [
                KvBatchMutation.Delete("durable"u8.ToArray()),
                KvBatchMutation.Delete("missing:1"u8.ToArray()),
                KvBatchMutation.Delete("missing:2"u8.ToArray()),
            ]);

            Assert.Null(keyspace.Get("durable"));
            Assert.Equal(1, keyspace.MutableOverlayEntryCount);
        }

        using var recovered = KvKeyspace.Open("overlay-deletes", _root, Options());
        Assert.Null(recovered.Get("durable"));
        Assert.Equal(1, recovered.MutableOverlayEntryCount);
        Assert.Equal(batchSequence, recovered.LastSequence);
    }

    [Fact]
    public void ApplyBatch_FinalOverlayAboveBudget_IsRejectedBeforeWalAppend()
    {
        var options = OverlayBudgetOptions(2);
        long previousSequence;
        using (var keyspace = KvKeyspace.Open("overlay-rejection", _root, options))
        {
            keyspace.Put("existing", [1]);
            previousSequence = keyspace.LastSequence;
            long walLength = keyspace.ActiveWalLength;
            long checkpointSchedules = keyspace.AutoCheckpointScheduleCount;

            IOException error = Assert.Throws<IOException>(() => keyspace.ApplyBatch(
            [
                KvBatchMutation.Put("existing"u8.ToArray(), [2]),
                KvBatchMutation.Put("new:1"u8.ToArray(), [3]),
                KvBatchMutation.Put("new:2"u8.ToArray(), [4]),
            ]));

            Assert.Contains("fresh checkpoint budget", error.Message, StringComparison.Ordinal);
            Assert.Equal(previousSequence, keyspace.LastSequence);
            Assert.Equal(walLength, keyspace.ActiveWalLength);
            Assert.Equal(checkpointSchedules, keyspace.AutoCheckpointScheduleCount);
            Assert.Equal([1], keyspace.Get("existing"));
            Assert.Null(keyspace.Get("new:1"));
            Assert.Null(keyspace.Get("new:2"));
        }

        using var recovered = KvKeyspace.Open("overlay-rejection", _root, Options());
        Assert.Equal(previousSequence, recovered.LastSequence);
        Assert.Equal([1], recovered.Get("existing"));
        Assert.Null(recovered.Get("new:1"));
        Assert.Null(recovered.Get("new:2"));
    }

    [Fact]
    public void ApplyBatch_OverlayBudget_CurrentOverlayCanBeCheckpointedAndRetried()
    {
        using var keyspace = KvKeyspace.Open("overlay-retry", _root, OverlayBudgetOptions(2));
        keyspace.Put("existing", [1]);

        IReadOnlyList<KvBatchMutation> batch =
        [
            KvBatchMutation.Put("new:1"u8.ToArray(), [3]),
            KvBatchMutation.Put("new:2"u8.ToArray(), [4]),
        ];
        IOException error = Assert.Throws<IOException>(() => keyspace.ApplyBatch(batch));

        Assert.Contains("current checkpoint budget", error.Message, StringComparison.Ordinal);
        WaitForAutomaticCheckpoint(keyspace);

        keyspace.ApplyBatch(batch);

        Assert.Equal([1], keyspace.Get("existing"));
        Assert.Equal([3], keyspace.Get("new:1"));
        Assert.Equal([4], keyspace.Get("new:2"));
    }

    [Fact]
    public void ApplyBatch_WalBudget_RecordAboveFreshBudget_IsRejectedWithoutCheckpoint()
    {
        IReadOnlyList<KvBatchMutation> batch =
        [
            KvBatchMutation.Put("large"u8.ToArray(), new byte[100]),
        ];
        long maxWalBytes = KvWalFile.HeaderSize
            + KvWalFile.CalculateMutationBatchRecordBytes(batch)
            - 1;
        using var keyspace = KvKeyspace.Open(
            "wal-intrinsic-rejection",
            _root,
            OverlayBudgetOptions(int.MaxValue) with { MaxWalBytes = maxWalBytes });
        keyspace.Put("existing", [1]);
        long walLength = keyspace.ActiveWalLength;
        long checkpointSchedules = keyspace.AutoCheckpointScheduleCount;

        IOException error = Assert.Throws<IOException>(() => keyspace.ApplyBatch(batch));

        Assert.Contains("fresh checkpoint budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.Equal(checkpointSchedules, keyspace.AutoCheckpointScheduleCount);
        Assert.Equal([1], keyspace.Get("existing"));
        Assert.Null(keyspace.Get("large"));
    }

    [Fact]
    public void ApplyBatch_WalBudget_CurrentWalCanBeCheckpointedAndRetried()
    {
        IReadOnlyList<KvBatchMutation> batch =
        [
            KvBatchMutation.Put("small"u8.ToArray(), [2]),
        ];
        long maxWalBytes = KvWalFile.HeaderSize
            + KvWalFile.CalculateMutationBatchRecordBytes(batch);
        using var keyspace = KvKeyspace.Open(
            "wal-retry",
            _root,
            OverlayBudgetOptions(int.MaxValue) with { MaxWalBytes = maxWalBytes });
        keyspace.Put("existing", [1]);

        IOException error = Assert.Throws<IOException>(() => keyspace.ApplyBatch(batch));

        Assert.Contains("current checkpoint budget", error.Message, StringComparison.Ordinal);
        WaitForAutomaticCheckpoint(keyspace);
        keyspace.ApplyBatch(batch);

        Assert.Equal([1], keyspace.Get("existing"));
        Assert.Equal([2], keyspace.Get("small"));
    }

    [Fact]
    public void ApplyBatch_WalBudget_HeaderOnlyLimitIsRejectedWithoutBackpressureWait()
    {
        using var keyspace = KvKeyspace.Open(
            "wal-header-limit",
            _root,
            OverlayBudgetOptions(int.MaxValue) with
            {
                MaxWalBytes = KvWalFile.HeaderSize,
                CheckpointWriteBackpressureTimeout = TimeSpan.FromMilliseconds(100),
            });
        Assert.Equal(0, keyspace.AutoCheckpointScheduleCount);

        IOException error = Assert.Throws<IOException>(() => keyspace.ApplyBatch(
            [KvBatchMutation.Put("key"u8.ToArray(), [1])]));

        Assert.Contains("fresh checkpoint budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(KvWalFile.HeaderSize, keyspace.ActiveWalLength);
        Assert.Equal(0, keyspace.AutoCheckpointScheduleCount);
    }

    [Fact]
    public void DeleteMany_OverlayBudget_CurrentOverlayCanBeCheckpointedAndRetried()
    {
        using (var preparation = KvKeyspace.Open("delete-overlay-retry", _root, Options()))
        {
            preparation.Put("durable:1", [1]);
            preparation.Put("durable:2", [2]);
            preparation.Compact();
        }

        using var keyspace = KvKeyspace.Open(
            "delete-overlay-retry",
            _root,
            OverlayBudgetOptions(2));
        keyspace.Put("unrelated", [9]);

        IOException error = Assert.Throws<IOException>(() =>
            keyspace.DeleteMany(["durable:1", "durable:2"]));

        Assert.Contains("current checkpoint budget", error.Message, StringComparison.Ordinal);
        WaitForAutomaticCheckpoint(keyspace);
        Assert.Equal(2, keyspace.DeleteMany(["durable:1", "durable:2"]));

        Assert.Equal([9], keyspace.Get("unrelated"));
        Assert.Null(keyspace.Get("durable:1"));
        Assert.Null(keyspace.Get("durable:2"));
    }

    [Fact]
    public void DeleteMany_OverlayBudget_AllowsCurrentOnlyKeysToBeRemoved()
    {
        using var keyspace = KvKeyspace.Open("delete-overlay-final-set", _root, OverlayBudgetOptions(3));
        keyspace.Put("remove:1", [1]);
        keyspace.Put("remove:2", [2]);

        Assert.Equal(2, keyspace.DeleteMany(["remove:1", "remove:2"]));

        Assert.Equal(0, keyspace.MutableOverlayEntryCount);
        Assert.Null(keyspace.Get("remove:1"));
        Assert.Null(keyspace.Get("remove:2"));
    }

    [Fact]
    public void DeletePublishPlans_DoNotReadBaseAfterWalSync()
    {
        using (var preparation = KvKeyspace.Open("delete-publish-plan", _root, Options()))
        {
            preparation.Put("batch-delete", [1]);
            preparation.Put("delete-many", [2]);
            preparation.Compact();
        }

        using var keyspace = KvKeyspace.Open("delete-publish-plan", _root, Options());
        bool walSyncStarted = false;
        int baseLookups = 0;
        keyspace.BaseLookupTestHook = () =>
        {
            Assert.False(walSyncStarted);
            baseLookups++;
        };
        keyspace.WalSyncTestHook = () => walSyncStarted = true;

        keyspace.ApplyBatch([KvBatchMutation.Delete("batch-delete"u8.ToArray())]);
        walSyncStarted = false;
        keyspace.DeleteMany(["delete-many"]);

        Assert.Equal(2, baseLookups);
        Assert.Null(keyspace.Get("batch-delete"));
        Assert.Null(keyspace.Get("delete-many"));
    }

    [Fact]
    public void Delete_SyncFailureFaultsKeyspaceAndReopenRecoversCommittedRecord()
    {
        var options = Options();
        using (var keyspace = KvKeyspace.Open("delete-sync-failure", _root, options))
        {
            keyspace.Put("delete", [1]);
            keyspace.WalSyncTestHook = () => throw new InvalidOperationException("simulated delete sync failure");

            Assert.Throws<InvalidOperationException>(() => keyspace.Delete("delete"));
            Assert.Throws<IOException>(() => keyspace.Put("after-failure", [2]));
            keyspace.WalSyncTestHook = null;
        }

        using var recovered = KvKeyspace.Open("delete-sync-failure", _root, options);
        Assert.Null(recovered.Get("delete"));
        Assert.Null(recovered.Get("after-failure"));
    }

    [Fact]
    public void DeleteMany_MultiChunkSyncFailureFaultsKeyspaceAndRecoversWholeBatch()
    {
        var options = Options() with { BatchDeleteMaxKeys = 1 };
        using (var keyspace = KvKeyspace.Open("delete-many-sync-failure", _root, options))
        {
            keyspace.Put("delete:1", [1]);
            keyspace.Put("delete:2", [2]);
            keyspace.Put("delete:3", [3]);
            keyspace.WalSyncTestHook = () => throw new InvalidOperationException("simulated batch delete sync failure");

            Assert.Throws<InvalidOperationException>(() =>
                keyspace.DeleteMany(["delete:1", "delete:2", "delete:3"]));
            Assert.Throws<IOException>(() => keyspace.Put("after-failure", [4]));
            keyspace.WalSyncTestHook = null;
        }

        using var recovered = KvKeyspace.Open("delete-many-sync-failure", _root, options);
        Assert.Null(recovered.Get("delete:1"));
        Assert.Null(recovered.Get("delete:2"));
        Assert.Null(recovered.Get("delete:3"));
        Assert.Null(recovered.Get("after-failure"));
    }

    [Fact]
    public void Open_MutationBatchWithTornTail_IgnoresEntireBatch()
    {
        string walDirectory = KvKeyspace.WalDirectory(_root);
        Directory.CreateDirectory(walDirectory);
        string walPath = KvKeyspace.ActiveWalPath(_root);
        long completeLength;
        using (var wal = KvWalFile.Open(walPath, startSequence: 1, bufferSize: 4096))
        {
            wal.AppendMutationBatch(
            [
                KvBatchMutation.Put("first"u8.ToArray(), [1]),
                KvBatchMutation.Put("second"u8.ToArray(), [2]),
            ]);
            wal.Sync();
            completeLength = wal.Length;
        }

        using (var stream = new FileStream(walPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            stream.SetLength(completeLength - 1);

        using var recovered = KvKeyspace.Open("torn", _root, Options());
        Assert.Null(recovered.Get("first"));
        Assert.Null(recovered.Get("second"));
        Assert.Equal(0, recovered.LastSequence);
        Assert.Empty(KvWalFile.Replay(walPath));
    }

    [Fact]
    public void Open_V2ActiveWal_ReplaysAndRollsToV3ActiveWal()
    {
        string walDirectory = KvKeyspace.WalDirectory(_root);
        Directory.CreateDirectory(walDirectory);
        string walPath = KvKeyspace.ActiveWalPath(_root);
        using (var wal = KvWalFile.Open(walPath, startSequence: 1, bufferSize: 4096))
        {
            wal.AppendPut("legacy"u8, [7]);
            wal.Sync();
        }

        byte[] bytes = File.ReadAllBytes(walPath);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, sizeof(int)), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60, sizeof(uint)), Crc32.HashToUInt32(bytes.AsSpan(0, 60)));
        File.WriteAllBytes(walPath, bytes);

        using var recovered = KvKeyspace.Open("legacy", _root, Options());

        Assert.Equal([7], recovered.Get("legacy"));
        Assert.Equal(3, KvWalFile.ReadHeaderInfo(walPath).Version);
        Assert.Single(Directory.GetFiles(walDirectory, "sealed-*.SDBKVWAL"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static KvOptions Options()
        => KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            ExpirerEnabled = false,
        };

    private static KvOptions OverlayBudgetOptions(int maxOverlayEntries)
        => KvOptions.Default with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = maxOverlayEntries,
            SyncWalOnEveryWrite = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        };

    private static void WaitForAutomaticCheckpoint(KvKeyspace keyspace)
    {
        bool completed = SpinWait.SpinUntil(
            () => keyspace.MutableOverlayEntryCount == 0
                && keyspace.PendingOverlayEntryCount == 0
                && keyspace.ActiveWalLength == KvWalFile.HeaderSize,
            TimeSpan.FromSeconds(10));
        Assert.True(completed, keyspace.LastCheckpointException?.ToString());
        Assert.Null(keyspace.LastCheckpointException);
    }

}
