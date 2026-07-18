using System.Buffers.Binary;
using System.IO.Hashing;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvWalFileSealedReplayTests : IDisposable
{
    private const int WalHeaderSize = 64;
    private const int RecordHeaderSize = 32;
    private const int RecordHeaderCrcOffset = 28;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-kv-sealed-wal-" + Guid.NewGuid().ToString("N"));

    public KvWalFileSealedReplayTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ReplaySealed_ValidFile_ReturnsSequenceMetadataAndRecords()
    {
        string path = CreateSealedWal(startSequence: 10, recordCount: 3);
        var records = new List<KvWalRecord>();

        KvWalReplayInfo info = KvWalFile.ReplaySealed(path, records.Add, expectedFirstSequence: 10);

        Assert.Equal(10, info.FirstSequence);
        Assert.Equal(12, info.LastSequence);
        Assert.Equal(13, info.NextSequence);
        Assert.Equal(3, info.RecordCount);
        Assert.Equal([10L, 11L, 12L], records.Select(static record => record.Sequence).ToArray());
    }

    [Fact]
    public void Replay_SealedWalWithTruncatedTail_ThrowsInsteadOfReturningValidPrefix()
    {
        string path = CreateSealedWal(startSequence: 1, recordCount: 2);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(stream.Length - 1);

        Assert.Throws<InvalidDataException>(() => KvWalFile.Replay(path).ToArray());
    }

    [Fact]
    public void Replay_SealedWalWithInvalidPayloadCrc_ThrowsInsteadOfReturningValidPrefix()
    {
        string path = CreateSealedWal(startSequence: 1, recordCount: 2);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x5A;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => KvWalFile.Replay(path).ToArray());
    }

    [Fact]
    public void Replay_SealedWalWhoseFilenameDoesNotMatchFinalRecord_Throws()
    {
        string path = CreateSealedWal(startSequence: 20, recordCount: 2);
        string mismatchedPath = SealedWalPath(endSequence: 22);
        File.Move(path, mismatchedPath);

        Assert.Throws<InvalidDataException>(() => KvWalFile.Replay(mismatchedPath).ToArray());
    }

    [Fact]
    public void Replay_SealedWalWithInternalSequenceGap_Throws()
    {
        string path = CreateSealedWal(startSequence: 30, recordCount: 2);
        byte[] bytes = File.ReadAllBytes(path);
        int firstPayloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.AsSpan(WalHeaderSize, sizeof(int)));
        int secondHeaderOffset = WalHeaderSize + RecordHeaderSize + firstPayloadLength;
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes.AsSpan(secondHeaderOffset + 8, sizeof(long)),
            32);
        uint headerCrc = Crc32.HashToUInt32(
            bytes.AsSpan(secondHeaderOffset, RecordHeaderCrcOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(secondHeaderOffset + RecordHeaderCrcOffset, sizeof(uint)),
            headerCrc);
        File.WriteAllBytes(path, bytes);

        string renamedPath = SealedWalPath(endSequence: 32);
        File.Move(path, renamedPath);

        Assert.Throws<InvalidDataException>(() => KvWalFile.Replay(renamedPath).ToArray());
    }

    [Fact]
    public void ReplaySealed_UnexpectedChainStart_ThrowsBeforeApplyingRecords()
    {
        string path = CreateSealedWal(startSequence: 40, recordCount: 2);
        var records = new List<KvWalRecord>();

        Assert.Throws<InvalidDataException>(
            () => KvWalFile.ReplaySealed(path, records.Add, expectedFirstSequence: 39));
        Assert.Empty(records);
    }

    [Fact]
    public void Open_ActiveWalWithDamagedTail_TruncatesTailAndContinuesAtMissingSequence()
    {
        string path = Path.Combine(_root, "active.SDBKVWAL");
        long firstRecordEnd;
        using (var wal = KvWalFile.Open(path, startSequence: 1, bufferSize: 4096))
        {
            wal.AppendPut("first"u8, [1]);
            wal.Sync();
            firstRecordEnd = wal.Length;
            wal.AppendPut("second"u8, [2]);
            wal.Sync();
        }

        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x5A;
        File.WriteAllBytes(path, bytes);

        var replayed = new List<KvWalRecord>();
        using var reopened = KvWalFile.Open(path, startSequence: 1, bufferSize: 4096, replayed.Add);

        Assert.Single(replayed);
        Assert.Equal(1, replayed[0].Sequence);
        Assert.Equal(firstRecordEnd, reopened.Length);
        Assert.Equal(2, reopened.NextSequence);
        Assert.Equal(2, reopened.AppendPut("replacement"u8, [3]));
    }

    [Fact]
    public void Open_ActiveWalWithCorruptMiddlePayload_FailsInsteadOfDiscardingLaterRecords()
    {
        string path = Path.Combine(_root, "active-middle-corruption.SDBKVWAL");
        long firstRecordEnd;
        using (var wal = KvWalFile.Open(path, startSequence: 1, bufferSize: 4096))
        {
            wal.AppendPut("first"u8, [1]);
            firstRecordEnd = wal.Length;
            wal.AppendPut("second"u8, [2]);
            wal.AppendPut("third"u8, [3]);
            wal.Sync();
        }

        byte[] bytes = File.ReadAllBytes(path);
        int secondPayloadOffset = checked((int)firstRecordEnd + 32);
        bytes[secondPayloadOffset + 16] ^= 0x5A;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(
            () => KvWalFile.Open(path, startSequence: 1, bufferSize: 4096, _ => { }));
    }

    private string CreateSealedWal(long startSequence, int recordCount)
    {
        string activePath = Path.Combine(_root, "active-" + Guid.NewGuid().ToString("N") + ".SDBKVWAL");
        long endSequence;
        using (var wal = KvWalFile.Open(activePath, startSequence, bufferSize: 4096))
        {
            for (int i = 0; i < recordCount; i++)
                wal.AppendPut([(byte)(i + 1)], [(byte)i]);
            endSequence = wal.NextSequence - 1;
            wal.Seal(SealedWalPath(endSequence));
        }

        return SealedWalPath(endSequence);
    }

    private string SealedWalPath(long endSequence)
        => Path.Combine(_root, $"sealed-{endSequence:D20}.SDBKVWAL");
}
