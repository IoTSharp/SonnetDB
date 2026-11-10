namespace SonnetDB.Kv;

/// <summary>
/// KV keyspace 的存储引擎。
/// </summary>
public enum KvStorageEngine
{
    /// <summary>覆盖层 + 单一有序状态文件的原有引擎。</summary>
    Legacy = 0,

    /// <summary>
    /// 分层 LSM 引擎（预览）：memtable + 多层 SST + manifest，快照为 O(1)。
    /// 只对新建或已是 LSM 布局的目录生效；已有 legacy 数据的目录继续使用 legacy 引擎。
    /// </summary>
    Lsm = 1,
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
