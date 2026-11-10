namespace SonnetDB.Kv;

/// <summary>
/// KV keyspace 的存储引擎。
/// </summary>
public enum KvStorageEngine
{
    /// <summary>覆盖层 + 单一有序状态文件的原有引擎。</summary>
    Legacy = 0,

    /// <summary>
    /// 分层 LSM 引擎（默认）：memtable + 多层 SST + manifest，快照为 O(1)。
    /// 已有 legacy 数据的目录按 <see cref="KvOptions.LegacyMigration"/> 在打开时迁移。
    /// </summary>
    Lsm = 1,
}

/// <summary>
/// 选择 LSM 引擎时对已有 legacy 数据目录的处理方式。
/// </summary>
public enum KvLegacyMigrationMode
{
    /// <summary>打开时自动迁移到 LSM；磁盘空间不足时跳过迁移并继续以 legacy 打开。</summary>
    Auto = 0,

    /// <summary>不迁移，继续以 legacy 打开（运维逃生口）。</summary>
    Disabled = 1,
}

/// <summary>
/// 测试进程切换 <see cref="KvOptions.StorageEngine"/> 默认值的入口（双引擎测试矩阵），生产代码不得设置。
/// </summary>
/// <remarks>
/// 独立于 <see cref="KvOptions"/> 的静态类：设置它不会触发 <see cref="KvOptions"/> 的类型初始化，
/// 因此必须在任何 <see cref="KvOptions"/> 实例（包括 <see cref="KvOptions.Default"/>）创建之前设置。
/// 显式指定的 <see cref="KvOptions.StorageEngine"/> 不受影响。
/// </remarks>
internal static class KvStorageEngineTestDefaults
{
    /// <summary>非 null 时作为新建 <see cref="KvOptions"/> 的默认引擎。</summary>
    internal static KvStorageEngine? Override;
}
