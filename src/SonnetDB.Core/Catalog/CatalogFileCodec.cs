using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SonnetDB.Buffers;
using SonnetDB.IO;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Catalog;

/// <summary>
/// 目录文件（.SDBCAT）的序列化与反序列化器。
/// </summary>
/// <remarks>
/// <para>
/// 文件布局（二进制，little-endian）：
/// <code>
/// 偏移      大小   字段
/// 0         64     CatalogFileHeader（含 magic、版本、条目数）
/// 64        变长   Entry 0
///   +0       8     SeriesIdValue (ulong)
///   +8       8     CreatedAtUtcTicks (long)
///   +16      4     MeasurementUtf8Len (int)
///   +20      变长  MeasurementUtf8Bytes
///   +…       4     TagCount (int)
///   +…       重复 TagCount 次：
///              4     KeyUtf8Len (int) + KeyUtf8Bytes
///              4     ValueUtf8Len (int) + ValueUtf8Bytes
/// ...        变长   Entry 1, Entry 2, …
/// </code>
/// </para>
/// </remarks>
public static class CatalogFileCodec
{
    /// <summary>目录文件扩展名。</summary>
    public const string FileExtension = ".SDBCAT";

    private static readonly Encoding _utf8 = Encoding.UTF8;

    // ── 路径重载 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 把目录全量持久化到指定路径（覆写 + flush + fsync，使用临时文件原子替换）。
    /// </summary>
    /// <param name="catalog">要持久化的目录。</param>
    /// <param name="path">目标文件路径。</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> 或 <paramref name="path"/> 为 null。</exception>
    public static void Save(SeriesCatalog catalog, string path)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(path);

