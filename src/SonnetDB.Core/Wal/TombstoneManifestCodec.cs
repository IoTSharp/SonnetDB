using System.Buffers;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using SonnetDB.Engine;
using SonnetDB.IO;

namespace SonnetDB.Wal;

/// <summary>
/// 墓碑清单文件（<c>tombstones.tslmanifest</c>）的序列化与反序列化器。
/// <para>
/// 物理布局（二进制，little-endian）：
/// <code>
/// TombstoneManifestHeader (32B)
///   Magic = "SDBTOMB!"  (8B)
///   FormatVersion = 1   (4B)
///   HeaderSize = 32     (4B)
///   TombstoneCount      (4B)
///   Reserved            (12B)
///
/// Tombstone[N]（每条变长）
///   ulong  SeriesId            (8B)
///   long   FromTimestamp       (8B)
///   long   ToTimestamp         (8B)
///   long   CreatedLsn          (8B)
///   uint16 FieldNameUtf8Length (2B)
///   byte[] FieldNameUtf8       变长
///
/// TombstoneManifestFooter (16B)
///   Crc32                      (4B)  整个 Tombstone[] 区域的 CRC32
///   Magic = "SDBTOMB!"         (8B)
///   Reserved                   (4B)
/// </code>
/// </para>
/// <para>
/// 写入策略：临时文件 + 原子 rename（崩溃安全）。
/// </para>
/// </summary>
public static class TombstoneManifestCodec
{
    /// <summary>清单文件名。</summary>
    public const string FileName = "tombstones.tslmanifest";

    /// <summary>上一代已校验清单的备份后缀。</summary>
    public const string BackupSuffix = ".bak";

    private static readonly byte[] _magic = "SDBTOMB!"u8.ToArray();
    private static readonly Encoding _utf8 = Encoding.UTF8;
    private static readonly object _saveSync = new();

    private const int _formatVersion = 1;
    private const int _headerSize = 32;
    private const int _footerSize = 16;

    // ── Header layout constants ──────────────────────────────────────────────
    // Magic(8) + FormatVersion(4) + HeaderSize(4) + TombstoneCount(4) + Reserved(12) = 32

    // ── Footer layout constants ──────────────────────────────────────────────
    // Crc32(4) + Magic(8) + Reserved(4) = 16

    /// <summary>
    /// 从指定路径加载墓碑清单；文件不存在时返回空集合。
    /// </summary>
    /// <param name="path">清单文件完整路径。</param>
    /// <returns>加载的墓碑只读列表；文件不存在时返回空列表。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> 为 null 时抛出。</exception>
    /// <exception cref="InvalidDataException">Magic / FormatVersion / Crc32 校验失败时抛出。</exception>
    public static IReadOnlyList<Tombstone> Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
            return [];

