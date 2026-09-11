namespace SonnetDB.Configuration;

/// <summary>对象派生后台任务的有界调度选项，绑定于 SemanticSearch:ObjectProcessing。</summary>
public sealed class ObjectProcessingOptions
{
    /// <summary>进程内等待队列容量；满载任务仍保存在持久 due 队列中。</summary>
    public int QueueCapacity { get; set; } = 128;
    /// <summary>同时生成缩略图或语义索引的任务数量。</summary>
    public int WorkerCount { get; set; } = 1;
    /// <summary>到期队列与旧任务恢复的轮询间隔，单位毫秒。</summary>
    public int RecoveryIntervalMilliseconds { get; set; } = 2_000;
    /// <summary>每库每轮最多读取的到期任务数。</summary>
    public int RecoveryPageSize { get; set; } = 64;
    /// <summary>每库每轮最多迁移的旧任务记录数，包含终态记录。</summary>
    public int LegacyMigrationPageSize { get; set; } = 64;
    /// <summary>一轮恢复最多访问的数据库数，后续轮次继续轮转。</summary>
    public int DatabasesPerPass { get; set; } = 8;
    /// <summary>单轮恢复和迁移共用的可取消时间预算，单位毫秒。</summary>
    public int RecoveryBudgetMilliseconds { get; set; } = 250;
    /// <summary>单个派生任务的执行期限，单位秒；租约额外保留三十秒供状态提交。</summary>
    public int ProcessingTimeoutSeconds { get; set; } = 120;
    /// <summary>同一输入最多执行次数，永久输入错误不会重试。</summary>
    public int MaxAttempts { get; set; } = 5;
    /// <summary>存储或 checkpoint 拒绝后的数据库冷却时间，单位秒。</summary>
    public int StorageCooldownSeconds { get; set; } = 30;
    /// <summary>单次 Bucket 语义回填最多读取的对象数。</summary>
    public int BackfillPageSize { get; set; } = 128;
    /// <summary>单次 Bucket 语义回填允许占用的时间预算（毫秒）。</summary>
    public int BackfillBudgetMilliseconds { get; set; } = 250;

    /// <summary>复制并收紧配置范围，避免热修改影响已启动 worker 的资源合同。</summary>
    internal ObjectProcessingOptions BoundedCopy() => new()
    {
        QueueCapacity = Math.Clamp(QueueCapacity, 1, 4_096),
        WorkerCount = Math.Clamp(WorkerCount, 1, 4),
        RecoveryIntervalMilliseconds = Math.Clamp(RecoveryIntervalMilliseconds, 100, 60_000),
        RecoveryPageSize = Math.Clamp(RecoveryPageSize, 1, 512),
        LegacyMigrationPageSize = Math.Clamp(LegacyMigrationPageSize, 1, 512),
        DatabasesPerPass = Math.Clamp(DatabasesPerPass, 1, 64),
        RecoveryBudgetMilliseconds = Math.Clamp(RecoveryBudgetMilliseconds, 10, 5_000),
        ProcessingTimeoutSeconds = Math.Clamp(ProcessingTimeoutSeconds, 1, 600),
        MaxAttempts = Math.Clamp(MaxAttempts, 1, 20),
        StorageCooldownSeconds = Math.Clamp(StorageCooldownSeconds, 1, 300),
        BackfillPageSize = Math.Clamp(BackfillPageSize, 1, 1_000),
        BackfillBudgetMilliseconds = Math.Clamp(BackfillBudgetMilliseconds, 10, 5_000),
    };
}
