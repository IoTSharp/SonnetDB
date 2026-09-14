using System.Buffers;
using System.IO.Hashing;
using SonnetDB.IO;

namespace SonnetDB.Wal;

/// <summary>
/// Flush 段发布的恢复标记。每个待发布段拥有独立标记：标记先于段文件的原子 rename
/// 落盘，段和独立 checkpoint 均成功后才标记为已提交并尝试删除。
/// </summary>
/// <remarks>
/// <para>
/// 重启时，未提交标记若没有被有效 checkpoint 覆盖，关联段必须删除并从 WAL 重放；
/// 已提交标记只是上次清理失败的遗留，可安全保留段并再次尝试清理。
/// </para>
/// <para>
/// 最终标记损坏时不能推断段是否已提交，因此读取失败会显式抛出，避免静默加载可能与
/// WAL 重放重复的数据。仅 <c>.tmp</c> 文件可安全忽略，因为它从未原子发布。
/// </para>
/// </remarks>
internal static class FlushPublicationFile
{
    internal const string TempSuffix = ".tmp";

    private const int FormatVersion = 1;
    private const int FileSize = 64;
    private const int CrcOffset = 48;
    private const int CrcCoveredLength = 48;

    private static readonly byte[] Magic = "SDBFPUB1"u8.ToArray();

    /// <summary>以原子替换方式持久化一个待确认的段发布标记。</summary>
    internal static FlushPublicationState SavePending(string path, long segmentId, long checkpointLsn)
    {
        var state = new FlushPublicationState(
            segmentId,
            checkpointLsn,
            FlushPublicationStatus.Pending,
            DateTime.UtcNow.Ticks);
        Save(path, state);
        return state;
    }

    /// <summary>把已经由 durable checkpoint 覆盖的标记提升为已提交状态。</summary>
    internal static void MarkCommittedRequired(
        string path,
        FlushPublicationState state,
        bool flushDirectory = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        Save(path, state with { Status = FlushPublicationStatus.Committed }, flushDirectory);
    }

    /// <summary>
    /// 读取已原子发布的标记；格式或校验失败则抛出。
    /// </summary>
    internal static FlushPublicationState Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length != FileSize)
        {
            throw new InvalidDataException(
                $"Flush publication marker '{path}' has an invalid length ({fs.Length}).");
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(FileSize);
        try
        {
            fs.ReadExactly(rented.AsSpan(0, FileSize));
            return Read(rented.AsSpan(0, FileSize), path);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>删除已提交 marker；清理失败不影响已经提交的数据可见性。</summary>
    internal static void TryClearCommitted(string path, bool flushDirectory = true)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            File.Delete(path);
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            if (flushDirectory && directory.Length > 0)
                DirectoryFsync.FlushRequired(directory);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    /// <summary>删除未提交 marker；调用方必须在删除孤立段后调用，失败时应中止启动。</summary>
    internal static void ClearUncommittedRequired(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        File.Delete(path);
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (directory.Length == 0)
            throw new ArgumentException("Flush publication marker must have a parent directory.", nameof(path));

        DirectoryFsync.FlushRequired(directory);
    }

    private static void Save(string path, FlushPublicationState state, bool flushDirectory = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(state.SegmentId);
        ArgumentOutOfRangeException.ThrowIfNegative(state.CheckpointLsn);
        if (!Enum.IsDefined(state.Status))
            throw new ArgumentOutOfRangeException(nameof(state), "Flush publication status is invalid.");

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (directory.Length == 0)
            throw new ArgumentException("Flush publication marker must have a parent directory.", nameof(path));

        Directory.CreateDirectory(directory);
        string tempPath = path + TempSuffix;
        Span<byte> buffer = stackalloc byte[FileSize];
        Write(buffer, state);

        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(buffer);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
        if (flushDirectory)
            DirectoryFsync.FlushRequired(directory);
    }

    private static void Write(Span<byte> destination, FlushPublicationState state)
    {
        destination.Clear();
        var writer = new SpanWriter(destination);
        writer.WriteBytes(Magic);
        writer.WriteInt32(FormatVersion);
        writer.WriteInt32(FileSize);
        writer.WriteInt64(state.SegmentId);
        writer.WriteInt64(state.CheckpointLsn);
        writer.WriteInt32((int)state.Status);
        writer.WriteInt32(0);
        writer.WriteInt64(state.CreatedAtUtcTicks);

        uint crc32 = Crc32.HashToUInt32(destination[..CrcCoveredLength]);
        writer.WriteUInt32(crc32);
    }

    private static FlushPublicationState Read(ReadOnlySpan<byte> source, string path)
    {
        var reader = new SpanReader(source);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException($"Flush publication marker '{path}' has invalid magic.");

        int version = reader.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Flush publication marker '{path}' has unsupported version {version}.");

        int fileSize = reader.ReadInt32();
        if (fileSize != FileSize)
            throw new InvalidDataException($"Flush publication marker '{path}' has invalid file size {fileSize}.");

        long segmentId = reader.ReadInt64();
        long checkpointLsn = reader.ReadInt64();
        int statusValue = reader.ReadInt32();
        _ = reader.ReadInt32();
        long createdAtUtcTicks = reader.ReadInt64();
        uint storedCrc32 = reader.ReadUInt32();
        uint actualCrc32 = Crc32.HashToUInt32(source[..CrcCoveredLength]);

        if (storedCrc32 != actualCrc32)
        {
            throw new InvalidDataException(
                $"Flush publication marker '{path}' has CRC32 mismatch (expected 0x{storedCrc32:X8}, got 0x{actualCrc32:X8}).");
        }

        if (segmentId <= 0
            || checkpointLsn < 0
            || createdAtUtcTicks <= 0
            || statusValue is not (int)FlushPublicationStatus.Pending and not (int)FlushPublicationStatus.Committed)
        {
            throw new InvalidDataException($"Flush publication marker '{path}' contains invalid state.");
        }

        return new FlushPublicationState(
            segmentId,
            checkpointLsn,
            (FlushPublicationStatus)statusValue,
            createdAtUtcTicks);
    }
}

/// <summary>待确认 Flush 段发布的持久化状态。</summary>
internal readonly record struct FlushPublicationState(
    long SegmentId,
    long CheckpointLsn,
    FlushPublicationStatus Status,
    long CreatedAtUtcTicks);

/// <summary>Flush 段发布标记状态。</summary>
internal enum FlushPublicationStatus
{
    /// <summary>段可能已 rename，但尚未由 durable checkpoint 确认。</summary>
    Pending = 0,

    /// <summary>段已由 durable checkpoint 确认；遗留 marker 仅等待清理。</summary>
    Committed = 1,
}
