using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SonnetDB.Kv;
using SonnetDB.Wal;

namespace SonnetDB.Tables;

internal sealed record ResumableMigrationTable(string RelativeDirectory, long SourceGeneration, long SourceSequence)
{
    internal byte[] SourceLayoutHash { get; init; } = [];
}

internal sealed record ResumableMigrationState(
    byte[] SourceCatalogHash, byte[] CandidateCatalog, ResumableMigrationTable[] Tables,
    int Step, byte[]? Cursor, bool Committing)
{
    internal Guid Id { get; init; } = Guid.NewGuid();
    internal long RowsInStep { get; init; }
    internal byte[] RequestIdentity { get; init; } = [];
    internal bool Discarding { get; init; }
}

internal sealed record ResumableMigrationPending(Guid MigrationId, int Step, byte[]? Cursor, byte[][] Keys);

/// <summary>大表迁移的持久状态、单批补偿和 rowstore 切换。</summary>
internal static class TableResumableSchemaMigrationJournal
{
    internal const string DirectoryName = ".schema-migration";
    internal const int MaxPendingKeys = 16_384;
    internal const int MaxPendingBytes = 16 * 1024 * 1024;
    private const int MaxCursorBytes = 4 * 1024 * 1024;
    private const int MaxPathBytes = 4_096;
    private const int MaxManifestBytes = TableSchemaMigrationJournal.MaxCatalogBytes
        + TableSchemaMigrationJournal.MaxTables * (MaxPathBytes + 64) + 256;
    private const int MaxCheckpointBytes = MaxCursorBytes + 128;
    private const int MaxCleanupEntries = 262_144;
    internal const string ManifestName = "manifest";
    private const string CheckpointName = "checkpoint";
    private const string PendingName = "pending";
    private const string OwnerName = ".schema-migration-owner";
    private const string StageLayoutName = ".schema-migration-layout";
    private static readonly UTF8Encoding _utf8 = new(false, true);

    internal static ResumableMigrationState? Read(string workDirectory)
    {
        RejectLink(Path.GetDirectoryName(workDirectory)!);
        if (!Directory.Exists(workDirectory)) return null;
        RejectLink(workDirectory);
        using var manifest = OpenRecord(workDirectory, ManifestName, "SDBRSM01"u8, MaxManifestBytes);
        if (manifest is null) return null;

        Guid id = ReadId(manifest);
        byte[] sourceHash = ReadBytes(manifest, SHA256.HashSizeInBytes);
        byte[] catalog = ReadSized(manifest, TableSchemaMigrationJournal.MaxCatalogBytes);
        byte[] requestIdentity = ReadSized(manifest, SHA256.HashSizeInBytes);
        int count = manifest.ReadInt32();
        if (count is <= 0 or > TableSchemaMigrationJournal.MaxTables)
            throw new InvalidDataException("分批迁移表数超出日志范围。");
        var tables = new ResumableMigrationTable[count];
        for (int index = 0; index < count; index++)
        {
            string relativeDirectory = _utf8.GetString(ReadSized(manifest, MaxPathBytes));
            tables[index] = new ResumableMigrationTable(relativeDirectory, manifest.ReadInt64(), manifest.ReadInt64())
            {
                SourceLayoutHash = ReadSized(manifest, SHA256.HashSizeInBytes),
            };
        }
        EndRecord(manifest);

        var state = new ResumableMigrationState(sourceHash, catalog, tables, 0, null, false)
        {
            Id = id,
            RequestIdentity = requestIdentity,
        };
        using var checkpoint = OpenRecord(workDirectory, CheckpointName, "SDBRSC01"u8, MaxCheckpointBytes);
        if (checkpoint is not null)
        {
            if (ReadId(checkpoint) != id)
                throw new InvalidDataException("分批迁移 checkpoint 的日志身份不匹配。");
            int step = checkpoint.ReadInt32();
            long rowsInStep = checkpoint.ReadInt64();
            byte phase = checkpoint.ReadByte();
            if (phase > 2) throw new InvalidDataException("分批迁移阶段无效。");
            state = state with
            {
                Step = step,
                RowsInStep = rowsInStep,
                Cursor = ReadCursor(checkpoint),
                Committing = phase == 1,
                Discarding = phase == 2,
            };
            EndRecord(checkpoint);
        }

        ValidateState(workDirectory, state);
        return state;
    }

