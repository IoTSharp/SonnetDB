using System.Buffers.Binary;
using System.IO.Hashing;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Wal;

public sealed class WalAtomicPointTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sndb-atomic-point-" + Guid.NewGuid().ToString("N"));

    public WalAtomicPointTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string FilePath => Path.Combine(_directory, "point.SDBWAL");

    private static Dictionary<string, FieldValue> Fields() => new()
    {
        ["long"] = FieldValue.FromLong(long.MinValue),
        ["double"] = FieldValue.FromDouble(1.25),
        ["bool"] = FieldValue.FromBool(true),
        ["string"] = FieldValue.FromString("时序🙂"),
        ["vector"] = FieldValue.FromVector(new float[] { 1, 2 }),
        ["geo"] = FieldValue.FromGeoPoint(10, 20),
    };

    [Fact]
    public void RoundTrip_AllFieldsShareOneRecord_AndReopenContinuesLsn()
    {
        using (var writer = WalWriter.OpenAtomicPoints(FilePath, startLsn: 42))
        {
            Assert.Equal(42, writer.AppendAtomicPoint(7, 1000, Fields()));
            writer.Sync();
        }
        using (var writer = WalWriter.OpenAtomicPoints(FilePath))
        {
            Assert.Equal(43, writer.NextLsn);
            Assert.Equal(43, writer.AppendAtomicPoint(7, 1000, Fields()));
        }
        using var reader = WalReader.OpenAtomicPoints(FilePath);
        var records = reader.Replay().Cast<AtomicPointRecord>().ToArray();
        Assert.Equal(2, records.Length);
        Assert.Equal(new long[] { 42, 43 }, records.Select(record => record.Lsn));
        Assert.All(records, record =>
        {
            Assert.Equal(7UL, record.SeriesId);
            Assert.Equal(1000, record.PointTimestamp);
            Assert.Equal(6, record.Fields.Count);
            Assert.Equal(long.MinValue, record.Fields["long"].AsLong());
            Assert.Equal(1.25, record.Fields["double"].AsDouble());
            Assert.True(record.Fields["bool"].AsBool());
            Assert.Equal("时序🙂", record.Fields["string"].AsString());
            Assert.Equal(new float[] { 1, 2 }, record.Fields["vector"].AsVector().ToArray());
            Assert.Equal(FieldValue.FromGeoPoint(10, 20), record.Fields["geo"]);
        });
    }

    [Fact]
    public void LsnBounds_RejectInvalidStartAndOverflowWithoutChangingConfirmedPoint()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WalWriter.OpenAtomicPoints(FilePath, startLsn: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WalWriter.OpenAtomicPoints(FilePath, startLsn: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WalWriter.OpenAtomicPoints(FilePath, startLsn: long.MaxValue));
        Assert.False(File.Exists(FilePath));
        using (var writer = WalWriter.OpenAtomicPoints(FilePath, startLsn: long.MaxValue - 1))
        {
            Assert.Equal(long.MaxValue - 1, writer.AppendAtomicPoint(1, 0, Fields()));
            long bytes = writer.BytesWritten;
            Assert.Throws<InvalidOperationException>(() => writer.AppendAtomicPoint(1, 1, Fields()));
            Assert.Equal(bytes, writer.BytesWritten);
            Assert.Equal(long.MaxValue, writer.NextLsn);
        }
        using (var writer = WalWriter.OpenAtomicPoints(FilePath))
        {
            Assert.Equal(long.MaxValue, writer.NextLsn);
            Assert.Throws<InvalidOperationException>(() => writer.AppendAtomicPoint(1, 1, Fields()));
        }
        using var reader = WalReader.OpenAtomicPoints(FilePath);
        Assert.Equal(long.MaxValue - 1, Assert.Single(reader.Replay()).Lsn);
    }

    [Fact]
    public void ExplicitVersionBoundary_RefusesBothDirections_WithoutChangingFile()
    {
        using (var writer = WalWriter.OpenAtomicPoints(FilePath))
            writer.AppendAtomicPoint(7, 1000, Fields());
        byte[] before = File.ReadAllBytes(FilePath);
        Assert.Throws<InvalidDataException>(() => WalReader.Open(FilePath));
        Assert.Throws<InvalidDataException>(() => WalWriter.Open(FilePath));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(before.AsSpan(8)));

        string legacyPath = Path.Combine(_directory, "legacy.SDBWAL");
        using (var writer = WalWriter.Open(legacyPath))
            writer.AppendWritePoint(7, 1000, "value", FieldValue.FromLong(1));
        byte[] legacyBefore = File.ReadAllBytes(legacyPath);
        Assert.Throws<InvalidDataException>(() => WalReader.OpenAtomicPoints(legacyPath));
        Assert.Throws<InvalidDataException>(() => WalWriter.OpenAtomicPoints(legacyPath));
        Assert.Equal(legacyBefore, File.ReadAllBytes(legacyPath));
        using var legacyReader = WalReader.Open(legacyPath);
        Assert.IsType<WritePointRecord>(Assert.Single(legacyReader.Replay()));
    }

    [Fact]
    public void V2_RefusesLegacyFieldsCatalogCheckpointAndDelete()
    {
        using var writer = WalWriter.OpenAtomicPoints(FilePath);
        Assert.Throws<InvalidOperationException>(() => writer.AppendWritePoint(1, 0, "v", FieldValue.FromLong(1)));
        Assert.Throws<InvalidOperationException>(() => writer.AppendCreateSeries(1, "cpu", new Dictionary<string, string>()));
        Assert.Throws<InvalidOperationException>(() => writer.AppendCheckpoint(1));
        Assert.Throws<InvalidOperationException>(() => writer.AppendDelete(1, "v", 0, 1));
        Assert.Equal(1, writer.NextLsn);
        Assert.Equal(FormatSizes.WalFileHeaderSize, writer.BytesWritten);
    }

    [Fact]
    public void InvalidOrCancelledPoint_WritesNoRecord_AndWriterRemainsUsable()
    {
        using var writer = WalWriter.OpenAtomicPoints(FilePath);
        Assert.Throws<ArgumentNullException>(() => writer.AppendAtomicPoint(1, 0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AppendAtomicPoint(1, -1, Fields()));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AppendAtomicPoint(1, 0, new Dictionary<string, FieldValue>()));
        Assert.Throws<ArgumentException>(() => writer.AppendAtomicPoint(1, 0,
            new Dictionary<string, FieldValue> { [" "] = FieldValue.FromLong(1) }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => writer.AppendAtomicPoint(1, 0, Fields(), cancellation.Token));
        Assert.Equal(1, writer.NextLsn);
        Assert.Equal(FormatSizes.WalFileHeaderSize, writer.BytesWritten);
        Assert.Equal(1, writer.AppendAtomicPoint(1, 0, Fields()));
    }

    [Fact]
    public void InvalidUtf16NameOrValue_IsRejectedBeforeWrite_AndLaterPointWorks()
    {
        using var writer = WalWriter.OpenAtomicPoints(FilePath);
        Assert.Throws<ArgumentException>(() => writer.AppendAtomicPoint(1, 0,
            new Dictionary<string, FieldValue> { ["\uD800"] = FieldValue.FromLong(1) }));
        Assert.Throws<ArgumentException>(() => writer.AppendAtomicPoint(1, 0,
            new Dictionary<string, FieldValue>
            {
                ["\uD800"] = FieldValue.FromLong(1),
                ["\uD801"] = FieldValue.FromLong(2),
            }));
        Assert.Throws<ArgumentException>(() => writer.AppendAtomicPoint(1, 0,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromString("\uD801") }));
        Assert.Equal(1, writer.NextLsn);
        Assert.Equal(FormatSizes.WalFileHeaderSize, writer.BytesWritten);
        Assert.Equal(1, writer.AppendAtomicPoint(1, 0, Fields()));
        writer.Flush();
        using var reader = WalReader.OpenAtomicPoints(FilePath);
        Assert.Equal(6, Assert.IsType<AtomicPointRecord>(Assert.Single(reader.Replay())).Fields.Count);
    }

    [Fact]
    public void RecoveryBudgetOrCancellation_LeavesFileUnchanged()
    {
        using (var writer = WalWriter.OpenAtomicPoints(FilePath))
        {
            writer.AppendAtomicPoint(1, 0, Fields());
            writer.AppendAtomicPoint(1, 1, Fields());
        }
        byte[] before = File.ReadAllBytes(FilePath);
        Assert.Throws<InvalidDataException>(() => WalWriter.OpenAtomicPoints(FilePath,
            recoveryOptions: new AtomicPointWalRecoveryOptions(MaxRecords: 1)));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Throws<InvalidDataException>(() => WalWriter.OpenAtomicPoints(FilePath,
            recoveryOptions: new AtomicPointWalRecoveryOptions(MaxReadBytes: before.Length - 1)));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Throws<InvalidDataException>(() => WalWriter.OpenAtomicPoints(FilePath,
            recoveryOptions: new AtomicPointWalRecoveryOptions(Timeout: TimeSpan.FromTicks(1))));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        using (var exact = WalReader.OpenAtomicPoints(FilePath,
            new AtomicPointWalRecoveryOptions(MaxRecords: 2, MaxReadBytes: before.Length)))
            Assert.Equal(2, exact.Replay().Count());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => WalReader.OpenAtomicPoints(FilePath, cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => WalWriter.OpenAtomicPoints(FilePath, cancellationToken: cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        using var duringReplay = new CancellationTokenSource();
        using var reader = WalReader.OpenAtomicPoints(FilePath, cancellationToken: duringReplay.Token);
        using var replay = reader.Replay().GetEnumerator();
        Assert.True(replay.MoveNext());
        duringReplay.Cancel();
        Assert.Throws<OperationCanceledException>(() => replay.MoveNext());
        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void FieldCountBudget_Accepts1024_Rejects1025BeforeAppend()
    {
        using var writer = WalWriter.OpenAtomicPoints(FilePath);
        var fields = Enumerable.Range(0, 1024).ToDictionary(i => "v" + i, i => FieldValue.FromLong(i));
        Assert.Equal(1, writer.AppendAtomicPoint(1, long.MaxValue, fields));
        long bytes = writer.BytesWritten;
        fields.Add("overflow", FieldValue.FromLong(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AppendAtomicPoint(1, 0, fields));
        Assert.Equal(2, writer.NextLsn);
        Assert.Equal(bytes, writer.BytesWritten);
    }

    [Fact]
    public void PayloadBudget_AcceptsExactLimit_RejectsOneByteMore()
    {
        // count(4), field length(4), field fixed payload(28), UTF8 field name(1).
        int stringLength = AtomicPointWalCodec.MaxPayloadBytes - 37;
        using var writer = WalWriter.OpenAtomicPoints(FilePath);
        var fields = new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromString(new string('x', stringLength)) };
        Assert.Equal(1, writer.AppendAtomicPoint(1, 0, fields));
        long bytes = writer.BytesWritten;
        fields["v"] = FieldValue.FromString(new string('x', stringLength + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AppendAtomicPoint(1, 0, fields));
        Assert.Equal(bytes, writer.BytesWritten);
        writer.Flush();
        using var reader = WalReader.OpenAtomicPoints(FilePath);
        Assert.Equal(stringLength, Assert.IsType<AtomicPointRecord>(Assert.Single(reader.Replay())).Fields["v"].AsString().Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(90)]
    public void TruncatedSecondRecord_DiscardsWholePoint_AndRepairPreservesFirst(int tailLength)
    {
        long secondOffset;
        using (var writer = WalWriter.OpenAtomicPoints(FilePath))
        {
            writer.AppendAtomicPoint(1, 0, Fields());
            secondOffset = writer.BytesWritten;
            writer.AppendAtomicPoint(1, 1, Fields());
        }
        using (var file = new FileStream(FilePath, FileMode.Open, FileAccess.Write))
            file.SetLength(secondOffset + tailLength);
        using (var reader = WalReader.OpenAtomicPoints(FilePath))
        {
            Assert.Single(reader.Replay());
            Assert.Equal(secondOffset, reader.LastValidOffset);
            Assert.Equal(1, reader.LastLsn);
        }
        using (var writer = WalWriter.OpenAtomicPoints(FilePath))
            Assert.Equal(2, writer.AppendAtomicPoint(1, 2, Fields()));
        using var recovered = WalReader.OpenAtomicPoints(FilePath);
        Assert.Equal(new long[] { 0, 2 }, recovered.Replay().Cast<AtomicPointRecord>().Select(point => point.PointTimestamp));
    }

    [Fact]
    public void UnknownAppend_SealsWriter_ReopenReconcilesCommittedWholePoint()
    {
        using (var writer = WalWriter.OpenAtomicPoints(FilePath))
        {
            writer.AfterAtomicPointWriteTestHook = () => throw new IOException("injected unknown acknowledgement");
            Assert.Throws<IOException>(() => writer.AppendAtomicPoint(1, 0, Fields()));
            Assert.Throws<InvalidOperationException>(() => writer.AppendAtomicPoint(1, 1, Fields()));
            Assert.Throws<InvalidOperationException>(() => writer.Sync());
            Assert.Throws<InvalidOperationException>(() => writer.AppendCheckpoint(1));
        }
        using (var writer = WalWriter.OpenAtomicPoints(FilePath))
        {
            Assert.Equal(2, writer.NextLsn);
            writer.AppendAtomicPoint(1, 1, Fields());
        }
        using var reader = WalReader.OpenAtomicPoints(FilePath);
        Assert.Equal(2, reader.Replay().Count());
    }

    [Fact]
    public void CancellationAfterWriteStarts_DoesNotUndoCompletePoint()
    {
        using var cancellation = new CancellationTokenSource();
        using (var writer = WalWriter.OpenAtomicPoints(FilePath))
        {
            writer.AfterAtomicPointWriteTestHook = cancellation.Cancel;
            Assert.Equal(1, writer.AppendAtomicPoint(1, 0, Fields(), cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested);
        }
        using var reader = WalReader.OpenAtomicPoints(FilePath);
        Assert.Single(reader.Replay());
    }

    [Theory]
    [InlineData(4)] // protected header record type
    [InlineData(8)] // protected header payload length
    [InlineData(32)] // payload count
    [InlineData(75)] // payload field data
    public void CorruptedTailRecord_DiscardsAllItsFields(int relativeOffset)
    {
        long offset;
        using (var writer = WalWriter.OpenAtomicPoints(FilePath))
        {
            writer.AppendAtomicPoint(1, 0, Fields());
            offset = writer.BytesWritten;
            writer.AppendAtomicPoint(1, 1, Fields());
        }
        byte[] bytes = File.ReadAllBytes(FilePath);
        bytes[(int)offset + relativeOffset] ^= 0xff;
        File.WriteAllBytes(FilePath, bytes);
        using (var reader = WalReader.OpenAtomicPoints(FilePath))
        {
            Assert.Single(reader.Replay());
            Assert.Equal(offset, reader.LastValidOffset);
        }
        using var repaired = WalWriter.OpenAtomicPoints(FilePath);
        Assert.Equal(2, repaired.NextLsn);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("duplicate")]
    [InlineData("series")]
    [InlineData("vector")]
    public void ValidCrcButInvalidPointShape_StopsBeforeFollowingRecord(string corruption)
    {
        using (var writer = WalWriter.OpenAtomicPoints(FilePath))
        {
            writer.AppendAtomicPoint(1, 0, new Dictionary<string, FieldValue>
            {
                ["a"] = FieldValue.FromLong(1),
                ["b"] = FieldValue.FromVector(new float[] { 1, 2 }),
            });
            writer.AppendAtomicPoint(1, 1, Fields());
        }
        byte[] bytes = File.ReadAllBytes(FilePath);
        int payloadOffset = FormatSizes.WalFileHeaderSize + FormatSizes.WalRecordHeaderSize;
        int firstField = payloadOffset + 2 * sizeof(int);
        int firstLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(payloadOffset + sizeof(int)));
        int secondField = firstField + firstLength + sizeof(int);
        switch (corruption)
        {
            case "count":
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(payloadOffset), 0);
                break;
            case "duplicate":
                bytes[secondField + 24] = bytes[firstField + 24]; // the two one-byte names become identical
                break;
            case "series":
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(secondField), 2);
                break;
            case "vector":
                // vector field: fixed prefix(24), name(1), value length(4), dimension(4)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(secondField + 29), 3);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        RewriteChecksums(bytes, FormatSizes.WalFileHeaderSize);
        File.WriteAllBytes(FilePath, bytes);
        using var reader = WalReader.OpenAtomicPoints(FilePath);
        Assert.Empty(reader.Replay());
        Assert.Equal(FormatSizes.WalFileHeaderSize, reader.LastValidOffset);
        Assert.Equal(0, reader.LastLsn);
    }

    private static void RewriteChecksums(byte[] bytes, int recordOffset)
    {
        int payloadOffset = recordOffset + FormatSizes.WalRecordHeaderSize;
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(recordOffset + 8));
        uint payloadCrc = Crc32.HashToUInt32(bytes.AsSpan(payloadOffset, payloadLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(recordOffset + 12), payloadCrc);
        bytes[recordOffset + 5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(recordOffset + 6), 0);
        var header = bytes.AsSpan(recordOffset, FormatSizes.WalRecordHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6), WalRecordHeader.ComputeHeaderChecksum(header));
    }
}
