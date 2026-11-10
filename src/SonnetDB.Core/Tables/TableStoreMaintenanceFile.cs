using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using SonnetDB.Kv;

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

    internal static bool IsLegacyMigrationComplete(KvKeyspace keyspace, long generation, ReadOnlySpan<byte> schemaFingerprint)
    {
        if (keyspace.SharedStore is not { } store)
            return IsLegacyMigrationComplete(keyspace.RootDirectory, generation, schemaFingerprint);
        byte[]? content = store.ReadMaintenance(keyspace.SharedStorageId, LegacyMigrationFileName);
        if (content is null)
        {
            string oldPath = Path.Combine(keyspace.RootDirectory, LegacyMigrationFileName);
            if (File.Exists(oldPath))
            {
                content = File.ReadAllBytes(oldPath);
                store.WriteMaintenance(keyspace.SharedStorageId, LegacyMigrationFileName, content);
                File.Delete(oldPath);
                SonnetDB.Wal.DirectoryFsync.FlushRequired(keyspace.RootDirectory);
            }
        }
        return content is not null && TryDecode(content, MarkerKind.LegacyMigration, out var marker)
            && marker.Generation == generation && marker.SchemaFingerprint.AsSpan().SequenceEqual(schemaFingerprint);
    }

    internal static void MarkLegacyMigrationComplete(KvKeyspace keyspace, long generation, ReadOnlySpan<byte> schemaFingerprint)
    {
        if (keyspace.SharedStore is { } store)
            store.WriteMaintenance(keyspace.SharedStorageId, LegacyMigrationFileName, Encode(MarkerKind.LegacyMigration, generation, 0, schemaFingerprint));
        else
            MarkLegacyMigrationComplete(keyspace.RootDirectory, generation, schemaFingerprint);
    }

    internal static bool ConsumeCleanIndexes(KvKeyspace keyspace, long generation, long sequence, ReadOnlySpan<byte> schemaFingerprint)
    {
        if (keyspace.SharedStore is not { } store)
            return ConsumeCleanIndexes(keyspace.RootDirectory, generation, sequence, schemaFingerprint);
        byte[]? content = store.ReadMaintenance(keyspace.SharedStorageId, CleanIndexesFileName);
        // Remove an old sidecar token too: an interrupted upgrade must never resurrect it.
        bool oldMatches = ConsumeCleanIndexes(keyspace.RootDirectory, generation, sequence, schemaFingerprint);
        if (content is null)
            return oldMatches;
        bool matches = TryDecode(content, MarkerKind.CleanIndexes, out var marker)
            && marker.Generation == generation && marker.Sequence == sequence
            && marker.SchemaFingerprint.AsSpan().SequenceEqual(schemaFingerprint);
        store.WriteMaintenance(keyspace.SharedStorageId, CleanIndexesFileName, null);
        return matches;
    }

    internal static void MarkIndexesClean(KvKeyspace keyspace, long generation, long sequence, ReadOnlySpan<byte> schemaFingerprint)
    {
        if (keyspace.SharedStore is { } store)
            store.WriteMaintenance(keyspace.SharedStorageId, CleanIndexesFileName, Encode(MarkerKind.CleanIndexes, generation, sequence, schemaFingerprint));
        else
            MarkIndexesClean(keyspace.RootDirectory, generation, sequence, schemaFingerprint);
    }

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
            foreach (var index in schema.Indexes.Where(static index => index.Descending.Any(static value => value) || index.IncludedColumns.Count != 0))
            {
                writer.Write("maintenance-v16");
                writer.Write(index.Name);
                for (int ordinal = 0; ordinal < index.Columns.Count; ordinal++) writer.Write(index.IsDescending(ordinal));
                writer.Write(index.IncludedColumns.Count);
                foreach (string column in index.IncludedColumns) writer.Write(column);
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
        byte[] content = Encode(kind, generation, sequence, schemaFingerprint);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string tempPath = path + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tempPath, path, overwrite: true);
        SonnetDB.Wal.DirectoryFsync.FlushRequired(directory);
    }

    private static byte[] Encode(MarkerKind kind, long generation, long sequence, ReadOnlySpan<byte> schemaFingerprint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        if (schemaFingerprint.Length != FingerprintLength)
            throw new ArgumentException("Table schema fingerprint must be 32 bytes.", nameof(schemaFingerprint));

        Span<byte> content = new byte[FileSize];
        Magic.CopyTo(content[..Magic.Length]);
        BinaryPrimitives.WriteInt32LittleEndian(content.Slice(8, sizeof(int)), CurrentVersion);
        BinaryPrimitives.WriteInt32LittleEndian(content.Slice(12, sizeof(int)), (int)kind);
        BinaryPrimitives.WriteInt64LittleEndian(content.Slice(16, sizeof(long)), generation);
        BinaryPrimitives.WriteInt64LittleEndian(content.Slice(24, sizeof(long)), sequence);
        schemaFingerprint.CopyTo(content.Slice(FingerprintOffset, FingerprintLength));
        BinaryPrimitives.WriteUInt32LittleEndian(
            content.Slice(CrcOffset, sizeof(uint)),
            Crc32.HashToUInt32(content[..CrcOffset]));

        return content.ToArray();
    }

    private static bool TryRead(string path, MarkerKind expectedKind, out MaintenanceMarker marker)
    {
        marker = default;
        if (!File.Exists(path))
            return false;

        return TryDecode(File.ReadAllBytes(path), expectedKind, out marker);
    }

    private static bool TryDecode(byte[] content, MarkerKind expectedKind, out MaintenanceMarker marker)
    {
        marker = default;
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
