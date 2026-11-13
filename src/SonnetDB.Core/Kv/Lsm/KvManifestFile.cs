using System.Buffers.Binary;
using System.IO.Hashing;

namespace SonnetDB.Kv.Lsm;

/// <summary>
/// manifest 读取结果。
/// </summary>
/// <param name="Edits">按顺序解析出的有效编辑。</param>
/// <param name="ValidLength">最后一条有效记录结束的偏移。</param>
/// <param name="IgnoredTailBytes">被当作撕裂尾部而忽略的字节数。</param>
internal sealed record KvManifestReadResult(IReadOnlyList<KvVersionEdit> Edits, long ValidLength, long IgnoredTailBytes);

/// <summary>
/// manifest 文件格式：<c>header(16B) | record*</c>。
/// </summary>
/// <remarks>
/// <para>header：<c>"SDBKVMAN"(8) | version(int32 LE) | CRC32(前 12 字节)</c>。</para>
/// <para>record：<c>length(uint32 LE) | CRC32(payload, uint32 LE) | payload(<see cref="KvVersionEdit"/>)</c>。</para>
/// <para>
/// 撕裂判定：只有<b>文件最后一条</b>记录不完整或 CRC 不符时才视为崩溃导致的未提交尾部并忽略；
/// 若损坏记录之后仍有字节，说明已提交的历史被破坏，按损坏拒绝打开（fail closed）。
/// </para>
/// </remarks>
internal static class KvManifestFile
{
    /// <summary>header 字节数。</summary>
    internal const int HeaderSize = 16;

    /// <summary>记录头字节数。</summary>
    internal const int RecordHeaderSize = 8;

    /// <summary>单条记录载荷上限。</summary>
    internal const int MaxRecordBytes = 64 * 1024 * 1024;

    /// <summary>早期 LSM manifest 格式；当前读取器继续接受，成功打开时重写为 v2。</summary>
    internal const int LegacyVersion = 1;

    /// <summary>当前格式版本；v2 声明 generation v3 布局与恢复合同，阻止忽略布局标记的旧 LSM 写者。</summary>
    internal const int CurrentVersion = 2;

    private static ReadOnlySpan<byte> Magic => "SDBKVMAN"u8;

    /// <summary>读取并解析整个 manifest。</summary>
    /// <exception cref="InvalidDataException">header 无效、非尾部记录损坏或 CRC 正确的记录无法解析。</exception>
    internal static KvManifestReadResult Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderSize
            || !bytes.AsSpan(0, 8).SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) != Crc32.HashToUInt32(bytes.AsSpan(0, 12)))
        {
            throw Corrupt(path, "header is invalid");
        }
        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        if (version is not (LegacyVersion or CurrentVersion))
            throw Corrupt(path, $"format version {version} is not supported");

        var edits = new List<KvVersionEdit>();
        long position = HeaderSize;
        while (position < bytes.Length)
        {
            long remaining = bytes.Length - position;
            if (remaining < RecordHeaderSize)
                break;
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)position));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)position + 4));
            long end = position + RecordHeaderSize + length;
            if (end > bytes.Length)
                break;
            if (length > MaxRecordBytes)
                throw Corrupt(path, $"record at offset {position} exceeds the size limit");

            ReadOnlySpan<byte> payload = bytes.AsSpan((int)position + RecordHeaderSize, (int)length);
            if (Crc32.HashToUInt32(payload) != crc)
            {
                if (end == bytes.Length)
                    break;
                throw Corrupt(path, $"record at offset {position} fails CRC and is followed by more data");
            }

            try
            {
                edits.Add(KvVersionEdit.Decode(payload));
            }
            catch (InvalidDataException exception)
            {
                throw Corrupt(path, $"record at offset {position} cannot be decoded: {exception.Message}", exception);
            }
            position = end;
        }

        return new KvManifestReadResult(edits, position, bytes.Length - position);
    }

    /// <summary>创建只写 header 的新 manifest（不 fsync，由调用方在写入首条记录后统一 fsync）。</summary>
    /// <exception cref="IOException">文件已存在。</exception>
    internal static FileStream Create(string path)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            WriteHeader(stream);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>写入独立快照 manifest 的格式头。</summary>
    internal static void WriteHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], CurrentVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Crc32.HashToUInt32(header[..12]));
        stream.Write(header);
    }

    /// <summary>以追加方式打开已有 manifest，并把撕裂尾部截断到 <paramref name="validLength"/>。</summary>
    internal static FileStream OpenForAppend(string path, long validLength)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(validLength);
        stream.Seek(validLength, SeekOrigin.Begin);
        return stream;
    }

    /// <summary>写入一条记录（不 fsync）。</summary>
    internal static void WriteRecord(Stream stream, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxRecordBytes)
            throw new InvalidOperationException("KV-LSM manifest record exceeds the size limit.");
        Span<byte> header = stackalloc byte[RecordHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Crc32.HashToUInt32(payload));
        stream.Write(header);
        stream.Write(payload);
    }

    private static InvalidDataException Corrupt(string path, string reason, Exception? inner = null)
        => new($"KV-LSM manifest '{path}' is corrupt: {reason}.", inner);
}