        string tmpPath = path + ".tmp";

        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bs = new BufferedStream(fs, 4096))
        {
            Save(catalog, bs);
            bs.Flush();
            fs.Flush(true); // fsync
        }

        File.Move(tmpPath, path, overwrite: true);
        // 目录 fsync：使原子改名对崩溃/掉电可见（catalog 必须早于 WAL 回收持久化，
        // 否则被回收的 CreateSeries 记录无法通过 catalog 文件解析 SeriesId）。#189
        SonnetDB.Wal.DirectoryFsync.FlushBestEffort(Path.GetDirectoryName(path) ?? string.Empty);
    }

    /// <summary>
    /// 从指定路径加载目录；文件不存在时返回空 <see cref="SeriesCatalog"/>。
    /// </summary>
    /// <param name="path">目录文件路径。</param>
    /// <returns>加载的 <see cref="SeriesCatalog"/>；文件不存在时返回空目录。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> 为 null。</exception>
    /// <exception cref="InvalidDataException">文件格式不正确或校验失败时抛出。</exception>
    public static SeriesCatalog Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
            return new SeriesCatalog();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(fs);
    }

    // ── Stream 重载 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 把目录全量序列化到 <see cref="Stream"/>（不关闭流）。
    /// </summary>
    /// <param name="catalog">要序列化的目录。</param>
    /// <param name="destination">目标输出流。</param>
    /// <exception cref="ArgumentNullException">任何参数为 null 时抛出。</exception>
    public static void Save(SeriesCatalog catalog, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(destination);

        var entries = catalog.Snapshot();

        // 写入文件头
        WriteHeader(destination, entries.Count);

        // 写入每条 entry
        foreach (var entry in entries)
            WriteEntry(destination, entry);
    }

    /// <summary>
    /// 从 <see cref="Stream"/> 加载目录到一个新的实例。
    /// </summary>
    /// <param name="source">源输入流。</param>
    /// <returns>加载的 <see cref="SeriesCatalog"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 为 null。</exception>
    /// <exception cref="InvalidDataException">文件格式不正确或校验失败时抛出。</exception>
    public static SeriesCatalog Load(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        int headerSize = FormatSizes.CatalogFileHeaderSize;
        byte[] headerBuf = ArrayPool<byte>.Shared.Rent(headerSize);
        try
        {
            // 读取固定头部
            int read = ReadExact(source, headerBuf, 0, headerSize);
            if (read < headerSize)
                throw new InvalidDataException("Catalog file is truncated: header incomplete.");

            var reader = new SpanReader(headerBuf.AsSpan(0, headerSize));
            var header = reader.ReadStruct<CatalogFileHeader>();

            // 校验 magic
            if (!header.Magic.AsReadOnlySpan().SequenceEqual(TsdbMagic.Catalog))
                throw new InvalidDataException(
                    $"Catalog file magic mismatch: expected \"SDBCATv1\".");

            // 校验版本
            if (header.FormatVersion != TsdbMagic.FormatVersion)
                throw new InvalidDataException(
                    $"Catalog file format version mismatch: expected {TsdbMagic.FormatVersion}, got {header.FormatVersion}.");

            // 校验头部大小
            if (header.HeaderSize != FormatSizes.CatalogFileHeaderSize)
                throw new InvalidDataException(
                    $"Catalog file header size mismatch: expected {FormatSizes.CatalogFileHeaderSize}, got {header.HeaderSize}.");

            int entryCount = header.EntryCount;
            var catalog = new SeriesCatalog();
            var entries = new List<SeriesEntry>(entryCount);

            for (int i = 0; i < entryCount; i++)
                entries.Add(ReadEntry(source));

            catalog.LoadEntries(entries);

            return catalog;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuf);
        }
    }

    // ── 私有写入辅助 ──────────────────────────────────────────────────────────

    private static void WriteHeader(Stream stream, int entryCount)
    {
        int headerSize = FormatSizes.CatalogFileHeaderSize;
        byte[] buf = ArrayPool<byte>.Shared.Rent(headerSize);
        try
        {
            var header = CatalogFileHeader.CreateNew(entryCount);
            var writer = new SpanWriter(buf.AsSpan(0, headerSize));
            writer.WriteStruct(in header);
            stream.Write(buf, 0, headerSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void WriteEntry(Stream stream, SeriesEntry entry)
    {
        // 按 canonical 顺序（key 升序）获取 tag 列表
        var tags = entry.Key.Tags;
        var sortedTagKeys = new string[tags.Count];
        int ti = 0;
        foreach (var k in tags.Keys)
            sortedTagKeys[ti++] = k;
        Array.Sort(sortedTagKeys, StringComparer.Ordinal);

        // 预计算字节长度以确定总缓冲大小
        int measurementBytes = _utf8.GetByteCount(entry.Measurement);
        int totalSize = 8 + 8 + 4 + measurementBytes + 4; // id + createdAt + mLen + mBytes + tagCount

        var tagData = new (string key, string value, int keyBytes, int valueBytes)[sortedTagKeys.Length];
        for (int i = 0; i < sortedTagKeys.Length; i++)
        {
            string k = sortedTagKeys[i];
            string v = tags[k];
            int kb = _utf8.GetByteCount(k);
            int vb = _utf8.GetByteCount(v);
            tagData[i] = (k, v, kb, vb);
            totalSize += 4 + kb + 4 + vb;
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            var writer = new SpanWriter(buf.AsSpan(0, totalSize));

            writer.WriteUInt64(entry.Id);
            writer.WriteInt64(entry.CreatedAtUtcTicks);
            writer.WriteInt32(measurementBytes);
            int written = _utf8.GetBytes(entry.Measurement, writer.FreeSpan);
            writer.Advance(written);

            writer.WriteInt32(tagData.Length);
            foreach (var (k, v, kb, vb) in tagData)
            {
                writer.WriteInt32(kb);
                written = _utf8.GetBytes(k, writer.FreeSpan);
                writer.Advance(written);

                writer.WriteInt32(vb);
                written = _utf8.GetBytes(v, writer.FreeSpan);
                writer.Advance(written);
            }

            stream.Write(buf, 0, totalSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // ── 私有读取辅助 ──────────────────────────────────────────────────────────

    private static SeriesEntry ReadEntry(Stream stream)
    {
        // 读取 SeriesIdValue + CreatedAtUtcTicks（固定 16 字节）
        Span<byte> fixedBuf = stackalloc byte[16];
        ReadExactSpan(stream, fixedBuf);
        ulong storedId = BinaryPrimitives.ReadUInt64LittleEndian(fixedBuf[..8]);
        long createdAt = BinaryPrimitives.ReadInt64LittleEndian(fixedBuf[8..]);

        // 读取 measurement
        string measurement = ReadString(stream);

        // 读取 tags
        int tagCount = ReadInt32(stream);
        if (tagCount < 0)
            throw new InvalidDataException("Invalid tag count in catalog entry.");

        var tags = new Dictionary<string, string>(tagCount, StringComparer.Ordinal);
        for (int i = 0; i < tagCount; i++)
        {
            string tagKey = ReadString(stream);
            string tagValue = ReadString(stream);
            tags[tagKey] = tagValue;
        }

        // 重建 SeriesKey 并校验 SeriesId
        var key = new SeriesKey(measurement, tags);
        ulong computedId = SeriesId.Compute(key);
        if (computedId != storedId)
            throw new InvalidDataException(
                $"SeriesId mismatch for series '{key.Canonical}': " +
                $"stored={storedId}, computed={computedId}.");

        return new SeriesEntry(computedId, key, measurement, tags, createdAt);
    }

    private static int ReadInt32(Stream stream)
    {
        Span<byte> buf = stackalloc byte[4];
        ReadExactSpan(stream, buf);
        return BinaryPrimitives.ReadInt32LittleEndian(buf);
    }

    private static string ReadString(Stream stream)
    {
        int byteCount = ReadInt32(stream);
        if (byteCount < 0)
            throw new InvalidDataException("Unexpected negative string length in catalog entry.");
        if (byteCount == 0)
            return string.Empty;

        byte[] buf = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            ReadExact(stream, buf, 0, byteCount);
            return _utf8.GetString(buf, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void ReadExactSpan(Stream stream, Span<byte> buffer)
    {
        int remaining = buffer.Length;
        int offset = 0;
        while (remaining > 0)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
                throw new InvalidDataException("Unexpected end of catalog stream.");
            offset += read;
            remaining -= read;
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
