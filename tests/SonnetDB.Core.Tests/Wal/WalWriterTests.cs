using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Wal;

/// <summary>
/// <see cref="WalWriter"/> 单元测试。
/// </summary>
public sealed class WalWriterTests : IDisposable
{
    private readonly string _tempDir;

    public WalWriterTests()
    {
        _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string TempFile() => System.IO.Path.Combine(_tempDir, System.IO.Path.GetRandomFileName() + ".SDBWAL");

    private static void RemoveLastLsnFooter(string path)
    {
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite);
        fs.SetLength(fs.Length - FormatSizes.WalLastLsnFooterSize);
    }

    private static void CorruptLastLsnFooter(string path)
    {
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite);
        fs.Position = fs.Length - 1;
        int b = fs.ReadByte();
        fs.Position = fs.Length - 1;
        fs.WriteByte((byte)(b ^ 0xFF));
    }

    private static void TruncateLastLsnFooterTail(string path)
    {
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite);
        fs.SetLength(fs.Length - 5);
    }

    [Fact]
    public void NewFile_HasFileHeader_ThenRecords()
    {
        string path = TempFile();
        using var writer = WalWriter.Open(path);

        writer.AppendWritePoint(1UL, 1000L, "temp", FieldValue.FromDouble(42.0));
        writer.AppendWritePoint(1UL, 2000L, "temp", FieldValue.FromDouble(43.0));
        writer.Sync();

        long fileSize = new System.IO.FileInfo(path).Length;
        Assert.True(fileSize >= FormatSizes.WalFileHeaderSize);
        Assert.Equal(writer.BytesWritten + FormatSizes.WalLastLsnFooterSize, fileSize);
    }

    [Fact]
    public void NextLsn_IsMonotonicallyIncreasing()
    {
        string path = TempFile();
        using var writer = WalWriter.Open(path);

        Assert.Equal(1L, writer.NextLsn);
        long lsn1 = writer.AppendWritePoint(1UL, 1000L, "f1", FieldValue.FromDouble(1.0));
        Assert.Equal(1L, lsn1);
        Assert.Equal(2L, writer.NextLsn);

        long lsn2 = writer.AppendWritePoint(1UL, 2000L, "f2", FieldValue.FromDouble(2.0));
        Assert.Equal(2L, lsn2);
        Assert.Equal(3L, writer.NextLsn);
    }

    [Fact]
    public void BytesWritten_MatchesExpectedSize()
    {
        string path = TempFile();
        using var writer = WalWriter.Open(path);

        Assert.Equal(FormatSizes.WalFileHeaderSize, writer.BytesWritten);

        writer.AppendCheckpoint(0L);
        int expectedPayloadSize = 8; // checkpoint payload = 8 bytes
        int expectedRecordSize = FormatSizes.WalRecordHeaderSize + expectedPayloadSize;
        Assert.Equal(FormatSizes.WalFileHeaderSize + expectedRecordSize, writer.BytesWritten);
    }

    [Fact]
    public void WriteAfterDispose_ThrowsObjectDisposedException()
    {
        string path = TempFile();
        var writer = WalWriter.Open(path);
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            writer.AppendWritePoint(1UL, 1000L, "f", FieldValue.FromDouble(1.0)));
    }

    [Fact]
    public void OpenExistingFile_NewWalWithFooter_UsesFastPathAndContinuesFromLastLsn()
    {
        string path = TempFile();

        // Write first batch
        using (var writer = WalWriter.Open(path, startLsn: 1))
        {
            writer.AppendWritePoint(1UL, 1000L, "temp", FieldValue.FromDouble(1.0));
            writer.AppendWritePoint(1UL, 2000L, "temp", FieldValue.FromDouble(2.0));
            writer.Sync();
        }

        // Reopen and continue
        using (var writer = WalWriter.Open(path))
        {
            Assert.True(writer.OpenedUsingLastLsnFooter);
            Assert.Equal(3L, writer.NextLsn); // continues from 3
            long lsn = writer.AppendWritePoint(1UL, 3000L, "temp", FieldValue.FromDouble(3.0));
            Assert.Equal(3L, lsn);
        }
    }

    [Fact]
    public void OpenExistingFile_OldWalWithoutFooter_ScansAndContinuesFromLastLsn()
    {
        string path = TempFile();

        using (var writer = WalWriter.Open(path, startLsn: 1))
        {
            writer.AppendWritePoint(1UL, 1000L, "temp", FieldValue.FromDouble(1.0));
            writer.AppendWritePoint(1UL, 2000L, "temp", FieldValue.FromDouble(2.0));
            writer.Sync();
        }

        RemoveLastLsnFooter(path);

        using (var writer = WalWriter.Open(path))
        {
            Assert.False(writer.OpenedUsingLastLsnFooter);
            Assert.Equal(3L, writer.NextLsn);
            Assert.Equal(3L, writer.AppendWritePoint(1UL, 3000L, "temp", FieldValue.FromDouble(3.0)));
            writer.Sync();
        }

        using var reader = WalReader.Open(path);
        Assert.Equal(new[] { 1L, 2L, 3L }, reader.Replay().Select(static r => r.Lsn).ToArray());
    }

    [Fact]
    public void OpenExistingFile_DamagedFooter_FallsBackToScanAndContinuesFromLastLsn()
    {
        string path = TempFile();

        using (var writer = WalWriter.Open(path, startLsn: 1))
        {
            writer.AppendWritePoint(1UL, 1000L, "temp", FieldValue.FromDouble(1.0));
            writer.AppendWritePoint(1UL, 2000L, "temp", FieldValue.FromDouble(2.0));
            writer.Sync();
        }

        CorruptLastLsnFooter(path);

        using (var writer = WalWriter.Open(path))
        {
            Assert.False(writer.OpenedUsingLastLsnFooter);
            Assert.Equal(3L, writer.NextLsn);
            Assert.Equal(3L, writer.AppendWritePoint(1UL, 3000L, "temp", FieldValue.FromDouble(3.0)));
            writer.Sync();
        }

        using var reader = WalReader.Open(path);
        Assert.Equal(new[] { 1L, 2L, 3L }, reader.Replay().Select(static r => r.Lsn).ToArray());
    }

    [Fact]
    public void OpenExistingFile_TruncatedFooterTail_FallsBackToScanAndContinuesFromLastLsn()
    {
        string path = TempFile();

        using (var writer = WalWriter.Open(path, startLsn: 1))
        {
            writer.AppendWritePoint(1UL, 1000L, "temp", FieldValue.FromDouble(1.0));
            writer.AppendWritePoint(1UL, 2000L, "temp", FieldValue.FromDouble(2.0));
            writer.Sync();
        }

        TruncateLastLsnFooterTail(path);

        using (var writer = WalWriter.Open(path))
        {
            Assert.False(writer.OpenedUsingLastLsnFooter);
            Assert.Equal(3L, writer.NextLsn);
            Assert.Equal(3L, writer.AppendWritePoint(1UL, 3000L, "temp", FieldValue.FromDouble(3.0)));
            writer.Sync();
        }

        using var reader = WalReader.Open(path);
        Assert.Equal(new[] { 1L, 2L, 3L }, reader.Replay().Select(static r => r.Lsn).ToArray());
    }

    [Fact]
    public void SyncAndReopen_AllDataReadable()
    {
        string path = TempFile();
        using (var writer = WalWriter.Open(path))
        {
            writer.AppendWritePoint(1UL, 1000L, "temp", FieldValue.FromDouble(10.0));
            writer.AppendWritePoint(2UL, 2000L, "pressure", FieldValue.FromDouble(20.0));
            writer.Sync();
        }

        var records = new List<WalRecord>();
        using var reader = WalReader.Open(path);
        records.AddRange(reader.Replay());

        Assert.Equal(2, records.Count);
        var wp0 = Assert.IsType<WritePointRecord>(records[0]);
        Assert.Equal(1UL, wp0.SeriesId);
        Assert.Equal(1000L, wp0.PointTimestamp);
    }

    [Fact]
    public void Flush_WritesBufferedRecordsToReadableWal()
    {
        string path = TempFile();
        using var writer = WalWriter.Open(path, bufferSize: 4 * 1024);

        writer.AppendWritePoint(1UL, 1000L, "temp", FieldValue.FromDouble(10.0));
        writer.Flush();

        using var reader = WalReader.Open(path);
        var record = Assert.IsType<WritePointRecord>(Assert.Single(reader.Replay()));
        Assert.Equal(1UL, record.SeriesId);
        Assert.Equal(1000L, record.PointTimestamp);
        Assert.Equal("temp", record.FieldName);
    }

    [Fact]
    public void AppendRecord_SmallAndLargePayload_RoundTrips()
    {
        string path = TempFile();
        string largeValue = new('x', 4096);

        using (var writer = WalWriter.Open(path))
        {
            writer.AppendCheckpoint(123L);
            writer.AppendWritePoint(9UL, 456L, "large_text", FieldValue.FromString(largeValue));
            writer.Sync();
        }

        using var reader = WalReader.Open(path);
        var records = reader.Replay().ToList();

        var checkpoint = Assert.IsType<CheckpointRecord>(records[0]);
        Assert.Equal(123L, checkpoint.CheckpointLsn);

        var writePoint = Assert.IsType<WritePointRecord>(records[1]);
        Assert.Equal(9UL, writePoint.SeriesId);
        Assert.Equal(456L, writePoint.PointTimestamp);
        Assert.Equal("large_text", writePoint.FieldName);
        Assert.Equal(largeValue, writePoint.Value.AsString());
    }

    [Fact]
    public void AppendCheckpoint_NegativeLsn_IsRejectedBeforeWriting()
    {
        string path = TempFile();
        using var writer = WalWriter.Open(path);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AppendCheckpoint(-1));
        writer.AppendWritePoint(9UL, 456L, "after-rejected-checkpoint", FieldValue.FromLong(1));
        writer.Sync();

        using var reader = WalReader.Open(path);
        WritePointRecord record = Assert.IsType<WritePointRecord>(Assert.Single(reader.Replay()));
        Assert.Equal("after-rejected-checkpoint", record.FieldName);
    }

    [Fact]
    public void AppendWritePoint_WithGeoPoint_RoundTrips()
    {
        string path = TempFile();
        var expected = FieldValue.FromGeoPoint(31.2304, 121.4737);

        using (var writer = WalWriter.Open(path))
        {
            writer.AppendWritePoint(7UL, 1234L, "position", expected);
            writer.Sync();
        }

        using var reader = WalReader.Open(path);
        var record = Assert.IsType<WritePointRecord>(Assert.Single(reader.Replay()));
        Assert.Equal(7UL, record.SeriesId);
        Assert.Equal(1234L, record.PointTimestamp);
        Assert.Equal("position", record.FieldName);
        Assert.Equal(expected, record.Value);
    }

    [Fact]
    public void IsOpen_TrueBeforeDispose_FalseAfter()
    {
        string path = TempFile();
        var writer = WalWriter.Open(path);
        Assert.True(writer.IsOpen);
        writer.Dispose();
        Assert.False(writer.IsOpen);
    }
}
