using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvWalRecoveryChainTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-kv-wal-chain-" + Guid.NewGuid().ToString("N"));

    public KvWalRecoveryChainTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Open_GapBetweenSealedWalFiles_FailsClosed()
    {
        CreateSealedWal(startSequence: 1, recordCount: 2);
        CreateSealedWal(startSequence: 4, recordCount: 1);
        CreateActiveWal(startSequence: 5, recordCount: 0);

        Assert.Throws<InvalidDataException>(() => OpenKeyspace());
    }

    [Fact]
    public void Open_GapBetweenSealedAndActiveWal_FailsClosed()
    {
        CreateSealedWal(startSequence: 1, recordCount: 2);
        CreateActiveWal(startSequence: 4, recordCount: 1);

        Assert.Throws<InvalidDataException>(() => OpenKeyspace());
    }

    [Fact]
    public void Open_ContinuousSealedAndActiveWal_ReplaysAllRecords()
    {
        CreateSealedWal(startSequence: 1, recordCount: 2);
        CreateSealedWal(startSequence: 3, recordCount: 2);
        CreateActiveWal(startSequence: 5, recordCount: 1);

        using KvKeyspace keyspace = OpenKeyspace();

        Assert.Equal(5, keyspace.LastSequence);
        for (int sequence = 1; sequence <= 5; sequence++)
            Assert.Equal([(byte)sequence], keyspace.Get($"key:{sequence}")!);
    }

    [Fact]
    public void Open_CorruptSealedWalAfterStateLoad_ReleasesStateHandleOnFailure()
    {
        string statePath;
        using (KvKeyspace keyspace = OpenKeyspace())
        {
            keyspace.Put("base", [1]);
            long sequence = keyspace.Compact();
            statePath = KvKeyspace.SegmentPath(_root, sequence);
        }

        string sealedWalPath = CreateSealedWal(startSequence: 2, recordCount: 1);
        byte[] bytes = File.ReadAllBytes(sealedWalPath);
        bytes[^1] ^= 0x5A;
        File.WriteAllBytes(sealedWalPath, bytes);

        Assert.Throws<InvalidDataException>(() => OpenKeyspace());

        string movedStatePath = statePath + ".moved";
        File.Move(statePath, movedStatePath);
        File.Delete(movedStatePath);
    }

    private KvKeyspace OpenKeyspace() => KvKeyspace.Open(
        "chain",
        _root,
        KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        });

    private string CreateSealedWal(long startSequence, int recordCount)
    {
        string temporaryPath = Path.Combine(
            KvKeyspace.WalDirectory(_root),
            $"active-{startSequence}-{Guid.NewGuid():N}.SDBKVWAL");
        Directory.CreateDirectory(KvKeyspace.WalDirectory(_root));
        using var wal = KvWalFile.Open(temporaryPath, startSequence, bufferSize: 4096);
        AppendRecords(wal, startSequence, recordCount);
        string sealedWalPath = KvKeyspace.SealedWalPath(_root, startSequence + recordCount - 1);
        wal.Seal(sealedWalPath);
        return sealedWalPath;
    }

    private void CreateActiveWal(long startSequence, int recordCount)
    {
        Directory.CreateDirectory(KvKeyspace.WalDirectory(_root));
        using var wal = KvWalFile.Open(
            KvKeyspace.ActiveWalPath(_root),
            startSequence,
            bufferSize: 4096);
        AppendRecords(wal, startSequence, recordCount);
    }

    private static void AppendRecords(KvWalFile wal, long startSequence, int recordCount)
    {
        for (int i = 0; i < recordCount; i++)
        {
            long sequence = startSequence + i;
            Assert.Equal(
                sequence,
                wal.AppendPut(System.Text.Encoding.UTF8.GetBytes($"key:{sequence}"), [(byte)sequence]));
        }

        wal.Sync();
    }
}
