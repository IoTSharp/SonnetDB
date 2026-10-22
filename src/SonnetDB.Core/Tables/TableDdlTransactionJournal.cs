using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SonnetDB.Wal;

namespace SonnetDB.Tables;

internal enum TableDdlTransactionDecision : byte
{
    Prepared = 1,
    Committed = 2,
}

/// <summary>DDL journal 内的结构化关系对象身份；不得用点号拼接 schema 与表名。</summary>
internal sealed record TableDdlObjectIdentity(string SchemaName, string Name);

/// <summary>持久关系 DDL 事务的版本化 prepared/committed 决策日志。</summary>
internal sealed record TableDdlTransactionJournal(
    Guid Owner,
    TableDdlTransactionDecision Decision,
    string WorkspaceRelativePath,
    string UndoRelativePath,
    int CatalogVersionFloor,
    IReadOnlyList<TableDdlObjectIdentity> Objects)
{
    internal const string FileName = "table-ddl-transaction.pending";
    private const int Version = 2;
    private const int MinimumSupportedVersion = 1;
    private const int MaximumObjects = 256;
    private const int MaximumStringBytes = 4_096;
    private const int MaximumPayloadBytes = 2 * 1024 * 1024;
    private static readonly byte[] Magic = "SDBDDL01"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static Action<string, TableDdlTransactionDecision>? AfterDurableWriteTestHook { get; set; }

    internal static void Prepare(string path, TableDdlTransactionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Decision != TableDdlTransactionDecision.Prepared)
            throw new ArgumentException("首次 DDL journal 必须是 prepared。", nameof(journal));
        if (File.Exists(path))
            throw new InvalidOperationException("已有 DDL 事务恢复日志，拒绝覆盖。");
        Write(path, journal, overwrite: false);
    }

    internal static void MarkCommitted(string path, Guid owner)
    {
        TableDdlTransactionJournal current = Read(path)
            ?? throw new InvalidDataException("DDL 事务 prepared 日志不存在。");
        if (current.Owner != owner || current.Decision != TableDdlTransactionDecision.Prepared)
            throw new InvalidDataException("DDL 事务 prepared 日志身份或状态不匹配。");
        Write(path, current with { Decision = TableDdlTransactionDecision.Committed }, overwrite: true);
    }

    internal static TableDdlTransactionJournal? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return null;
        RejectLink(path);
        var info = new FileInfo(path);
        if (info.Length is < 80 or > MaximumPayloadBytes)
            throw new InvalidDataException("DDL 事务日志长度非法。");
        byte[] payload = File.ReadAllBytes(path);
        ReadOnlySpan<byte> content = payload.AsSpan(0, payload.Length - 32);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), payload.AsSpan(payload.Length - 32)))
            throw new InvalidDataException("DDL 事务日志校验和不匹配。");
        var reader = new JournalReader(content);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic)
            || reader.ReadInt32() is < MinimumSupportedVersion or > Version)
            throw new InvalidDataException("DDL 事务日志 magic 或版本未知。");
        var decision = (TableDdlTransactionDecision)reader.ReadByte();
        if (decision is not TableDdlTransactionDecision.Prepared and not TableDdlTransactionDecision.Committed)
            throw new InvalidDataException("DDL 事务日志决策状态未知。");
        reader.Skip(3);
        Guid owner = new(reader.ReadBytes(16));
        int floor = reader.ReadInt32();
        if (floor < 0)
            throw new InvalidDataException("DDL 事务日志目录版本 floor 非法。");
        string workspace = reader.ReadString();
        string undo = reader.ReadString();
        ValidateRelativePath(workspace, nameof(workspace));
        ValidateRelativePath(undo, nameof(undo));
        int count = reader.ReadInt32();
        if (count is < 0 or > MaximumObjects)
            throw new InvalidDataException("DDL 事务日志对象数量非法。");
        var objects = new TableDdlObjectIdentity[count];
        for (int index = 0; index < count; index++)
        {
            string schema = reader.ReadString();
            string name = reader.ReadString();
            if (schema.Length == 0 || name.Length == 0)
                throw new InvalidDataException("DDL 事务日志对象名称为空。");
            objects[index] = new TableDdlObjectIdentity(schema, name);
        }
        if (!reader.End)
            throw new InvalidDataException("DDL 事务日志包含尾随字节。");
        return new TableDdlTransactionJournal(owner, decision, workspace, undo, floor, objects);
    }

    internal static void Complete(string path)
    {
        if (!File.Exists(path))
            return;
        RejectLink(path);
        File.Delete(path);
        DirectoryFsync.FlushRequired(Path.GetDirectoryName(path)!);
    }

    private static void Write(string path, TableDdlTransactionJournal journal, bool overwrite)
    {
        Validate(journal);
        byte[] payload = Encode(journal);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + journal.Owner.ToString("N") + ".tmp";
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
            created = false;
            DirectoryFsync.FlushRequired(Path.GetDirectoryName(path)!);
            AfterDurableWriteTestHook?.Invoke(path, journal.Decision);
        }
        finally
        {
            if (created && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static byte[] Encode(TableDdlTransactionJournal journal)
    {
        using var stream = new MemoryStream();
        stream.Write(Magic);
        WriteInt32(stream, Version);
        stream.WriteByte((byte)journal.Decision);
        stream.Write([0, 0, 0]);
        stream.Write(journal.Owner.ToByteArray());
        WriteInt32(stream, journal.CatalogVersionFloor);
        WriteString(stream, journal.WorkspaceRelativePath);
        WriteString(stream, journal.UndoRelativePath);
        WriteInt32(stream, journal.Objects.Count);
        foreach (TableDdlObjectIdentity identity in journal.Objects)
        {
            WriteString(stream, identity.SchemaName);
            WriteString(stream, identity.Name);
        }
        if (stream.Length > MaximumPayloadBytes - 32)
            throw new InvalidOperationException("DDL 事务日志超过字节预算。");
        byte[] content = stream.ToArray();
        byte[] payload = new byte[content.Length + 32];
        content.CopyTo(payload, 0);
        SHA256.HashData(content, payload.AsSpan(content.Length));
        return payload;
    }

    private static void Validate(TableDdlTransactionJournal journal)
    {
        if (!Enum.IsDefined(journal.Decision))
            throw new ArgumentOutOfRangeException(nameof(journal));
        if (journal.CatalogVersionFloor < 0)
            throw new ArgumentOutOfRangeException(nameof(journal));
        ValidateRelativePath(journal.WorkspaceRelativePath, nameof(journal.WorkspaceRelativePath));
        ValidateRelativePath(journal.UndoRelativePath, nameof(journal.UndoRelativePath));
        if (journal.Objects.Count > MaximumObjects)
            throw new ArgumentOutOfRangeException(nameof(journal));
        var names = new HashSet<(string Schema, string Name)>();
        foreach (TableDdlObjectIdentity identity in journal.Objects)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.SchemaName);
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.Name);
            if (!names.Add((identity.SchemaName, identity.Name)))
                throw new ArgumentException("DDL 事务日志包含重复对象。", nameof(journal));
        }
    }

    private static void ValidateRelativePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (Path.IsPathFullyQualified(path)
            || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(static part => part is "" or "." or ".."))
            throw new ArgumentException("DDL 事务日志路径必须是无跳转的相对路径。", parameterName);
        _ = Utf8.GetBytes(path).Length <= MaximumStringBytes
            ? true : throw new ArgumentException("DDL 事务日志路径超过字节预算。", parameterName);
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Utf8.GetBytes(value);
        if (bytes.Length > MaximumStringBytes)
            throw new ArgumentException("DDL 事务日志字符串超过字节预算。", nameof(value));
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("DDL 事务日志路径为链接，拒绝跟随。");
    }

    private ref struct JournalReader(ReadOnlySpan<byte> source)
    {
        private ReadOnlySpan<byte> _source = source;
        private int _offset;
        internal bool End => _offset == _source.Length;

        internal byte ReadByte()
        {
            Ensure(1);
            return _source[_offset++];
        }

        internal int ReadInt32()
        {
            Ensure(4);
            int value = BinaryPrimitives.ReadInt32LittleEndian(_source[_offset..]);
            _offset += 4;
            return value;
        }

        internal ReadOnlySpan<byte> ReadBytes(int count)
        {
            Ensure(count);
            ReadOnlySpan<byte> value = _source.Slice(_offset, count);
            _offset += count;
            return value;
        }

        internal string ReadString()
        {
            int length = ReadInt32();
            if (length is < 0 or > MaximumStringBytes)
                throw new InvalidDataException("DDL 事务日志字符串长度非法。");
            return Utf8.GetString(ReadBytes(length));
        }

        internal void Skip(int count) => _ = ReadBytes(count);

        private void Ensure(int count)
        {
            if (count < 0 || count > _source.Length - _offset)
                throw new InvalidDataException("DDL 事务日志意外结束。");
        }
    }
}
