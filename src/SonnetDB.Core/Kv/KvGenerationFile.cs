using System.Buffers.Binary;
using System.IO.Hashing;

namespace SonnetDB.Kv;

/// <summary>持久化 keyspace 当前 generation，防止 clear 后旧 state 在崩溃恢复时重新可见。</summary>
internal static class KvGenerationFile
{
    private const int FileSize = 64;
    private const int CurrentVersion = 2;
    private static ReadOnlySpan<byte> Magic => "SDBKVGEN"u8;

    public const string FileName = "generation.meta";

    public static long Load(string rootDirectory) => LoadMetadata(rootDirectory).Generation;

    internal static KvGenerationMetadata LoadMetadata(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, FileName);
        if (!File.Exists(path))
            return new KvGenerationMetadata(0, 0, 0, CurrentVersion);

        Span<byte> content = stackalloc byte[FileSize];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.ReadAtLeast(content, FileSize, throwOnEndOfStream: false) != FileSize)
            throw new InvalidDataException("KV generation metadata is truncated.");

        int version = BinaryPrimitives.ReadInt32LittleEndian(content.Slice(8, 4));
        long generation = BinaryPrimitives.ReadInt64LittleEndian(content.Slice(16, 8));
        long createdUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(content.Slice(24, 8));
        long resetSequence = version >= 2
            ? BinaryPrimitives.ReadInt64LittleEndian(content.Slice(32, 8))
            : 0;
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(60, 4));
        if (!content[..8].SequenceEqual(Magic)
            || version is < 1 or > CurrentVersion
            || generation < 0
            || createdUtcTicks < 0
            || resetSequence < 0
            || version >= 2 && generation > 0 && resetSequence == 0
            || expectedCrc != Crc32.HashToUInt32(content[..60]))
        {
            throw new InvalidDataException("KV generation metadata is invalid.");
        }

        return new KvGenerationMetadata(generation, createdUtcTicks, resetSequence, version);
    }

    public static void Save(string rootDirectory, long generation, long resetSequence = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentOutOfRangeException.ThrowIfNegative(resetSequence);
        if (generation == 0 && resetSequence != 0)
            throw new ArgumentException("Generation zero cannot have a reset sequence.", nameof(resetSequence));

        int version = resetSequence > 0 ? CurrentVersion : 1;
        Directory.CreateDirectory(rootDirectory);
        string path = Path.Combine(rootDirectory, FileName);
        string tempPath = path + ".tmp";
        Span<byte> content = stackalloc byte[FileSize];
        Magic.CopyTo(content[..8]);
        BinaryPrimitives.WriteInt32LittleEndian(content.Slice(8, 4), version);
        BinaryPrimitives.WriteInt64LittleEndian(content.Slice(16, 8), generation);
        BinaryPrimitives.WriteInt64LittleEndian(content.Slice(24, 8), DateTime.UtcNow.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(content.Slice(32, 8), resetSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(content.Slice(60, 4), Crc32.HashToUInt32(content[..60]));

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
        SonnetDB.Wal.DirectoryFsync.FlushRequired(rootDirectory);
    }
}

internal readonly record struct KvGenerationMetadata(
    long Generation,
    long CreatedUtcTicks,
    long ResetSequence,
    int Version);
