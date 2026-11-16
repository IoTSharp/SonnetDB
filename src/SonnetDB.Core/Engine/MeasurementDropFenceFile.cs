using System.Buffers.Binary;
using System.IO.Hashing;
using SonnetDB.Wal;

namespace SonnetDB.Engine;

/// <summary>
/// 持久化 DROP MEASUREMENT 的序列代际屏障。
/// </summary>
/// <remarks>
/// SeriesId 由 measurement 名称和 tags 确定性计算，因此同名 measurement 重建后可能复用旧 ID。
/// 文件记录被删除序列及其 DROP 前最后一个 WAL LSN；重开时只过滤屏障之前的旧墓碑，DROP 后的新删除仍然生效。
/// </remarks>
internal static class MeasurementDropFenceFile
{
    private static readonly byte[] Magic = "SDBDFEN1"u8.ToArray();
    private const int Version = 1;
    private const int HeaderSize = 32;
    private const int EntrySize = 16;
    private const int FooterSize = 4;
    private const int MaxEntries = 4_000_000;

    internal static IReadOnlyDictionary<ulong, long> Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path))
                throw new InvalidDataException($"Measurement drop fence '{path}' is a directory.");
            return new Dictionary<ulong, long>();
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderSize + FooterSize)
            throw new InvalidDataException($"Measurement drop fence '{path}' has an invalid length.");

        ReadOnlySpan<byte> source = bytes;
        if (!source[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(source[8..]) != Version
            || BinaryPrimitives.ReadInt32LittleEndian(source[12..]) != HeaderSize)
        {
            throw new InvalidDataException($"Measurement drop fence '{path}' has an invalid header.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(source[16..]);
        if (count < 0 || count > MaxEntries
            || bytes.Length != checked(HeaderSize + count * EntrySize + FooterSize))
        {
            throw new InvalidDataException($"Measurement drop fence '{path}' has an invalid entry count.");
        }

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(source[^FooterSize..]);
        uint actualCrc = Crc32.HashToUInt32(source[..^FooterSize]);
        if (actualCrc != expectedCrc)
            throw new InvalidDataException($"Measurement drop fence '{path}' failed its checksum.");

        var fences = new Dictionary<ulong, long>(count);
        int offset = HeaderSize;
        for (int i = 0; i < count; i++)
        {
            ulong seriesId = BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);
            long dropLsn = BinaryPrimitives.ReadInt64LittleEndian(source[(offset + 8)..]);
            if (dropLsn < 0 || !fences.TryAdd(seriesId, dropLsn))
                throw new InvalidDataException($"Measurement drop fence '{path}' contains a duplicate or invalid series entry.");
            offset += EntrySize;
        }

        return fences;
    }

    internal static void SaveMerged(
        string path,
        IReadOnlySet<ulong> seriesIds,
        long dropLsn)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(seriesIds);
        ArgumentOutOfRangeException.ThrowIfNegative(dropLsn);
        if (seriesIds.Count == 0)
            return;

        var fences = new Dictionary<ulong, long>(Load(path));
        foreach (ulong seriesId in seriesIds)
        {
            if (fences.TryGetValue(seriesId, out long existing) && existing >= dropLsn)
                continue;
            fences[seriesId] = dropLsn;
        }

        if (fences.Count > MaxEntries)
            throw new InvalidOperationException("Measurement drop fence entries exceed the configured limit.");

        byte[] bytes = new byte[checked(HeaderSize + fences.Count * EntrySize + FooterSize)];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), fences.Count);

        int offset = HeaderSize;
        foreach ((ulong seriesId, long fenceLsn) in fences.OrderBy(static pair => pair.Key))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), seriesId);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 8), fenceLsn);
            offset += EntrySize;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(bytes.Length - FooterSize),
            Crc32.HashToUInt32(bytes.AsSpan(0, bytes.Length - FooterSize)));

        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Measurement drop fence must be inside the database directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string tempPath = $"{path}.tmp.{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
            DirectoryFsync.FlushRequired(directory);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static bool Covers(
        IReadOnlyDictionary<ulong, long> fences,
        ulong seriesId,
        long createdLsn)
        => fences.TryGetValue(seriesId, out long dropLsn) && createdLsn <= dropLsn;
}
