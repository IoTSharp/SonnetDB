using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SonnetDB.Wal;

namespace SonnetDB.Tables;

/// <summary>单张 CTAS 的私有 v1 创建日志；与已发布的表 schema/行编码分开版本化。</summary>
internal sealed record TableCreationJournal(Guid Owner, string TableName)
{
    internal const string FileName = "table-create.pending";
    internal const string OwnerFileName = ".ctas-owner";
    private const int MaximumNameBytes = 4_096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static Action<string, string>? PreparationPhaseTestHook { get; set; }
    internal static Action<string>? AfterOwnerRemovedTestHook { get; set; }
    internal string StageDirectory(string root) => Path.Combine(root, "ctas-" + Owner.ToString("N"));

    internal void Prepare(string path)
    {
        byte[] name = Utf8.GetBytes(TableName);
        if (name.Length is < 1 or > MaximumNameBytes)
            throw new InvalidOperationException("CTAS 表名超出创建日志长度预算。");
        byte[] payload = new byte[checked(32 + name.Length + 32)];
        "SDBCRT01"u8.CopyTo(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), 1);
        Owner.TryWriteBytes(payload.AsSpan(12, 16));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(28), name.Length);
        name.CopyTo(payload.AsSpan(32));
        SHA256.HashData(payload.AsSpan(0, payload.Length - 32), payload.AsSpan(payload.Length - 32));
        string temporary = path + ".tmp";
        bool createdTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                createdTemporary = true;
                PreparationPhaseTestHook?.Invoke(path, "temporary-created");
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
                PreparationPhaseTestHook?.Invoke(path, "temporary-durable");
            }
            File.Move(temporary, path, overwrite: false);
            DirectoryFsync.FlushRequired(Path.GetDirectoryName(path)!);
        }
        finally
        {
            if (createdTemporary && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static TableCreationJournal? Read(string path)
    {
        if (!File.Exists(path)) return null;
        RejectLink(path);
        long length = new FileInfo(path).Length;
        if (length is < 65 or > 64 + MaximumNameBytes)
            throw new InvalidDataException("CTAS 创建日志长度非法。");
        byte[] payload = File.ReadAllBytes(path);
        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(28));
        if (!payload.AsSpan(0, 8).SequenceEqual("SDBCRT01"u8)
            || BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8)) != 1
            || nameLength < 1 || nameLength != payload.Length - 64
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload.AsSpan(0, payload.Length - 32)),
                payload.AsSpan(payload.Length - 32)))
            throw new InvalidDataException("CTAS 创建日志损坏或版本未知，拒绝恢复。");
        return new TableCreationJournal(new Guid(payload.AsSpan(12, 16)), Utf8.GetString(payload, 32, nameLength));
    }

    internal void CreateStage(string root)
    {
        string stage = StageDirectory(root);
        if (Directory.Exists(stage)) throw new IOException("CTAS 暂存目录已经存在，拒绝接管。");
        Directory.CreateDirectory(stage);
        using (var marker = new FileStream(Path.Combine(stage, OwnerFileName), FileMode.CreateNew,
            FileAccess.Write, FileShare.None))
        {
            marker.Write(Encoding.ASCII.GetBytes(Owner.ToString("N")));
            marker.Flush(flushToDisk: true);
        }
        DirectoryFsync.FlushRequired(stage);
        DirectoryFsync.FlushRequired(root);
    }

    internal void ValidateOwner(string directory, bool allowIncompleteStage = false, bool allowEmptyRemainder = false)
    {
        RejectLink(directory);
        string marker = Path.Combine(directory, OwnerFileName);
        if (!File.Exists(marker)
            && (allowIncompleteStage || allowEmptyRemainder && !Directory.EnumerateFileSystemEntries(directory).Take(1).Any()))
            return;
        RejectLink(marker);
        if (new FileInfo(marker).Length != 32
            || File.ReadAllText(marker, Encoding.ASCII) != Owner.ToString("N"))
            throw new InvalidDataException("CTAS rowstore 归属标记不匹配，拒绝接管或删除。");
    }

    internal void Complete(string root, string finalDirectory)
    {
        // 必须先持久移除日志再移除标记；否则崩溃会留下无归属凭据的 pending final。
        File.Delete(Path.Combine(root, FileName));
        DirectoryFsync.FlushRequired(root);
        if (Directory.Exists(finalDirectory))
        {
            File.Delete(Path.Combine(finalDirectory, OwnerFileName));
            DirectoryFsync.FlushRequired(finalDirectory);
        }
    }

    internal void RemoveOwnedDirectory(string root, string directory, bool staging)
    {
        if (!Directory.Exists(directory)) return;
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || staging && !full.Equals(Path.GetFullPath(StageDirectory(root)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("CTAS 恢复路径越过当前数据库目录。");
        // 日志在 mkdir 之前已持久化，随机 stage 路径也已确认不存在；mkdir/marker 写入中断时
        // 可按日志清除此 stage。最终目录则必须具有完整、已同步的归属标记。
        ValidateOwner(full, allowIncompleteStage: staging, allowEmptyRemainder: true);
        long started = Stopwatch.GetTimestamp();
        var pending = new Queue<(string Directory, int Depth)>();
        var directories = new List<string>();
        var files = new List<string>();
        pending.Enqueue((full, 0));
        int entries = 0;
        while (pending.Count != 0 && entries <= 8_192)
        {
            CheckDeadline(started);
            (string current, int depth) = pending.Dequeue();
            if (depth > 16) throw new IOException("CTAS 恢复目录深度超出预算。");
            directories.Add(current);
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                CheckDeadline(started);
                if (++entries > 8_192) throw new IOException("CTAS 恢复文件数超出预算。");
                RejectLink(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                    pending.Enqueue((entry, depth + 1));
                else if (!entry.Equals(Path.Combine(full, OwnerFileName), StringComparison.OrdinalIgnoreCase))
                    files.Add(entry);
            }
        }
        foreach (string file in files) { CheckDeadline(started); File.Delete(file); }
        for (int index = directories.Count - 1; index > 0; index--)
        {
            CheckDeadline(started);
            Directory.Delete(directories[index]);
        }
        CheckDeadline(started);
        File.Delete(Path.Combine(full, OwnerFileName));
        AfterOwnerRemovedTestHook?.Invoke(full);
        Directory.Delete(full);
        DirectoryFsync.FlushRequired(Path.GetDirectoryName(full)!);
    }

    private static void CheckDeadline(long started)
    {
        if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
            throw new TimeoutException("CTAS 恢复清理超过十秒预算；保留日志供下次恢复。");
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("CTAS 日志或 rowstore 路径为链接，拒绝跟随。");
    }
}
