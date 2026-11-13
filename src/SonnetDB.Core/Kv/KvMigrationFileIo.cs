namespace SonnetDB.Kv;

/// <summary>迁移元数据的实际文件 I/O 边界；只用于隔离故障夹具。</summary>
internal enum KvMigrationIoPhase
{
    BeforeWrite,
    BeforeSync,
    AfterSync,
    AfterReplace,
}

/// <summary>迁移文件故障注入入口；生产运行时为空。</summary>
internal delegate void KvMigrationIoTestHook(KvMigrationIoPhase phase, string path, FileStream? stream, ReadOnlySpan<byte> content);

/// <summary>原子发布迁移元数据；未完成的临时文件不能成为恢复决定。</summary>
internal static class KvMigrationFileIo
{
    internal static KvMigrationIoTestHook? TestHook { get; set; }

    internal static Func<string, long>? AvailableSpaceTestHook { get; set; }

    internal static void WriteAtomic(string path, ReadOnlySpan<byte> content)
    {
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            TestHook?.Invoke(KvMigrationIoPhase.BeforeWrite, temp, stream, content);
            stream.Write(content);
            TestHook?.Invoke(KvMigrationIoPhase.BeforeSync, temp, stream, content);
            stream.Flush(flushToDisk: true);
            TestHook?.Invoke(KvMigrationIoPhase.AfterSync, temp, stream, content);
        }

        File.Move(temp, path, overwrite: true);
        TestHook?.Invoke(KvMigrationIoPhase.AfterReplace, path, null, content);
    }
}
