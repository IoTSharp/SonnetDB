using System.Buffers;
using System.IO.Hashing;
using SonnetDB.IO;

namespace SonnetDB.Wal;

internal static class WalCheckpointFile
{
    internal const string TempSuffix = ".tmp";

    private const int FormatVersion = 1;
    private const int FileSize = 64;
    private const int CrcOffset = 48;
    private const int CrcCoveredLength = 48;

    private static readonly byte[] Magic = "SDBWCKP1"u8.ToArray();

    internal static void Save(string path, WalCheckpointState state)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegative(state.CheckpointLsn);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(state.SegmentId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(state.SegmentLength);

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (directory.Length > 0)
            Directory.CreateDirectory(directory);

        string tempPath = path + TempSuffix;
        Span<byte> buffer = stackalloc byte[FileSize];
        Write(buffer, state);

        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(buffer);
            fs.Flush(true);
        }

        File.Move(tempPath, path, overwrite: true);
        FlushDirectoryBestEffort(directory);
    }

    internal static WalCheckpointState? TryLoad(string path, Func<WalCheckpointState, bool>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
            return null;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(FileSize);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int read = ReadExact(fs, buffer, 0, FileSize);
            if (read < FileSize)
                return null;

            var state = Read(buffer.AsSpan(0, FileSize));
            if (state is null)
                return null;

            if (validate is not null && !validate(state.Value))
                return null;

            return state;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void Write(Span<byte> destination, WalCheckpointState state)
    {
        destination.Clear();
        var writer = new SpanWriter(destination);
        writer.WriteBytes(Magic);
        writer.WriteInt32(FormatVersion);
        writer.WriteInt32(FileSize);
        writer.WriteInt64(state.CheckpointLsn);
        writer.WriteInt64(state.SegmentId);
        writer.WriteInt64(state.SegmentLength);
        writer.WriteInt64(state.CreatedAtUtcTicks);

        uint crc32 = Crc32.HashToUInt32(destination[..CrcCoveredLength]);
        destination[CrcOffset] = (byte)crc32;
        destination[CrcOffset + 1] = (byte)(crc32 >> 8);
        destination[CrcOffset + 2] = (byte)(crc32 >> 16);
        destination[CrcOffset + 3] = (byte)(crc32 >> 24);
    }

    private static WalCheckpointState? Read(ReadOnlySpan<byte> source)
    {
        var reader = new SpanReader(source);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            return null;

        int version = reader.ReadInt32();
        if (version != FormatVersion)
            return null;

        int fileSize = reader.ReadInt32();
        if (fileSize != FileSize)
            return null;

        long checkpointLsn = reader.ReadInt64();
        long segmentId = reader.ReadInt64();
        long segmentLength = reader.ReadInt64();
        long createdAtUtcTicks = reader.ReadInt64();
        uint storedCrc32 = reader.ReadUInt32();

        uint actualCrc32 = Crc32.HashToUInt32(source[..CrcCoveredLength]);
        if (actualCrc32 != storedCrc32)
            return null;

        if (checkpointLsn < 0 || segmentId <= 0 || segmentLength <= 0)
            return null;

        return new WalCheckpointState(checkpointLsn, segmentId, segmentLength, createdAtUtcTicks);
    }

    private static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    internal static void FlushDirectoryBestEffort(string directory)
        => DirectoryFsync.FlushBestEffort(directory);
}

internal readonly record struct WalCheckpointState(
    long CheckpointLsn,
    long SegmentId,
    long SegmentLength,
    long CreatedAtUtcTicks);
