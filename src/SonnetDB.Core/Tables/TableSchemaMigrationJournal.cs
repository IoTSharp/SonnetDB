using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Wal;

namespace SonnetDB.Tables;

internal sealed record TableSchemaMigrationEntry(byte[] Key, byte[]? PreviousValue);

internal sealed record TableSchemaMigrationUndo(
    string TableName, long Generation, byte[] SchemaFingerprint,
    IReadOnlyList<TableSchemaMigrationEntry> Entries);

internal sealed record TableSchemaMigrationRecord(
    byte[] OriginalCatalog, IReadOnlyList<TableSchemaMigrationUndo> Tables, bool IsComplete);

// 专用于一条受约束主键 schema 迁移，不复用轻事务日志格式。
// 先持久化旧 schema 语义的 v13 屏障，再 fsync 原目录和 raw before-images，最后才可改 rowstore。
// 恢复仅还原旧 schema 语义，目录格式永久保持 v13，不能在 pending 日志期间给旧引擎写入窗口。
internal static class TableSchemaMigrationJournal
{
    internal const string FileName = "schema-migration.sdbddl";
    internal const int MaxTables = 32;
    internal const int MaxEntries = 1_000_000;
    internal const int MaxCatalogBytes = 16 * 1024 * 1024;
    internal const int MaxPayloadBytes = 128 * 1024 * 1024;
    private const int HeaderSize = 24;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static ReadOnlySpan<byte> Magic => "SDBDDL01"u8;
    private static ReadOnlySpan<byte> CompleteMarker => "SDBDLC01"u8;

    internal static Action<string>? AfterPreparePublishedTestHook { get; set; }
    internal static Action<string>? BeforeCompleteFlushTestHook { get; set; }

    internal static void Prepare(string path, byte[] originalCatalog, IReadOnlyList<TableSchemaMigrationUndo> tables)
    {
        int size = ValidatePlan(originalCatalog, tables);
        if (Read(path) is { IsComplete: false })
            throw new InvalidOperationException("主键迁移尚未恢复，不能覆盖恢复日志。");
        WritePrepared(path, originalCatalog, tables, size);
    }

