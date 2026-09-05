namespace SonnetDB.Sql.Execution;

/// <summary>单次 SQL 执行向过程/触发器运行时传递的治理选项。</summary>
public sealed record SqlExecutionOptions
{
    /// <summary>默认嵌入式执行选项。</summary>
    public static SqlExecutionOptions Default { get; } = new();

    /// <summary>取消令牌。</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>审计调用方标识；不记录参数值或行数据。</summary>
    public string Caller { get; init; } = "embedded";

    /// <summary>调用方是否具备当前数据库写权限。</summary>
    public bool CanWrite { get; init; } = true;

    /// <summary>
    /// 调用方是否具备当前数据库的 Admin 权限。创建或修改可能触发外部网络连接、监听端口等
    /// 高风险基础设施定义时，必须同时检查此权限；嵌入式默认值保持向后兼容。
    /// </summary>
    public bool CanAdminister { get; init; } = true;

    /// <summary>单次调用链最多执行的 body 语句数。</summary>
    public int MaxRoutineStatements { get; init; } = 64;

    /// <summary>过程与触发器调用链最大深度。</summary>
    public int MaxRoutineDepth { get; init; } = 8;

    /// <summary>单次过程调用累计允许返回的最大结果行数。</summary>
    public int MaxRoutineResultRows { get; init; } = 10_000;

    /// <summary>
    /// 可选的单查询阻塞算子内存上限（字节）；为空时使用数据库的
    /// <see cref="SonnetDB.Engine.SqlMemoryOptions.QueryLimitBytes"/>。
    /// </summary>
    public long? BlockingOperatorMemoryLimitBytes { get; init; }

    /// <summary>是否允许在估算收益成立且资源足够时启用受控 SQL 并行。</summary>
    public bool EnableParallelism { get; init; } = true;

    /// <summary>单条 SQL 的并行 worker 上限；为空时使用数据库配置。</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>覆盖数据库默认并行准入行数阈值；为空时使用数据库配置。</summary>
    public long? ParallelismMinRows { get; init; }

    /// <summary>
    /// 参数无关的查询 fingerprint。未提供时由 AST 生成；不会保存 SQL 参数或行内容。
    /// </summary>
    public string? QueryFingerprint { get; init; }

    /// <summary>可选的内部执行证据收集器；不进入公开 JSON 或持久化合同。</summary>
    internal SqlExecutionMetrics? Metrics { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Caller);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRoutineStatements);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRoutineDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRoutineDepth, 64);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRoutineResultRows);
        if (BlockingOperatorMemoryLimitBytes is { } memoryLimit)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryLimit);
        if (MaxDegreeOfParallelism is { } degree)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(degree);
        if (ParallelismMinRows is { } minRows)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minRows);
    }
}
