using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace SonnetDB.Tables;

internal sealed record TableTransactionUndo(
    string TableName, long Generation, byte[] SchemaFingerprint,
    IReadOnlyList<TableStore.RollbackAction> Actions);

// 单个 TableManager 串行提交，最多保留一个待恢复事务。先同步 before-images，
// 再写各表 WAL；所有表同步后追加提交标记。恢复撤销没有完整提交标记的事务。
internal static class TableTransactionJournal
{
    internal const string FileName = "transaction.sdbtxn";
    private const int HeaderSize = 24;
    private const int MaxPayloadBytes = 128 * 1024 * 1024;
    private const int MaxTables = 1024;
    private const int MaxActions = 1_000_000;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static ReadOnlySpan<byte> Magic => "SDBTXN01"u8;
    private static ReadOnlySpan<byte> CompleteMarker => "SDBDONE1"u8;

    internal static void Prepare(string path, IReadOnlyList<TableTransactionUndo> tables)
    {
        if (tables.Count is < 1 or > MaxTables)
            throw new InvalidOperationException($"关系事务恢复日志最多支持 {MaxTables} 张表。");
        long size = 4;
        foreach (var table in tables)
        {
            int nameBytes = Utf8.GetByteCount(table.TableName);
            if (nameBytes is < 1 or > 1024 || table.SchemaFingerprint.Length != 32
                || table.Actions.Count > MaxActions)
                throw new InvalidOperationException("关系事务恢复日志的表或操作数量无效。");
            size += 4 + nameBytes + 8 + 32 + 4;
            foreach (var action in table.Actions)
                size += 8L + action.Key.Length + (action.Value?.Length ?? 0);
        }
        if (size > MaxPayloadBytes)
            throw new InvalidOperationException($"关系事务恢复日志超过 {MaxPayloadBytes} 字节上限，请缩小事务。");

        using var payload = new MemoryStream((int)size);
        using (var writer = new BinaryWriter(payload, Utf8, leaveOpen: true))
        {
            writer.Write(tables.Count);
            foreach (var table in tables)
            {
                WriteBytes(writer, Utf8.GetBytes(table.TableName));
                writer.Write(table.Generation);
                writer.Write(table.SchemaFingerprint);
                writer.Write(table.Actions.Count);
                foreach (var action in table.Actions)
                {
                    WriteBytes(writer, action.Key);
                    WriteBytes(writer, action.Value);
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
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static IReadOnlyList<TableTransactionUndo> ReadPending(string path)
    {
        if (!File.Exists(path)) return [];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < HeaderSize || file.Length > HeaderSize + MaxPayloadBytes + CompleteMarker.Length)
            throw new InvalidDataException("TableTransactionJournal: invalid file length.");
        Span<byte> header = stackalloc byte[HeaderSize];
        file.ReadExactly(header);
        if (!header[..8].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != Crc32.HashToUInt32(header[..20]))
            throw new InvalidDataException("TableTransactionJournal: invalid header or unsupported version.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (length is < 4 or > MaxPayloadBytes || file.Length < HeaderSize + length)
            throw new InvalidDataException("TableTransactionJournal: truncated payload.");
        byte[] payload = new byte[length];
        file.ReadExactly(payload);
        if (Crc32.HashToUInt32(payload) != BinaryPrimitives.ReadUInt32LittleEndian(header[16..]))
            throw new InvalidDataException("TableTransactionJournal: payload CRC mismatch.");
        int remaining = checked((int)(file.Length - file.Position));
        if (remaining > CompleteMarker.Length)
            throw new InvalidDataException("TableTransactionJournal: unexpected trailing data.");
        Span<byte> marker = stackalloc byte[CompleteMarker.Length];
        file.ReadExactly(marker[..remaining]);
        if (!marker[..remaining].SequenceEqual(CompleteMarker[..remaining]))
            throw new InvalidDataException("TableTransactionJournal: invalid completion marker.");
        if (remaining == CompleteMarker.Length) return [];

        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Utf8);
        int count = reader.ReadInt32();
        if (count is < 1 or > MaxTables)
            throw new InvalidDataException("TableTransactionJournal: invalid table count.");
        var tables = new List<TableTransactionUndo>(count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            string name = Utf8.GetString(ReadBytes(reader, 1024, nullable: false)!);
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                throw new InvalidDataException("TableTransactionJournal: invalid or duplicate table name.");
            long generation = reader.ReadInt64();
            byte[] fingerprint = reader.ReadBytes(32);
            int actionCount = reader.ReadInt32();
            if (generation < 0 || fingerprint.Length != 32 || actionCount is < 0 or > MaxActions
                || actionCount > (stream.Length - stream.Position) / 8)
                throw new InvalidDataException("TableTransactionJournal: invalid undo metadata.");
            var actions = new List<TableStore.RollbackAction>(actionCount);
            for (int actionIndex = 0; actionIndex < actionCount; actionIndex++)
            {
                byte[] key = ReadBytes(reader, MaxPayloadBytes, nullable: false)!;
                if (key.Length == 0 || (key[0] != (byte)'r' && key[0] != (byte)'i'))
                    throw new InvalidDataException("TableTransactionJournal: invalid row or index key.");
                actions.Add(new TableStore.RollbackAction(key, ReadBytes(reader, MaxPayloadBytes, nullable: true)));
            }
            tables.Add(new TableTransactionUndo(name, generation, fingerprint, actions));
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("TableTransactionJournal: unexpected payload data.");
        return tables;
    }

    internal static void Complete(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Span<byte> header = stackalloc byte[HeaderSize];
        file.ReadExactly(header);
        file.Position = HeaderSize + BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        file.Write(CompleteMarker);
        file.SetLength(file.Position);
        file.Flush(flushToDisk: true);
    }

    private static void WriteBytes(BinaryWriter writer, byte[]? bytes)
    {
        writer.Write(bytes?.Length ?? -1);
        if (bytes is not null) writer.Write(bytes);
    }

    private static byte[]? ReadBytes(BinaryReader reader, int maximum, bool nullable)
    {
        int length = reader.ReadInt32();
        if (length == -1 && nullable) return null;
        if (length < 0 || length > maximum || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("TableTransactionJournal: invalid value length.");
        return reader.ReadBytes(length);
    }
}
