using System.Buffers.Binary;
using System.IO.Hashing;
using SonnetDB.Engine;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvWalGenerationRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-kv-generation-recovery-" + Guid.NewGuid().ToString("N"));

    public KvWalGenerationRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Open_ClearCommittedOnlyInActiveWal_PersistsRecoveredGenerationBeforeWalCleanup()
    {
        var db = Open();
        var kv = db.Keyspaces.Open("clear-before-meta");
        kv.Put("old", [1]);
        kv.Compact();
        string keyspaceRoot = kv.RootDirectory;
        db.CrashSimulationCloseWal();

        using (var wal = KvWalFile.Open(
            KvKeyspace.ActiveWalPath(keyspaceRoot),
            startSequence: 2,
            bufferSize: 4096))
        {
            Assert.Equal(2, wal.AppendClearGeneration(generation: 1));
            Assert.Equal(3, wal.AppendPut("new"u8, [2]));
            wal.Sync();
        }
        Assert.False(File.Exists(Path.Combine(keyspaceRoot, KvGenerationFile.FileName)));

        using (var recoveredDb = Open())
        {
            var recovered = recoveredDb.Keyspaces.Open("clear-before-meta");
            Assert.Equal(1, recovered.Generation);
            Assert.Null(recovered.Get("old"));
            Assert.Equal([2], recovered.Get("new")!);
            Assert.Equal(1, KvGenerationFile.Load(keyspaceRoot));
            KvGenerationMetadata metadata = KvGenerationFile.LoadMetadata(keyspaceRoot);
            Assert.Equal(2, metadata.Version);
            Assert.Equal(3, metadata.ResetSequence);
            recovered.Compact();
        }

        using var reopened = Open();
        var durable = reopened.Keyspaces.Open("clear-before-meta");
        Assert.Equal(1, durable.Generation);
        Assert.Null(durable.Get("old"));
        Assert.Equal([2], durable.Get("new")!);
    }

    [Fact]
    public void Open_DurableGenerationAheadOfWalBoundary_FailsClosed()
    {
        var db = Open();
        var kv = db.Keyspaces.Open("missing-boundary");
        kv.Put("old", [1]);
        kv.Compact();
        string keyspaceRoot = kv.RootDirectory;
        db.CrashSimulationCloseWal();

        using (var wal = KvWalFile.Open(
            KvKeyspace.ActiveWalPath(keyspaceRoot),
            startSequence: 2,
            bufferSize: 4096))
        {
            Assert.Equal(2, wal.AppendClearGeneration(generation: 1));
            Assert.Equal(3, wal.AppendPut("generation-one"u8, [2]));
            wal.Sync();
        }
        KvGenerationFile.Save(keyspaceRoot, generation: 2);

        using var reopened = Open();
        Assert.Throws<InvalidDataException>(() => reopened.Keyspaces.Open("missing-boundary"));
    }

    [Fact]
    public void Open_DurableGenerationWhoseActiveClearTailIsCorrupt_FailsClosed()
    {
        var db = Open();
        var kv = db.Keyspaces.Open("corrupt-boundary");
        kv.Put("old", [1]);
        kv.Compact();
        string keyspaceRoot = kv.RootDirectory;
        string activeWalPath = KvKeyspace.ActiveWalPath(keyspaceRoot);
        db.CrashSimulationCloseWal();

        using (var wal = KvWalFile.Open(activeWalPath, startSequence: 2, bufferSize: 4096))
        {
            Assert.Equal(2, wal.AppendClearGeneration(generation: 1));
            wal.Sync();
        }
        byte[] bytes = File.ReadAllBytes(activeWalPath);
        bytes[^1] ^= 0x5A;
        File.WriteAllBytes(activeWalPath, bytes);
        KvGenerationFile.Save(keyspaceRoot, generation: 1);

        using var reopened = Open();
        Assert.Throws<InvalidDataException>(() => reopened.Keyspaces.Open("corrupt-boundary"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_LegacyResetAfterClearLayout_UsesPostMetadataWalHeaderAsBoundary(bool writeAfterClear)
    {
        var db = Open();
        var kv = db.Keyspaces.Open("legacy-reset");
        kv.Put("old", [1]);
        kv.Compact();
        string keyspaceRoot = kv.RootDirectory;
        string activeWalPath = KvKeyspace.ActiveWalPath(keyspaceRoot);
        db.CrashSimulationCloseWal();

        KvGenerationFile.Save(keyspaceRoot, generation: 1);
        Thread.Sleep(20);
        File.Delete(activeWalPath);
        using (var resetWal = KvWalFile.Open(activeWalPath, startSequence: 3, bufferSize: 4096))
        {
            if (writeAfterClear)
                Assert.Equal(3, resetWal.AppendPut("new"u8, [2]));
            resetWal.Sync();
        }

        using var reopened = Open();
        var restored = reopened.Keyspaces.Open("legacy-reset");
        Assert.Equal(1, restored.Generation);
        Assert.Null(restored.Get("old"));
        Assert.Equal(writeAfterClear ? 3 : 2, restored.LastSequence);
        if (writeAfterClear)
            Assert.Equal([2], restored.Get("new")!);
    }

    [Fact]
    public void Open_LegacyResetWalWhoseRecordsRestartedAtOne_RemainsCompatible()
    {
        var db = Open();
        var kv = db.Keyspaces.Open("legacy-sequence-reset");
        kv.Put("old", [1]);
        kv.Compact();
        string keyspaceRoot = kv.RootDirectory;
        string activeWalPath = KvKeyspace.ActiveWalPath(keyspaceRoot);
        db.CrashSimulationCloseWal();

        KvGenerationFile.Save(keyspaceRoot, generation: 1);
        Thread.Sleep(20);
        File.Delete(activeWalPath);
        using (var resetWal = KvWalFile.Open(activeWalPath, startSequence: 3, bufferSize: 4096))
            resetWal.Sync();

        string sequenceOneWalPath = Path.Combine(keyspaceRoot, "sequence-one.tmp");
        using (var sequenceOneWal = KvWalFile.Open(sequenceOneWalPath, startSequence: 1, bufferSize: 4096))
        {
            Assert.Equal(1, sequenceOneWal.AppendPut("new"u8, [2]));
            sequenceOneWal.Sync();
        }
        byte[] resetHeader = File.ReadAllBytes(activeWalPath);
        byte[] sequenceOneWalBytes = File.ReadAllBytes(sequenceOneWalPath);
        Array.Resize(ref resetHeader, 64 + sequenceOneWalBytes.Length - 64);
        sequenceOneWalBytes.AsSpan(64).CopyTo(resetHeader.AsSpan(64));
        File.WriteAllBytes(activeWalPath, resetHeader);
        File.Delete(sequenceOneWalPath);

        using var reopened = Open();
        var restored = reopened.Keyspaces.Open("legacy-sequence-reset");
        Assert.Equal(1, restored.Generation);
        Assert.Null(restored.Get("old"));
        Assert.Equal([2], restored.Get("new")!);
        Assert.Equal(2, restored.LastSequence);
    }

    [Fact]
    public void Clear_Reopen_DoesNotReusePreClearVersionForCas()
    {
        long oldVersion;
        using (var db = Open())
        {
            var kv = db.Keyspaces.Open("clear-cas-version");
            oldVersion = kv.Put("relationship", [1]);
            kv.Clear();
        }

        using var reopened = Open();
        var restored = reopened.Keyspaces.Open("clear-cas-version");
        long newVersion = restored.Put("relationship", [2]);
        KvCasResult staleCas = restored.CompareAndSet("relationship", oldVersion, [3]);

        Assert.True(newVersion > oldVersion);
        Assert.False(staleCas.Succeeded);
        Assert.Equal([2], restored.Get("relationship")!);
    }

    [Fact]
    public void Open_V2ResetSequenceDoesNotDependOnWallClockOrdering()
    {
        string keyspaceRoot;
        string activeWalPath;
        KvGenerationMetadata metadata;
        using (var db = Open())
        {
            var kv = db.Keyspaces.Open("v2-clock-rollback");
            kv.Put("old", [1]);
            kv.Clear();
            keyspaceRoot = kv.RootDirectory;
            activeWalPath = KvKeyspace.ActiveWalPath(keyspaceRoot);
            metadata = KvGenerationFile.LoadMetadata(keyspaceRoot);
        }

        Assert.Equal(2, metadata.Version);
        Assert.True(metadata.ResetSequence > 0);
        byte[] header = File.ReadAllBytes(activeWalPath);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(16, sizeof(long)),
            metadata.CreatedUtcTicks - TimeSpan.TicksPerHour);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(60, sizeof(uint)),
            Crc32.HashToUInt32(header.AsSpan(0, 60)));
        File.WriteAllBytes(activeWalPath, header);

        using var reopened = Open();
        var restored = reopened.Keyspaces.Open("v2-clock-rollback");
        Assert.Equal(1, restored.Generation);
        Assert.Null(restored.Get("old"));
        Assert.Equal(metadata.ResetSequence - 1, restored.LastSequence);
    }

    [Fact]
    public void Open_LegacyResetBoundary_IsUpgradedToV2BeforeSubsequentClockRollback()
    {
        string keyspaceRoot;
        string activeWalPath;
        using (var db = Open())
        {
            var kv = db.Keyspaces.Open("legacy-upgrade");
            kv.Put("old", [1]);
            kv.Compact();
            keyspaceRoot = kv.RootDirectory;
            activeWalPath = KvKeyspace.ActiveWalPath(keyspaceRoot);
            db.CrashSimulationCloseWal();
        }

        KvGenerationFile.Save(keyspaceRoot, generation: 1);
        Thread.Sleep(20);
        File.Delete(activeWalPath);
        using (var resetWal = KvWalFile.Open(activeWalPath, startSequence: 3, bufferSize: 4096))
            resetWal.Sync();

        using (var firstReopen = Open())
        {
            var restored = firstReopen.Keyspaces.Open("legacy-upgrade");
            Assert.Null(restored.Get("old"));
        }

        KvGenerationMetadata upgraded = KvGenerationFile.LoadMetadata(keyspaceRoot);
        Assert.Equal(2, upgraded.Version);
        Assert.Equal(3, upgraded.ResetSequence);

        byte[] header = File.ReadAllBytes(activeWalPath);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(16, sizeof(long)),
            upgraded.CreatedUtcTicks - TimeSpan.TicksPerHour);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(60, sizeof(uint)),
            Crc32.HashToUInt32(header.AsSpan(0, 60)));
        File.WriteAllBytes(activeWalPath, header);

        using var secondReopen = Open();
        var durable = secondReopen.Keyspaces.Open("legacy-upgrade");
        Assert.Equal(1, durable.Generation);
        Assert.Null(durable.Get("old"));
        Assert.Equal(2, durable.LastSequence);
    }

    [Fact]
    public void Open_PreMetadataWalWithoutDurableClearBoundary_IsNotMistakenForLegacyReset()
    {
        var db = Open();
        var kv = db.Keyspaces.Open("pre-metadata-wal");
        kv.Put("old-state", [1]);
        kv.Compact();
        string keyspaceRoot = kv.RootDirectory;
        string activeWalPath = KvKeyspace.ActiveWalPath(keyspaceRoot);
        db.CrashSimulationCloseWal();

        using (var oldWal = KvWalFile.Open(activeWalPath, startSequence: 2, bufferSize: 4096))
        {
            Assert.Equal(2, oldWal.AppendPut("old-wal"u8, [2]));
            oldWal.Sync();
        }
        Thread.Sleep(20);
        KvGenerationFile.Save(keyspaceRoot, generation: 1);

        using var reopened = Open();
        Assert.Throws<InvalidDataException>(() => reopened.Keyspaces.Open("pre-metadata-wal"));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        Kv = KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        },
    });
}
