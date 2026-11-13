using System.Buffers.Binary;
using System.IO.Hashing;

namespace SonnetDB.Kv;

/// <summary>LSM 布局目录的种类。</summary>
internal enum KvLsmLayoutKind
{
    /// <summary>目录本身是一个独立的 LSM 实例（CURRENT/MANIFEST/sst/wal 都在该目录）。</summary>
    Standalone = 1,

    /// <summary>目录只是逻辑 keyspace 的附属目录，数据位于共享 <c>KvLsmStore</c> 中的 <see cref="KvLsmLayoutMarker.StorageId"/> 前缀下。</summary>
    Shared = 2,
}

/// <summary>LSM 布局标记的内容。</summary>
/// <param name="Kind">布局种类。</param>
/// <param name="StorageId">共享实例中的逻辑 keyspace 编号；独立实例为 0。</param>
/// <param name="FormatVersion">普通 LSM 布局为 v3，离线回滚屏障为 v4。</param>
internal readonly record struct KvLsmLayoutMarker(KvLsmLayoutKind Kind, long StorageId, int FormatVersion = KvLsmLayoutMarkerFile.Version);

/// <summary>
/// LSM 布局标记：写在 <c>generation.meta</c> 中的 v3 记录，离线回滚期间使用 v4 屏障。
/// </summary>
/// <remarks>
/// <para>
/// 复用 legacy generation 文件的魔数与位置，是有意的降级防线：旧版本二进制读取 <c>generation.meta</c> 时只接受 v1/v2，
/// 遇到 v3 抛出 <see cref="InvalidDataException"/>，不会把 LSM 目录当成空 keyspace 打开并写入新的 legacy WAL。
/// </para>
/// <para>
/// 布局（64 字节，little-endian）：<c>"SDBKVGEN" | version=3 (int32) | kind (int32) | storageId (int64) |
/// createdUtcTicks (int64) | 保留 | crc32(前 60 字节)</c>。
/// </para>
/// </remarks>
internal static class KvLsmLayoutMarkerFile
{
    /// <summary>标记使用的 generation 文件版本。</summary>
    internal const int Version = 3;

    /// <summary>离线回滚期间的降级屏障；旧 LSM 版本也必须拒绝打开。</summary>
    internal const int RollbackVersion = 4;

    private const int FileSize = 64;
    private static ReadOnlySpan<byte> Magic => "SDBKVGEN"u8;

    /// <summary>读取标记；文件不存在或是 legacy v1/v2 generation 文件时返回 false。</summary>
    /// <exception cref="InvalidDataException">v3 标记损坏。</exception>
    internal static bool TryRead(string rootDirectory, out KvLsmLayoutMarker marker)
    {
        marker = default;
        string path = Path.Combine(rootDirectory, KvGenerationFile.FileName);
        if (!File.Exists(path))
            return false;

        Span<byte> content = stackalloc byte[FileSize];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.ReadAtLeast(content, FileSize, throwOnEndOfStream: false) != FileSize)
                return false; // 截断的 legacy 文件交给 legacy 打开路径报告
        }

        if (!content[..8].SequenceEqual(Magic))
            return false;
        int version = BinaryPrimitives.ReadInt32LittleEndian(content.Slice(8, 4));
        if (version is not (Version or RollbackVersion))
            return false;

        int kind = BinaryPrimitives.ReadInt32LittleEndian(content.Slice(12, 4));
        long storageId = BinaryPrimitives.ReadInt64LittleEndian(content.Slice(16, 8));
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(60, 4));
        if (expectedCrc != Crc32.HashToUInt32(content[..60])
            || kind is not ((int)KvLsmLayoutKind.Standalone or (int)KvLsmLayoutKind.Shared)
            || (kind == (int)KvLsmLayoutKind.Standalone && storageId != 0)
            || (kind == (int)KvLsmLayoutKind.Shared && storageId <= 0))
        {
            throw new InvalidDataException($"KV-LSM layout marker '{path}' is invalid.");
        }

        marker = new KvLsmLayoutMarker((KvLsmLayoutKind)kind, storageId, version);
        return true;
    }

    /// <summary>原子写入标记（tmp → fsync → 改名 → 目录 fsync）；覆盖已有的 generation 文件。</summary>
    internal static void Write(string rootDirectory, KvLsmLayoutMarker marker)
    {
        if (marker.Kind is not (KvLsmLayoutKind.Standalone or KvLsmLayoutKind.Shared))
            throw new ArgumentOutOfRangeException(nameof(marker));
        if (marker.FormatVersion is not (Version or RollbackVersion))
            throw new ArgumentOutOfRangeException(nameof(marker));
        if (marker.Kind == KvLsmLayoutKind.Shared ? marker.StorageId <= 0 : marker.StorageId != 0)
            throw new ArgumentOutOfRangeException(nameof(marker), "KV-LSM storage id does not match the layout kind.");

        Directory.CreateDirectory(rootDirectory);
        string path = Path.Combine(rootDirectory, KvGenerationFile.FileName);
        Span<byte> content = stackalloc byte[FileSize];
        content.Clear();
        Magic.CopyTo(content[..8]);
        BinaryPrimitives.WriteInt32LittleEndian(content.Slice(8, 4), marker.FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(content.Slice(12, 4), (int)marker.Kind);
        BinaryPrimitives.WriteInt64LittleEndian(content.Slice(16, 8), marker.StorageId);
        BinaryPrimitives.WriteInt64LittleEndian(content.Slice(24, 8), DateTime.UtcNow.Ticks);
        BinaryPrimitives.WriteUInt32LittleEndian(content.Slice(60, 4), Crc32.HashToUInt32(content[..60]));

        KvMigrationFileIo.WriteAtomic(path, content);
        SonnetDB.Wal.DirectoryFsync.FlushRequired(rootDirectory);
    }
}