    internal static void Save(string workDirectory, ResumableMigrationState state)
    {
        ValidateState(workDirectory, state);
        string manifestPath = Path.Combine(workDirectory, ManifestName);
        if (!File.Exists(manifestPath))
        {
            if (state.Step != 0 || state.Cursor is not null || state.RowsInStep != 0 || state.Committing || state.Discarding)
                throw new InvalidOperationException("发布分批迁移 checkpoint 前必须先持久化迁移计划。");
            string parent = Path.GetDirectoryName(workDirectory)!;
            Directory.CreateDirectory(parent);
            RejectLink(parent);
            Directory.CreateDirectory(workDirectory);
            RejectLink(workDirectory);
            DirectoryFsync.FlushRequired(parent);
            DirectoryFsync.FlushRequired(Path.GetDirectoryName(parent)!);
            DeleteRecord(workDirectory, CheckpointName);
            DeleteRecord(workDirectory, PendingName);
            WriteRecord(workDirectory, ManifestName, "SDBRSM01"u8, MaxManifestBytes, writer =>
            {
                writer.Write(state.Id.ToByteArray());
                writer.Write(state.SourceCatalogHash);
                WriteSized(writer, state.CandidateCatalog);
                WriteSized(writer, state.RequestIdentity);
                writer.Write(state.Tables.Length);
                foreach (ResumableMigrationTable table in state.Tables)
                {
                    WriteSized(writer, _utf8.GetBytes(table.RelativeDirectory));
                    writer.Write(table.SourceGeneration);
                    writer.Write(table.SourceSequence);
                    WriteSized(writer, table.SourceLayoutHash);
                }
            });
            return;
        }

        RejectLink(workDirectory);
        RejectLink(manifestPath);
        WriteRecord(workDirectory, CheckpointName, "SDBRSC01"u8, MaxCheckpointBytes, writer =>
        {
            writer.Write(state.Id.ToByteArray());
            writer.Write(state.Step);
            writer.Write(state.RowsInStep);
            writer.Write((byte)(state.Committing ? 1 : state.Discarding ? 2 : 0));
            WriteSized(writer, state.Cursor ?? []);
        });
    }

    internal static ResumableMigrationPending? ReadPending(string workDirectory)
    {
        using var reader = OpenRecord(workDirectory, PendingName, "SDBRSB01"u8, MaxPendingBytes);
        if (reader is null) return null;
        Guid id = ReadId(reader);
        int step = reader.ReadInt32();
        byte[]? cursor = ReadCursor(reader);
        int count = reader.ReadInt32();
        if (step < 0 || count is <= 0 or > MaxPendingKeys)
            throw new InvalidDataException("分批迁移补偿条目数或阶段无效。");
        var keys = new byte[count][];
        for (int index = 0; index < count; index++)
        {
            keys[index] = ReadSized(reader, MaxCursorBytes);
            if (keys[index].Length == 0)
                throw new InvalidDataException("分批迁移补偿键不能为空。");
        }
        EndRecord(reader);
        return new ResumableMigrationPending(id, step, cursor, keys);
    }

    internal static void SavePending(string workDirectory, ResumableMigrationPending pending)
    {
        if (pending.MigrationId == Guid.Empty || pending.Step < 0
            || pending.Cursor is { Length: 0 or > MaxCursorBytes }
            || pending.Keys.Length is <= 0 or > MaxPendingKeys)
            throw new InvalidOperationException("分批迁移补偿记录超出范围。");
        if (File.Exists(Path.Combine(workDirectory, PendingName)))
            throw new InvalidOperationException("已有分批迁移补偿记录，须先完成补偿或清除已提交记录。");

        WriteRecord(workDirectory, PendingName, "SDBRSB01"u8, MaxPendingBytes, writer =>
        {
            writer.Write(pending.MigrationId.ToByteArray());
            writer.Write(pending.Step);
            WriteSized(writer, pending.Cursor ?? []);
            writer.Write(pending.Keys.Length);
            foreach (byte[] key in pending.Keys)
            {
                if (key.Length is 0 or > MaxCursorBytes || writer.BaseStream.Position > MaxPendingBytes - key.Length - 36)
                    throw new InvalidOperationException("分批迁移补偿键超出记录预算。");
                WriteSized(writer, key);
            }
        });
    }

    internal static void ClearPending(string workDirectory) => DeleteRecord(workDirectory, PendingName);

    internal static string StageDirectory(string workDirectory, int index)
        => Path.Combine(workDirectory, FormattableString.Invariant($"staged-{index}"));