        // Windows 原子替换要求并发读句柄允许删除共享；读者仍持有原文件代次，不会读到半写内容。
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return Load(fs);
    }

    /// <summary>
    /// 加载墓碑清单；主文件损坏或缺失时读取上一代已校验备份。
    /// 主文件和备份均不可用时保留原始异常，避免静默丢弃删除语义。
    /// </summary>
    /// <param name="path">主清单文件完整路径。</param>
    /// <returns>主清单或上一代备份中的墓碑列表。</returns>
    public static IReadOnlyList<Tombstone> LoadWithFallback(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string backupPath = path + BackupSuffix;
        if (!File.Exists(path))
            return File.Exists(backupPath) ? Load(backupPath) : [];

        try
        {
            return Load(path);
        }
        catch (Exception primaryException) when (IsRecoverableReadFailure(primaryException) && File.Exists(backupPath))
        {
            try
            {
                var recovered = Load(backupPath);
                System.Diagnostics.Trace.TraceWarning(
                    $"Tombstone manifest '{path}' is unavailable; loaded last verified backup '{backupPath}'.");
                return recovered;
            }
            catch (Exception backupException) when (IsRecoverableReadFailure(backupException))
            {
                throw new InvalidDataException(
                    $"Tombstone manifest '{path}' and its backup are both unavailable.",
                    new AggregateException(primaryException, backupException));
            }
        }
    }

    /// <summary>
    /// 将墓碑列表持久化到指定路径（临时文件 + 原子 rename）。
    /// </summary>
    /// <param name="path">目标文件完整路径。</param>
    /// <param name="tombstones">要持久化的墓碑列表。</param>
    /// <param name="tempSuffix">临时文件后缀（默认 ".tmp"）。</param>
    /// <exception cref="ArgumentNullException">任何参数为 null 时抛出。</exception>
    public static void Save(string path, IReadOnlyList<Tombstone> tombstones, string tempSuffix = ".tmp")
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(tombstones);
        ArgumentException.ThrowIfNullOrEmpty(tempSuffix);

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        if (directory.Length > 0)
            Directory.CreateDirectory(directory);

        lock (_saveSync)
        {
            // 每次写入使用唯一临时文件，避免同进程并发保存或异常遗留的固定 .tmp 相互覆盖。
            string tempPath = CreateTemporaryPath(fullPath, tempSuffix);
            try
            {
                WriteManifest(tempPath, tombstones);
                PreserveVerifiedGeneration(fullPath);

                File.Move(tempPath, fullPath, overwrite: true);
                EnsureInitialBackup(fullPath);
                WalCheckpointFile.FlushDirectoryBestEffort(directory);
            }
            finally
            {
                TryDeleteTemporaryFile(tempPath);
            }
        }
    }

    /// <summary>生成同目录唯一临时文件路径，保证 rename 不跨文件系统。</summary>
    private static string CreateTemporaryPath(string path, string tempSuffix) =>
        $"{path}{tempSuffix}.{Guid.NewGuid():N}";

    /// <summary>将完整清单写入临时文件并强制刷新到稳定存储。</summary>
    private static void WriteManifest(string path, IReadOnlyList<Tombstone> tombstones)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var bs = new BufferedStream(fs, 65536);
        Save(tombstones, bs);
        bs.Flush();
        fs.Flush(true);
    }

    /// <summary>主清单有效时，将其原子复制为上一代备份；损坏主文件不会污染已有备份。</summary>
    private static void PreserveVerifiedGeneration(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            _ = Load(path);
        }
        catch (InvalidDataException)
        {
            return;
        }

        WriteDurableCopy(path, path + BackupSuffix);
    }

    /// <summary>首次创建清单时同步建立有效备份，确保第一次异常写入后也可恢复。</summary>
    private static void EnsureInitialBackup(string path)
    {
        string backupPath = path + BackupSuffix;
        if (!File.Exists(backupPath))
            WriteDurableCopy(path, backupPath);
    }

    /// <summary>通过唯一临时文件和原子 rename 创建持久化副本。</summary>
    private static void WriteDurableCopy(string sourcePath, string destinationPath)
    {
        string tempPath = CreateTemporaryPath(destinationPath, ".tmp");
        try
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
                destination.Flush(true);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    /// <summary>判断读取失败是否允许尝试上一代备份。</summary>
    private static bool IsRecoverableReadFailure(Exception exception) =>
        exception is InvalidDataException or IOException;

    /// <summary>清理尚未 rename 的临时文件，不掩盖主流程异常。</summary>
    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ── 私有实现 ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<Tombstone> Load(Stream source)
    {
        // 读取 header
        byte[] headerBuf = ArrayPool<byte>.Shared.Rent(_headerSize);
        try
        {
            int read = ReadExact(source, headerBuf, 0, _headerSize);
            if (read < _headerSize)
                throw new InvalidDataException("TombstoneManifest: header is truncated.");

            var reader = new SpanReader(headerBuf.AsSpan(0, _headerSize));
            ReadOnlySpan<byte> magic = reader.ReadBytes(8);
            if (!magic.SequenceEqual(_magic))
                throw new InvalidDataException("TombstoneManifest: invalid magic in header.");

            int version = reader.ReadInt32();
            if (version != _formatVersion)
                throw new InvalidDataException($"TombstoneManifest: unsupported format version {version}.");

            int hSize = reader.ReadInt32();
            if (hSize != _headerSize)
                throw new InvalidDataException($"TombstoneManifest: unexpected header size {hSize}.");

            int tombstoneCount = reader.ReadInt32();
            if (tombstoneCount < 0)
                throw new InvalidDataException("TombstoneManifest: negative tombstone count.");

            // Read all tombstone bytes into a buffer for CRC32 calculation
            // We'll accumulate tombstone bytes and then verify footer
            var crcHasher = new Crc32();
            var result = new List<Tombstone>(tombstoneCount);

            for (int i = 0; i < tombstoneCount; i++)
            {
                // Fixed part: SeriesId(8) + From(8) + To(8) + CreatedLsn(8) + FieldNameLen(2) = 34 bytes
                byte[] fixedBuf = ArrayPool<byte>.Shared.Rent(34);
                try
                {
                    int fixedRead = ReadExact(source, fixedBuf, 0, 34);
                    if (fixedRead < 34)
                        throw new InvalidDataException($"TombstoneManifest: tombstone {i} is truncated (fixed part).");

                    crcHasher.Append(fixedBuf.AsSpan(0, 34));

                    var fixedReader = new SpanReader(fixedBuf.AsSpan(0, 34));
                    ulong seriesId = fixedReader.ReadUInt64();
                    long fromTs = fixedReader.ReadInt64();
                    long toTs = fixedReader.ReadInt64();
                    long createdLsn = fixedReader.ReadInt64();
                    int fieldNameLen = fixedReader.ReadUInt16();

                    // Variable part: FieldNameUtf8
                    byte[] fieldBuf = ArrayPool<byte>.Shared.Rent(Math.Max(fieldNameLen, 1));
                    try
                    {
                        if (fieldNameLen > 0)
                        {
                            int fieldRead = ReadExact(source, fieldBuf, 0, fieldNameLen);
                            if (fieldRead < fieldNameLen)
                                throw new InvalidDataException($"TombstoneManifest: tombstone {i} field name is truncated.");

                            crcHasher.Append(fieldBuf.AsSpan(0, fieldNameLen));
                        }

                        string fieldName = fieldNameLen > 0
                            ? _utf8.GetString(fieldBuf, 0, fieldNameLen)
                            : string.Empty;

                        result.Add(new Tombstone(seriesId, fieldName, fromTs, toTs, createdLsn));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(fieldBuf);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(fixedBuf);
                }
            }

            // Read and verify footer
            byte[] footerBuf = ArrayPool<byte>.Shared.Rent(_footerSize);
            try
            {
                int footerRead = ReadExact(source, footerBuf, 0, _footerSize);
                if (footerRead < _footerSize)
                    throw new InvalidDataException("TombstoneManifest: footer is truncated.");

                uint storedCrc = MemoryMarshal.Read<uint>(footerBuf.AsSpan(0, 4));
                ReadOnlySpan<byte> footerMagic = footerBuf.AsSpan(4, 8);

                if (!footerMagic.SequenceEqual(_magic))
                    throw new InvalidDataException("TombstoneManifest: invalid magic in footer.");

                uint computedCrc = crcHasher.GetCurrentHashAsUInt32();
                if (computedCrc != storedCrc)
                    throw new InvalidDataException(
                        $"TombstoneManifest: CRC32 mismatch (expected 0x{storedCrc:X8}, got 0x{computedCrc:X8}).");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(footerBuf);
            }

            return result.AsReadOnly();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuf);
        }
    }

    private static void Save(IReadOnlyList<Tombstone> tombstones, Stream destination)
    {
        // Write header
        byte[] headerBuf = ArrayPool<byte>.Shared.Rent(_headerSize);
        try
        {
            headerBuf.AsSpan(0, _headerSize).Clear();
            var writer = new SpanWriter(headerBuf.AsSpan(0, _headerSize));
            writer.WriteBytes(_magic);
            writer.WriteInt32(_formatVersion);
            writer.WriteInt32(_headerSize);
            writer.WriteInt32(tombstones.Count);
            // Reserved (12 bytes) already zeroed
            destination.Write(headerBuf, 0, _headerSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuf);
        }

        // Write tombstones and accumulate CRC32
        var crcHasher = new Crc32();

        foreach (var tomb in tombstones)
        {
            int fieldNameLen = _utf8.GetByteCount(tomb.FieldName);
            // Fixed: SeriesId(8) + From(8) + To(8) + CreatedLsn(8) + FieldNameLen(2) = 34
            int totalSize = 34 + fieldNameLen;
            byte[] buf = ArrayPool<byte>.Shared.Rent(totalSize);
            try
            {
                buf.AsSpan(0, totalSize).Clear();
                var writer = new SpanWriter(buf.AsSpan(0, totalSize));
                writer.WriteUInt64(tomb.SeriesId);
                writer.WriteInt64(tomb.FromTimestamp);
                writer.WriteInt64(tomb.ToTimestamp);
                writer.WriteInt64(tomb.CreatedLsn);
                writer.WriteUInt16((ushort)fieldNameLen);
                if (fieldNameLen > 0)
                {
                    int written = _utf8.GetBytes(tomb.FieldName, writer.FreeSpan);
                    writer.Advance(written);
                }

                crcHasher.Append(buf.AsSpan(0, totalSize));
                destination.Write(buf, 0, totalSize);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        // Write footer: Crc32(4) + Magic(8) + Reserved(4) = 16
        uint crc = crcHasher.GetCurrentHashAsUInt32();
        byte[] footerBuf = ArrayPool<byte>.Shared.Rent(_footerSize);
        try
        {
            footerBuf.AsSpan(0, _footerSize).Clear();
            var writer = new SpanWriter(footerBuf.AsSpan(0, _footerSize));
            writer.WriteUInt32(crc);
            writer.WriteBytes(_magic);
            // Reserved (4 bytes) already zeroed
            destination.Write(footerBuf, 0, _footerSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(footerBuf);
        }
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
}
