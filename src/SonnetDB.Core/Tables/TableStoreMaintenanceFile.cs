using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;

namespace SonnetDB.Tables;

/// <summary>关系表一次性迁移与索引干净关闭状态的持久化标记。</summary>
internal static class TableStoreMaintenanceFile
{
    private const int FileSize = 96;
    private const int CurrentVersion = 1;
    private const int FingerprintOffset = 32;
    private const int FingerprintLength = 32;
    private const int CrcOffset = FileSize - sizeof(uint);
    private static ReadOnlySpan<byte> Magic => "SDBTBLMT"u8;

    internal const string LegacyMigrationFileName = "legacy-rows-v1.complete";
    internal const string CleanIndexesFileName = "indexes.clean";

    internal static bool IsLegacyMigrationComplete(
        string rootDirectory,
        long generation,
        ReadOnlySpan<byte> schemaFingerprint)
    {
        return TryRead(
            Path.Combine(rootDirectory, LegacyMigrationFileName),
            MarkerKind.LegacyMigration,
            out var marker)
            && marker.Generation == generation
            && marker.SchemaFingerprint.AsSpan().SequenceEqual(schemaFingerprint);
    }

    internal static void MarkLegacyMigrationComplete(
        string rootDirectory,
        long generation,
        ReadOnlySpan<byte> schemaFingerprint)
    {
        Save(
            Path.Combine(rootDirectory, LegacyMigrationFileName),
            MarkerKind.LegacyMigration,
            generation,
            sequence: 0,
            schemaFingerprint);
    }

    /// <summary>
    /// 消费上次干净关闭令牌。令牌在返回前持久删除，因此本次进程若崩溃，下次打开必定重建索引。
    /// </summary>
    internal static bool ConsumeCleanIndexes(
        string rootDirectory,
        long generation,
        long sequence,
        ReadOnlySpan<byte> schemaFingerprint)
    {
        string path = Path.Combine(rootDirectory, CleanIndexesFileName);
        if (!File.Exists(path))
            return false;

        bool matches = TryRead(path, MarkerKind.CleanIndexes, out var marker)
            && marker.Generation == generation
            && marker.Sequence == sequence
            && marker.SchemaFingerprint.AsSpan().SequenceEqual(schemaFingerprint);

        File.Delete(path);
        SonnetDB.Wal.DirectoryFsync.FlushRequired(rootDirectory);
        return matches;
    }

    internal static void MarkIndexesClean(
        string rootDirectory,
        long generation,
        long sequence,
        ReadOnlySpan<byte> schemaFingerprint)
    {
        Save(
            Path.Combine(rootDirectory, CleanIndexesFileName),
            MarkerKind.CleanIndexes,
            generation,
            sequence,
            schemaFingerprint);
    }

    internal static byte[] ComputeSchemaFingerprint(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(schema.Name);
            writer.Write(schema.Columns.Count);
            foreach (var column in schema.Columns)
            {
                writer.Write(column.Name);
                writer.Write((byte)column.DataType);
                writer.Write(column.IsNullable);
                writer.Write(column.IsPrimaryKey);
                writer.Write(column.IsRowVersion);
                writer.Write(column.Ordinal);
            }

            writer.Write(schema.PrimaryKey.Count);
            foreach (string columnName in schema.PrimaryKey)
                writer.Write(columnName);

            writer.Write(schema.Indexes.Count);
            foreach (var index in schema.Indexes)
            {
                writer.Write(index.Name);
                writer.Write(index.IsUnique);
                writer.Write(index.JsonPath ?? string.Empty);
                writer.Write(index.Columns.Count);
                foreach (string columnName in index.Columns)
                    writer.Write(columnName);
            }
        }

        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    private static void Save(
        string path,
        MarkerKind kind,
        long generation,
        long sequence,
        ReadOnlySpan<byte> schemaFingerprint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        if (schemaFingerprint.Length != FingerprintLength)
            throw new ArgumentException("Table schema fingerprint must be 32 bytes.", nameof(schemaFingerprint));

        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string tempPath = path + ".tmp";
        Span<byte> content = stackalloc byte[FileSize];
        Magic.CopyTo(content[..Magic.Length]);
        BinaryPrimitives.WriteInt32LittleEndian(content.Slice(8, sizeof(int)), CurrentVersion);
        BinaryPrimitives.WriteInt32LittleEndian(content.Slice(12, sizeof(int)), (int)kind);
        BinaryPrimitives.WriteInt64LittleEndian(content.Slice(16, sizeof(long)), generation);
        BinaryPrimitives.WriteInt64LittleEndian(content.Slice(24, sizeof(long)), sequence);
        schemaFingerprint.CopyTo(content.Slice(FingerprintOffset, FingerprintLength));
        BinaryPrimitives.WriteUInt32LittleEndian(
            content.Slice(CrcOffset, sizeof(uint)),
            Crc32.HashToUInt32(content[..CrcOffset]));

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
        SonnetDB.Wal.DirectoryFsync.FlushRequired(directory);
    }

    private static bool TryRead(string path, MarkerKind expectedKind, out MaintenanceMarker marker)
    {
        marker = default;
        if (!File.Exists(path))
            return false;

        byte[] content = File.ReadAllBytes(path);
        if (content.Length != FileSize
            || !content.AsSpan(0, Magic.Length).SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(content.AsSpan(8, sizeof(int))) != CurrentVersion
            || BinaryPrimitives.ReadInt32LittleEndian(content.AsSpan(12, sizeof(int))) != (int)expectedKind
            || BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(CrcOffset, sizeof(uint)))
                != Crc32.HashToUInt32(content.AsSpan(0, CrcOffset)))
        {
            return false;
        }

        long generation = BinaryPrimitives.ReadInt64LittleEndian(content.AsSpan(16, sizeof(long)));
        long sequence = BinaryPrimitives.ReadInt64LittleEndian(content.AsSpan(24, sizeof(long)));
        if (generation < 0 || sequence < 0)
            return false;

        marker = new MaintenanceMarker(
            generation,
            sequence,
            content.AsSpan(FingerprintOffset, FingerprintLength).ToArray());
        return true;
    }

    private enum MarkerKind
    {
        LegacyMigration = 1,
        CleanIndexes = 2,
    }

    private readonly record struct MaintenanceMarker(
        long Generation,
        long Sequence,
        byte[] SchemaFingerprint);
}