    private static string BackupDirectory(string workDirectory, int index)
        => Path.Combine(workDirectory, FormattableString.Invariant($"original-{index}"));

    internal static string OriginalDirectory(string workDirectory, string relativeDirectory)
    {
        string[] segments = relativeDirectory.Split(['/', '\\'], StringSplitOptions.None);
        TableObjectName name = TableObjectNameCodec.DecodeStoragePath(segments);
        if (!segments.SequenceEqual(TableObjectNameCodec.EncodeStoragePath(name), StringComparer.Ordinal))
            throw new InvalidDataException("分批迁移包含非规范 rowstore 路径。");
        string root = Path.GetDirectoryName(workDirectory)!;
        RejectLink(root);
        string directory = root;
        foreach (string segment in segments)
        {
            directory = Path.Combine(directory, segment);
            RejectLink(directory);
        }
        return directory;
    }

    internal static void CheckStage(string workDirectory, ResumableMigrationState state, int index)
        => _ = InspectStage(workDirectory, state, index);

    internal static void PrepareStage(string workDirectory, ResumableMigrationState state, int index)
    {
        if (InspectStage(workDirectory, state, index)) return;
        string stage = StageDirectory(workDirectory, index);
        Directory.CreateDirectory(stage);
        WriteAtomic(Path.Combine(stage, OwnerName), OwnerBytes(state.Id, index));
        DirectoryFsync.FlushRequired(workDirectory);
    }

