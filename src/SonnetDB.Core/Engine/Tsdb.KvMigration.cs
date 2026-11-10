using SonnetDB.Kv;

namespace SonnetDB.Engine;

public sealed partial class Tsdb
{
    /// <summary>在数据库与目标 keyspace 均离线且租约排他的条件下执行迁移回滚。</summary>
    internal static void RollbackKvMigration(string databaseDirectory, string keyspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspaceDirectory);
        if (!Directory.Exists(databaseDirectory) || !File.Exists(TsdbPaths.CatalogPath(databaseDirectory)))
            throw new DirectoryNotFoundException("指定的数据库目录不存在或缺少 catalog.SDBCAT。");
        if (!Directory.Exists(keyspaceDirectory))
            throw new DirectoryNotFoundException("指定的 KV 或表目录不存在。");
        string database = NormalizeRootDirectoryForOwnership(databaseDirectory);
        string target = NormalizeRootDirectoryForOwnership(keyspaceDirectory);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = Path.TrimEndingDirectorySeparator(database) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, comparison))
            throw new ArgumentException("回滚目标必须位于指定数据库内。");
        // Reject a nested database whose actual owner would use a different lifecycle lease.
        string? ancestor = Path.GetDirectoryName(target);
        for (int depth = 0; depth < 128 && ancestor is not null && !string.Equals(ancestor, database, comparison); depth++)
        {
            if (File.Exists(TsdbPaths.CatalogPath(ancestor)))
                throw new ArgumentException("回滚目标属于另一个嵌套数据库。");
            ancestor = Path.GetDirectoryName(ancestor);
        }
        if (!string.Equals(ancestor, database, comparison))
            throw new ArgumentException("回滚目标目录层级超过限制。");
        RootDirectoryOwnership? ownership = AcquireRootDirectoryOwnership(database);
        try
        {
            using FileStream keyspaceLease = KvKeyspace.AcquireLifecycleLease(target);
            if (KvLegacyMigration.RecoverRollback(target))
                return;
            if (KvLsmLayoutMarkerFile.TryRead(target, out KvLsmLayoutMarker marker) && marker.Kind == KvLsmLayoutKind.Shared)
            {
                string rowstore = Path.Combine(database, "tables", "rowstore") + Path.DirectorySeparatorChar;
                string storePath = Path.Combine(database, "tables", "lsm");
                if (!target.StartsWith(rowstore, comparison) || !File.Exists(Path.Combine(storePath, "CURRENT")))
                    throw new InvalidOperationException("共享表目录或 tables/lsm 实例不匹配。");
                using var budget = new KvDiskReadBudget(2);
                using KvLsmStore store = KvLsmStore.Open(storePath, KvOptions.Default, budget);
                KvLegacyMigration.RollbackShared(target, store);
            }
            else
            {
                KvLegacyMigration.RollbackStandalone(target);
            }
        }
        finally
        {
            ReleaseRootDirectoryOwnership(ref ownership);
        }
    }
}