    internal static int ValidatePlan(byte[] originalCatalog, IReadOnlyList<TableSchemaMigrationUndo> tables)
    {
        ArgumentNullException.ThrowIfNull(originalCatalog);
        ArgumentNullException.ThrowIfNull(tables);
        if (originalCatalog.Length is < 1 or > MaxCatalogBytes || tables.Count is < 1 or > MaxTables)
            throw new InvalidOperationException("主键迁移恢复日志的目录或表数量超过上限。");
        long size = 8L + originalCatalog.Length;
        int totalEntries = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            int nameBytes = Utf8.GetByteCount(table.TableName);
            if (nameBytes is < 1 or > 1024 || string.IsNullOrWhiteSpace(table.TableName)
                || !names.Add(table.TableName) || table.Generation < 0 || table.SchemaFingerprint.Length != 32
                || table.Entries.Count > MaxEntries - totalEntries)
                throw new InvalidOperationException("主键迁移恢复日志的表标识、generation 或条目数量无效。");
            totalEntries += table.Entries.Count;
            size += 4L + nameBytes + 8 + 32 + 4;
            var keys = new HashSet<byte[]>(JournalKeyComparer.Instance);
            foreach (var entry in table.Entries)
            {
                if (!IsValidKey(entry.Key) || !keys.Add(entry.Key))
                    throw new InvalidOperationException("主键迁移恢复日志仅接受关系行、索引和内部元数据键。");
                size += 8L + entry.Key.Length + (entry.PreviousValue?.Length ?? 0);
                if (size > MaxPayloadBytes)
                    throw new InvalidOperationException($"主键迁移恢复日志超过 {MaxPayloadBytes} 字节上限。");
            }
        }
        if (size > MaxPayloadBytes)
            throw new InvalidOperationException($"主键迁移恢复日志超过 {MaxPayloadBytes} 字节上限。");
        return (int)size;
    }

    private static void WritePrepared(string path, byte[] originalCatalog,
        IReadOnlyList<TableSchemaMigrationUndo> tables, int size)
    {
        using var payload = new MemoryStream(size);
        using (var writer = new BinaryWriter(payload, Utf8, leaveOpen: true))
        {
            WriteBytes(writer, originalCatalog);
            writer.Write(tables.Count);
            foreach (var table in tables)
            {
                WriteBytes(writer, Utf8.GetBytes(table.TableName));
                writer.Write(table.Generation);
                writer.Write(table.SchemaFingerprint);
                writer.Write(table.Entries.Count);
                foreach (var entry in table.Entries)
                {
                    WriteBytes(writer, entry.Key);
                    WriteBytes(writer, entry.PreviousValue);
                }
            }
        }
        ReadOnlySpan<byte> bytes = payload.GetBuffer().AsSpan(0, (int)payload.Length);
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32.HashToUInt32(bytes));
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], Crc32.HashToUInt32(header[..20]));
        string temporary = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(header);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            AfterPreparePublishedTestHook?.Invoke(path);
            DirectoryFsync.FlushRequired(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static TableSchemaMigrationRecord? Read(string path)
    {
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < HeaderSize || file.Length > HeaderSize + MaxPayloadBytes + CompleteMarker.Length)
            throw Invalid("invalid file length");
        Span<byte> header = stackalloc byte[HeaderSize];
        file.ReadExactly(header);
        if (!header[..8].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != Crc32.HashToUInt32(header[..20]))
            throw Invalid("invalid header or unsupported version");
        int length = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (length is < 8 or > MaxPayloadBytes || file.Length < HeaderSize + length)
            throw Invalid("truncated payload");
        byte[] payload = new byte[length];
        file.ReadExactly(payload);
        if (Crc32.HashToUInt32(payload) != BinaryPrimitives.ReadUInt32LittleEndian(header[16..]))
            throw Invalid("payload CRC mismatch");
        int remaining = checked((int)(file.Length - file.Position));
        if (remaining > CompleteMarker.Length)
            throw Invalid("unexpected trailing data");
        Span<byte> marker = stackalloc byte[CompleteMarker.Length];
        file.ReadExactly(marker[..remaining]);
        if (!marker[..remaining].SequenceEqual(CompleteMarker[..remaining]))
            throw Invalid("invalid completion marker");

        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Utf8);
        byte[] catalog = ReadBytes(reader, MaxCatalogBytes, nullable: false)!;
        if (catalog.Length == 0 || stream.Length - stream.Position < sizeof(int))
            throw Invalid("missing original catalog");
        int count = reader.ReadInt32();
        if (count is < 1 or > MaxTables)
            throw Invalid("invalid table count");
        var tables = new List<TableSchemaMigrationUndo>(count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        int totalEntries = 0;
        for (int index = 0; index < count; index++)
        {
            string name = Utf8.GetString(ReadBytes(reader, 1024, nullable: false)!);
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name) || stream.Length - stream.Position < 44)
                throw Invalid("invalid table identity");
            long generation = reader.ReadInt64();
            byte[] fingerprint = reader.ReadBytes(32);
            int entryCount = reader.ReadInt32();
            if (generation < 0 || entryCount < 0 || entryCount > MaxEntries - totalEntries
                || entryCount > (stream.Length - stream.Position) / 9)
                throw Invalid("invalid undo metadata");
            totalEntries += entryCount;
            var entries = new List<TableSchemaMigrationEntry>(entryCount);
            var keys = new HashSet<byte[]>(JournalKeyComparer.Instance);
            for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
            {
                byte[] key = ReadBytes(reader, MaxPayloadBytes, nullable: false)!;
                if (!IsValidKey(key) || !keys.Add(key)) throw Invalid("invalid or duplicate row, index or metadata key");
                entries.Add(new TableSchemaMigrationEntry(key, ReadBytes(reader, MaxPayloadBytes, nullable: true)));
            }
            tables.Add(new TableSchemaMigrationUndo(name, generation, fingerprint, entries));
        }
        if (stream.Position != stream.Length)
            throw Invalid("unexpected payload data");
        return new TableSchemaMigrationRecord(catalog, tables, remaining == CompleteMarker.Length);
    }

    internal static void Complete(string path)
    {
        // 只接受已校验的完整日志；重复完成不改提交决定，部分标记由恢复按未完成处理。
        _ = Read(path) ?? throw Invalid("missing journal");
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Span<byte> header = stackalloc byte[HeaderSize];
        file.ReadExactly(header);
        file.Position = HeaderSize + BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        file.Write(CompleteMarker);
        file.SetLength(file.Position);
        BeforeCompleteFlushTestHook?.Invoke(path);
        file.Flush(flushToDisk: true);
    }

    private static bool IsValidKey(byte[] key)
        => key.Length > 0 && key[0] is (byte)'r' or (byte)'i' or (byte)'m';

    private static void WriteBytes(BinaryWriter writer, byte[]? bytes)
    {
        writer.Write(bytes?.Length ?? -1);
        if (bytes is not null) writer.Write(bytes);
    }

    private static byte[]? ReadBytes(BinaryReader reader, int maximum, bool nullable)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position < sizeof(int))
            throw Invalid("truncated value length");
        int length = reader.ReadInt32();
        if (length == -1 && nullable) return null;
        if (length < 0 || length > maximum || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw Invalid("invalid value length");
        return reader.ReadBytes(length);
    }

    private static InvalidDataException Invalid(string reason)
        => new($"TableSchemaMigrationJournal: {reason}.");

    private sealed class JournalKeyComparer : IEqualityComparer<byte[]>
    {
        internal static readonly JournalKeyComparer Instance = new();
        public bool Equals(byte[]? left, byte[]? right)
            => ReferenceEquals(left, right) || left is not null && right is not null && left.AsSpan().SequenceEqual(right);
        public int GetHashCode(byte[] value) => unchecked((int)Crc32.HashToUInt32(value));
    }
}