    private static bool InspectStage(string workDirectory, ResumableMigrationState state, int index)
    {
        if (index < 0 || index >= state.Tables.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        string stage = StageDirectory(workDirectory, index);
        RejectLink(stage);
        bool hasCheckpoint = index < state.Step || (index == state.Step && state.Cursor is not null);
        if (HasOwner(stage, state.Id, index))
        {
            ValidateStageLayout(stage, required: hasCheckpoint);
            return true;
        }
        if (hasCheckpoint)
            throw new InvalidDataException("分批迁移 checkpoint 引用了缺失或归属不符的暂存 rowstore。");
        if (Directory.Exists(stage) && Directory.EnumerateFileSystemEntries(stage).Take(2).Any(
            entry => !string.Equals(entry, Path.Combine(stage, OwnerName + ".tmp"), StringComparison.Ordinal)))
            throw new InvalidDataException("分批迁移暂存 rowstore 的归属不匹配。");

        return false;
    }

    internal static void RecordStageLayout(string stageDirectory)
    {
        if (!ValidateStageLayout(stageDirectory, required: false))
            WriteAtomic(Path.Combine(stageDirectory, StageLayoutName), ReadSourceLayoutHash(stageDirectory));
    }

    private static bool ValidateStageLayout(string stageDirectory, bool required)
    {
        string path = Path.Combine(stageDirectory, StageLayoutName);
        RejectLink(path);
        if (!File.Exists(path))
        {
            if (required)
                throw new InvalidDataException("分批迁移 checkpoint 引用了缺少布局标记的暂存 rowstore。");
            return false;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> layoutHash = stackalloc byte[SHA256.HashSizeInBytes];
        if (stream.Length != layoutHash.Length
            || stream.ReadAtLeast(layoutHash, layoutHash.Length, throwOnEndOfStream: false) != layoutHash.Length
            || !layoutHash.SequenceEqual(ReadSourceLayoutHash(stageDirectory)))
            throw new InvalidDataException("分批迁移暂存 rowstore 布局身份不匹配。");
        return true;
    }

    internal static byte[] ReadSourceLayoutHash(string sourceDirectory)
    {
        string layout = Path.Combine(sourceDirectory, KvGenerationFile.FileName);
        RejectLink(layout);
        if (!File.Exists(layout)) return SHA256.HashData(ReadOnlySpan<byte>.Empty);
        using var stream = new FileStream(layout, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != 64)
            throw new InvalidDataException("分批迁移源 rowstore generation 标记长度无效。");
        return SHA256.HashData(stream);
    }

    internal static void Complete(string workDirectory, string schemaPath, ResumableMigrationState state,
        Action<string>? phaseHook = null)
    {
        ValidateState(workDirectory, state);
        if (!state.Committing || ReadPending(workDirectory) is not null)
            throw new InvalidOperationException("持久发布前必须完成分批迁移并清除单批补偿。");
        RejectLink(workDirectory);
        RejectLink(schemaPath);
        if (TableSchemaCodec.ReadFormatVersion(schemaPath) < TableSchemaCodec.ResumableMigrationVersion)
            throw new InvalidDataException("分批迁移缺少永久目录兼容屏障。");

        using var candidate = new MemoryStream(state.CandidateCatalog, writable: false);
        var catalog = TableSchemaCodec.Load(candidate);
        if (candidate.Position != candidate.Length)
            throw new InvalidDataException("分批迁移候选目录包含未识别的尾部数据。");
        var candidateDirectories = catalog.Select(schema => string.Join('/', TableObjectNameCodec.EncodeStoragePath(schema.ObjectName)))
            .ToHashSet(StringComparer.Ordinal);
        byte[] liveHash = ReadCatalogHash(schemaPath);
        bool sourceCatalog = liveHash.AsSpan().SequenceEqual(state.SourceCatalogHash);
        bool candidateCatalog = liveHash.AsSpan().SequenceEqual(SHA256.HashData(state.CandidateCatalog));
        if (!sourceCatalog && !candidateCatalog)
            throw new InvalidDataException("分批迁移提交与当前目录身份不匹配。");

        var moves = new DirectoryMove[state.Tables.Length];
        for (int index = 0; index < state.Tables.Length; index++)
        {
            ResumableMigrationTable table = state.Tables[index];
            if (!candidateDirectories.Contains(table.RelativeDirectory.Replace('\\', '/')))
                throw new InvalidDataException("分批迁移发布路径不属于候选目录。");
            moves[index] = InspectMove(workDirectory, table, state.Id, index, sourceCatalog, candidateCatalog);
            if (!sourceCatalog && moves[index].Phase != MovePhase.Installed)
                throw new InvalidDataException("分批迁移目录已经发布，但仍缺少已安装的 rowstore。");
        }
        DirectoryFsync.FlushRequired(workDirectory);
        DirectoryFsync.FlushRequired(Path.GetDirectoryName(schemaPath)!);
        for (int index = 0; index < moves.Length; index++)
        {
            DirectoryMove move = moves[index];
            if (move.Phase == MovePhase.Ready)
            {
                MoveDirectory(move.Final, move.Backup);
                phaseHook?.Invoke(FormattableString.Invariant($"after-resumable-source-move:{index}"));
            }
            if (move.Phase != MovePhase.Installed)
            {
                MoveDirectory(move.Stage, move.Final);
                phaseHook?.Invoke(FormattableString.Invariant($"after-resumable-swap:{index}"));
            }
            DirectoryFsync.FlushRequired(Path.GetDirectoryName(move.Final)!);
        }

        if (!candidateCatalog)
            WriteAtomic(schemaPath, state.CandidateCatalog, ".resumable.tmp");
        DirectoryFsync.FlushRequired(Path.GetDirectoryName(schemaPath)!);
        phaseHook?.Invoke("after-resumable-catalog");
        RemoveWorkspaceDirectories(workDirectory, state, committing: true);
        Retire(workDirectory);
    }

    internal static void Discard(string workDirectory, ResumableMigrationState state)
    {
        if (state.Committing)
            throw new InvalidOperationException("分批迁移已确定提交，须完成发布。");
        if (!state.Discarding)
            Save(workDirectory, state with { Discarding = true });
        RemoveWorkspaceDirectories(workDirectory, state, committing: false);
        ClearPending(workDirectory);
        Retire(workDirectory);
    }

    internal static byte[] ReadCatalogHash(string schemaPath)
    {
        RejectLink(schemaPath);
        using var stream = new FileStream(schemaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > TableSchemaMigrationJournal.MaxCatalogBytes)
            throw new InvalidDataException("分批迁移 catalog 超出日志范围。");
        return SHA256.HashData(stream);
    }

    private static DirectoryMove InspectMove(string workDirectory, ResumableMigrationTable table, Guid id, int index,
        bool sourceCatalog, bool candidateCatalog)
    {
        string final = OriginalDirectory(workDirectory, table.RelativeDirectory);
        string stage = StageDirectory(workDirectory, index);
        string backup = BackupDirectory(workDirectory, index);
        RejectLink(stage);
        RejectLink(backup);
        bool hasFinal = Directory.Exists(final);
        bool hasStage = Directory.Exists(stage);
        bool hasBackup = Directory.Exists(backup);
        bool finalOwned = HasOwner(final, id, index);
        bool stageOwned = HasOwner(stage, id, index);
        if (finalOwned && !hasStage)
        {
            ValidateStageLayout(final, required: true);
            if (!candidateCatalog)
            {
                if (!hasBackup)
                    throw new InvalidDataException("分批迁移在候选目录发布前缺少源 rowstore。");
                ValidateSourceLayout(backup, table);
            }
            return new DirectoryMove(final, stage, backup, MovePhase.Installed);
        }
        if (!sourceCatalog || !stageOwned || finalOwned || hasFinal == hasBackup)
            throw new InvalidDataException("分批迁移 rowstore 路径或归属状态无效。");
        ValidateStageLayout(stage, required: true);
        ValidateSourceLayout(hasFinal ? final : backup, table);
        return new DirectoryMove(final, stage, backup, hasFinal ? MovePhase.Ready : MovePhase.SourceMoved);
    }

    private static void ValidateSourceLayout(string directory, ResumableMigrationTable table)
    {
        if (table.SourceLayoutHash.Length != 0 && !ReadSourceLayoutHash(directory).AsSpan().SequenceEqual(table.SourceLayoutHash))
            throw new InvalidDataException("分批迁移源 rowstore 布局身份不匹配。");
    }

    private static void MoveDirectory(string source, string destination)
    {
        Directory.Move(source, destination);
        DirectoryFsync.FlushRequired(Path.GetDirectoryName(destination)!);
        DirectoryFsync.FlushRequired(Path.GetDirectoryName(source)!);
    }

    private static void RemoveWorkspaceDirectories(string workDirectory, ResumableMigrationState state, bool committing)
    {
        long started = Stopwatch.GetTimestamp();
        int entries = 0;
        for (int index = 0; index < state.Tables.Length; index++)
        {
            string directory = committing ? BackupDirectory(workDirectory, index) : StageDirectory(workDirectory, index);
            RejectLink(directory);
            if (!Directory.Exists(directory)) continue;
            string? owner = null;
            if (!committing)
            {
                owner = Path.Combine(directory, OwnerName);
                if (!HasOwner(directory, state.Id, index) && Directory.EnumerateFileSystemEntries(directory).Take(2).Any(
                    entry => !string.Equals(entry, owner + ".tmp", StringComparison.Ordinal)))
                    throw new InvalidDataException("分批迁移暂存 rowstore 的归属不匹配，拒绝清除。");
            }
            RemoveOwnedDirectory(directory, owner, started, ref entries);
        }
        DirectoryFsync.FlushRequired(workDirectory);
    }

    private static void RemoveOwnedDirectory(string directory, string? owner, long started, ref int count)
    {
        var directories = new List<string> { directory };
        for (int index = 0; index < directories.Count; index++)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directories[index]))
            {
                if (++count > MaxCleanupEntries || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMinutes(2))
                    throw new IOException("分批迁移目录清理超出文件数或时间预算，须重开并继续。");
                RejectLink(entry);
                if (Directory.Exists(entry))
                    directories.Add(entry);
                else if (!string.Equals(entry, owner, StringComparison.Ordinal))
                    File.Delete(entry);
            }
        }
        for (int index = directories.Count - 1; index > 0; index--)
        {
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMinutes(2))
                throw new IOException("分批迁移目录清理超出时间预算，须重开并继续。");
            Directory.Delete(directories[index]);
        }
        if (owner is not null) File.Delete(owner);
        Directory.Delete(directory);
    }

    private static void Retire(string workDirectory)
    {
        // 先退休不可变计划，再删 checkpoint。否则崩溃可能把已提交计划当成初始复制重新开始。
        DeleteRecord(workDirectory, ManifestName);
        DeleteRecord(workDirectory, CheckpointName);
    }

    private static void ValidateState(string workDirectory, ResumableMigrationState state)
    {
        if (state.Id == Guid.Empty || state.SourceCatalogHash.Length != SHA256.HashSizeInBytes
            || state.CandidateCatalog.Length is < 32 or > TableSchemaMigrationJournal.MaxCatalogBytes
            || BinaryPrimitives.ReadInt32LittleEndian(state.CandidateCatalog.AsSpan(8)) != TableSchemaCodec.ResumableMigrationVersion
            || state.RequestIdentity.Length > SHA256.HashSizeInBytes
            || state.Tables.Length is <= 0 or > TableSchemaMigrationJournal.MaxTables
            || state.Step < 0 || state.Step > state.Tables.Length * 2
            || state.RowsInStep < 0 || state.Cursor is { Length: 0 or > MaxCursorBytes }
            || state.Committing && (state.Discarding || state.Step != state.Tables.Length * 2 || state.Cursor is not null))
            throw new InvalidDataException("分批迁移计划或 checkpoint 无效。");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ResumableMigrationTable table in state.Tables)
        {
            if (table.SourceGeneration < 0 || table.SourceSequence < 0
                || table.SourceLayoutHash.Length is not (0 or SHA256.HashSizeInBytes)
                || _utf8.GetByteCount(table.RelativeDirectory) > MaxPathBytes
                || !paths.Add(OriginalDirectory(workDirectory, table.RelativeDirectory)))
                throw new InvalidDataException("分批迁移包含无效或重复的 rowstore 路径。");
        }
    }

    private static void RejectLink(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("分批迁移路径不能包含链接或重解析点。");
    }

    private static byte[] OwnerBytes(Guid id, int index)
    {
        var bytes = new byte[20];
        id.TryWriteBytes(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), index);
        return bytes;
    }

    private static bool HasOwner(string directory, Guid id, int index)
    {
        string path = Path.Combine(directory, OwnerName);
        RejectLink(path);
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> owner = stackalloc byte[20];
        return stream.Length == owner.Length && stream.ReadAtLeast(owner, owner.Length, throwOnEndOfStream: false) == owner.Length
            && owner.SequenceEqual(OwnerBytes(id, index));
    }

    private static BinaryReader? OpenRecord(string workDirectory, string name, ReadOnlySpan<byte> magic, int limit)
    {
        string path = Path.Combine(workDirectory, name);
        RejectLink(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        const int digestLength = SHA256.HashSizeInBytes;
        if (stream.Length <= magic.Length + digestLength || stream.Length > limit)
            throw new InvalidDataException("分批迁移记录超出字节范围。");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        if (!data.AsSpan(0, magic.Length).SequenceEqual(magic)
            || !SHA256.HashData(data.AsSpan(0, data.Length - digestLength)).AsSpan().SequenceEqual(data.AsSpan(data.Length - digestLength)))
            throw new InvalidDataException("分批迁移记录校验失败或版本不支持。");
        return new BinaryReader(new MemoryStream(data, magic.Length, data.Length - magic.Length - digestLength, writable: false), _utf8);
    }

    private static void WriteRecord(string workDirectory, string name, ReadOnlySpan<byte> magic, int limit, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        stream.Write(magic);
        using (var writer = new BinaryWriter(stream, _utf8, leaveOpen: true)) write(writer);
        if (stream.Length > limit - SHA256.HashSizeInBytes)
            throw new InvalidOperationException("分批迁移记录超出字节预算。");
        stream.Write(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
        WriteAtomic(Path.Combine(workDirectory, name), stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> data, string suffix = ".tmp")
    {
        string temporary = path + suffix;
        RejectLink(path);
        RejectLink(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        DirectoryFsync.FlushRequired(Path.GetDirectoryName(path)!);
    }

    private static void DeleteRecord(string workDirectory, string name)
    {
        string path = Path.Combine(workDirectory, name);
        RejectLink(path);
        File.Delete(path);
        string temporary = path + ".tmp";
        RejectLink(temporary);
        File.Delete(temporary);
        DirectoryFsync.FlushRequired(workDirectory);
    }

    private static Guid ReadId(BinaryReader reader)
    {
        var id = new Guid(ReadBytes(reader, 16));
        if (id == Guid.Empty) throw new InvalidDataException("分批迁移记录身份为空。");
        return id;
    }

    private static byte[]? ReadCursor(BinaryReader reader)
    {
        byte[] cursor = ReadSized(reader, MaxCursorBytes);
        return cursor.Length == 0 ? null : cursor;
    }

    private static byte[] ReadSized(BinaryReader reader, int limit)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > limit) throw new InvalidDataException("分批迁移字段长度无效。");
        return ReadBytes(reader, length);
    }

    private static byte[] ReadBytes(BinaryReader reader, int length)
    {
        if (length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("分批迁移记录存在截断字段。");
        return reader.ReadBytes(length);
    }

    private static void WriteSized(BinaryWriter writer, byte[] field)
    {
        writer.Write(field.Length);
        writer.Write(field);
    }

    private static void EndRecord(BinaryReader reader)
    {
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("分批迁移记录包含未识别的尾部数据。");
    }

    private enum MovePhase { Ready, SourceMoved, Installed }
    private sealed record DirectoryMove(string Final, string Stage, string Backup, MovePhase Phase);
}
