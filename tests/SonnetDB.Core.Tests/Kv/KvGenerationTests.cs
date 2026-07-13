using System.Text;
using SonnetDB.Engine;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvGenerationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-kv-generation-" + Guid.NewGuid().ToString("N"));

    public KvGenerationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void DeleteMany_WritesOneBatchRecordAndRecoversAtomically()
    {
        string walPath;
        var db = Open();
        var kv = db.Keyspaces.Open("cache");
        kv.Put("k:1", [1]);
        kv.Put("k:2", [2]);
        kv.Put("k:3", [3]);
        kv.Compact();

        Assert.Equal(3, kv.DeleteMany(["k:1", "k:2", "k:3", "k:3"]));
        walPath = KvKeyspace.ActiveWalPath(kv.RootDirectory);
        var records = KvWalFile.Replay(walPath).ToArray();
        Assert.Equal(2, records.Length);
        Assert.Equal(KvWalRecordKind.DeleteBatch, records[0].Kind);
        Assert.Equal(3, KvWalFile.DecodeDeleteBatch(records[0]).Keys.Count);
        Assert.Equal(KvWalRecordKind.DeleteBatchCommit, records[1].Kind);
        db.CrashSimulationCloseWal();

        using var reopened = Open();
        var restored = reopened.Keyspaces.Open("cache");
        Assert.Equal(0, restored.Count);
        Assert.Null(restored.Get("k:1"));
        Assert.Null(restored.Get("k:2"));
        Assert.Null(restored.Get("k:3"));
    }

    [Fact]
    public void Replay_IncompleteDeleteBatch_DoesNotPublishAnyTombstone()
    {
        string keyspaceRoot = Path.Combine(_root, "kv", "keyspaces", "cache");
        string walPath = KvKeyspace.ActiveWalPath(keyspaceRoot);
        using (var wal = KvWalFile.Open(walPath, startSequence: 1, bufferSize: 4096))
        {
            wal.AppendPut("k:1"u8, [1]);
            wal.AppendPut("k:2"u8, [2]);
            wal.AppendDeleteBatch(
                batchId: wal.NextSequence,
                chunkIndex: 0,
                totalChunks: 2,
                [Encoding.UTF8.GetBytes("k:1")]);
            wal.Sync();
        }

        using var db = Open();
        var restored = db.Keyspaces.Open("cache");
        Assert.Equal([1], restored.Get("k:1"));
        Assert.Equal([2], restored.Get("k:2"));
    }

    [Fact]
    public void Clear_GenerationSurvivesCrashAndManifestCleanupIsResumable()
    {
        string oldSegment;
        var db = Open();
        var kv = db.Keyspaces.Open("cache");
        kv.Put("old:1", Encoding.UTF8.GetBytes("old"));
        long oldSequence = kv.Compact();
        oldSegment = KvKeyspace.SegmentPath(kv.RootDirectory, oldSequence);
        Assert.True(File.Exists(oldSegment));

        KvClearResult clear = kv.Clear();
        Assert.Equal(1, clear.RemovedKeys);
        Assert.Equal(1, clear.Generation);
        Assert.True(clear.CleanupPendingFiles >= 1);
        Assert.Null(kv.Get("old:1"));
        kv.Put("new:1", Encoding.UTF8.GetBytes("new"));
        db.CrashSimulationCloseWal();

        using var reopened = Open();
        var restored = reopened.Keyspaces.Open("cache");
        Assert.Equal(1, restored.Generation);
        Assert.Null(restored.Get("old:1"));
        Assert.Equal("new", Encoding.UTF8.GetString(restored.Get("new:1")!));

        KvCleanupStatus before = restored.GetCleanupStatus();
        Assert.True(before.PendingFiles >= 1);
        Assert.True(before.PendingBytes > 0);
        Assert.Equal(1, restored.CleanupPendingFiles(maxFiles: 1));
        Assert.False(File.Exists(oldSegment));
        Assert.Equal(0, restored.GetCleanupStatus().PendingFiles);
    }

    [Fact]
    public void Clear_CleanupHonorsPerRoundFileBudget()
    {
        using var db = Open();
        var kv = db.Keyspaces.Open("cache");
        kv.Put("a", [1]);
        kv.Put("b", [2]);
        long sequence = kv.Compact();
        string segment = KvKeyspace.SegmentPath(kv.RootDirectory, sequence);
        File.Copy(segment, KvKeyspace.SegmentPath(kv.RootDirectory, sequence + 1));

        var clear = kv.Clear();
        Assert.Equal(2, clear.RemovedKeys);
        Assert.True(clear.CleanupPendingFiles >= 2);

        Assert.Equal(1, kv.CleanupPendingFiles(maxFiles: 1));
        Assert.True(kv.GetCleanupStatus().PendingFiles >= 1);
        while (kv.CleanupPendingFiles(maxFiles: 1) > 0) { }
        Assert.Equal(0, kv.GetCleanupStatus().PendingFiles);
    }

    [Fact]
    public void CleanupManifest_RejectsNonStateFileInsideKeyspaceRoot()
    {
        using var db = Open();
        var kv = db.Keyspaces.Open("cache");
        kv.Put("old", [1]);
        kv.Compact();
        kv.Clear();
        string generationPath = Path.Combine(kv.RootDirectory, KvGenerationFile.FileName);
        Assert.True(File.Exists(generationPath));

        KvCleanupManifest.Save(
            kv.RootDirectory,
            new KvCleanupManifestModel(
                KvCleanupManifestModel.CurrentVersion,
                kv.Generation,
                DateTime.UtcNow.Ticks,
                [KvGenerationFile.FileName]));

        Assert.Throws<InvalidDataException>(() => kv.CleanupPendingFiles(maxFiles: 1));
        Assert.True(File.Exists(generationPath));
    }

    [Fact]
    public void BackgroundCleanup_ThrottlesUnderPressureAndResumesFromManifest()
    {
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Kv = KvOptions.Default with
            {
                ExpirerEnabled = false,
                CleanupEnabled = true,
                CleanupPollInterval = TimeSpan.FromMilliseconds(20),
                CleanupMaxFilesPerRound = 1,
                CleanupPauseWhenQueriesActive = false,
                CleanupPauseWhenFlushPending = false,
                CleanupMaxCpuPercent = 0,
                CleanupMaxMemoryLoadPercent = 0,
            },
        });
        db._kvCleanupThrottleHook = static () => KvCleanupThrottleReason.ActiveQueries;
        var kv = db.Keyspaces.Open("pressure-cache");
        kv.Put("old", [1]);
        long sequence = kv.Compact();
        string oldSegment = KvKeyspace.SegmentPath(kv.RootDirectory, sequence);
        kv.Clear();

        Assert.True(SpinWait.SpinUntil(
            () => db.GetKvMaintenanceStatus().ThrottledRounds > 0,
            TimeSpan.FromSeconds(3)));
        KvMaintenanceStatus throttled = db.GetKvMaintenanceStatus();
        Assert.Equal("active_queries", throttled.LastThrottleReason);
        Assert.True(throttled.PendingFiles >= 1);
        Assert.True(throttled.PendingBytes > 0);
        Assert.True(File.Exists(oldSegment));

        db._kvCleanupThrottleHook = static () => KvCleanupThrottleReason.None;
        Assert.True(SpinWait.SpinUntil(() => !File.Exists(oldSegment), TimeSpan.FromSeconds(3)));
        KvMaintenanceStatus completed = db.GetKvMaintenanceStatus();
        Assert.True(completed.CleanupRounds >= 1);
        Assert.True(completed.RemovedFiles >= 1);
        Assert.Equal(0, completed.PendingFiles);
        Assert.True(completed.LastBytesPerSecond > 0);
        Assert.Null(completed.LastThrottleReason);
    }

    [Fact]
    public void BackgroundCleanup_ReportsLastErrorAndRetriesManifest()
    {
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Kv = KvOptions.Default with
            {
                ExpirerEnabled = false,
                CleanupEnabled = true,
                CleanupPollInterval = TimeSpan.FromMilliseconds(20),
                CleanupMaxFilesPerRound = 1,
                CleanupPauseWhenQueriesActive = false,
                CleanupPauseWhenFlushPending = false,
                CleanupMaxCpuPercent = 0,
                CleanupMaxMemoryLoadPercent = 0,
            },
        });
        var expected = new IOException("cleanup failure");
        db._kvCleanupFaultHook = () => throw expected;
        var kv = db.Keyspaces.Open("failure-cache");
        kv.Put("old", [1]);
        long sequence = kv.Compact();
        string oldSegment = KvKeyspace.SegmentPath(kv.RootDirectory, sequence);
        kv.Clear();

        Assert.True(SpinWait.SpinUntil(
            () => db.GetKvMaintenanceStatus().LastErrorType == typeof(IOException).FullName,
            TimeSpan.FromSeconds(3)));
        Assert.Same(expected, db.LastError);
        Assert.True(File.Exists(oldSegment));

        db._kvCleanupFaultHook = null;
        Assert.True(SpinWait.SpinUntil(() => !File.Exists(oldSegment), TimeSpan.FromSeconds(3)));
        Assert.Equal(0, db.GetKvMaintenanceStatus().PendingFiles);
    }

    [Fact]
    public void BackgroundCleanup_AfterRestartDiscoversUnopenedKeyspaceManifest()
    {
        string oldSegment;
        var db = Open();
        var kv = db.Keyspaces.Open("cold-cache");
        kv.Put("old", [1]);
        long sequence = kv.Compact();
        oldSegment = KvKeyspace.SegmentPath(kv.RootDirectory, sequence);
        kv.Clear();
        db.CrashSimulationCloseWal();

        using var reopened = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Kv = KvOptions.Default with
            {
                ExpirerEnabled = false,
                CleanupEnabled = true,
                CleanupPollInterval = TimeSpan.FromMilliseconds(20),
                CleanupMaxFilesPerRound = 1,
                CleanupPauseWhenQueriesActive = false,
                CleanupPauseWhenFlushPending = false,
                CleanupMaxCpuPercent = 0,
                CleanupMaxMemoryLoadPercent = 0,
            },
        });

        Assert.True(
            SpinWait.SpinUntil(() => !File.Exists(oldSegment), TimeSpan.FromSeconds(3)),
            "后台维护应发现尚未由业务打开的 keyspace cleanup manifest。");
        Assert.Null(reopened.Keyspaces.Open("cold-cache").Get("old"));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        Kv = KvOptions.Default with
        {
            ExpirerEnabled = false,
            CleanupEnabled = false,
        },
    });
}
