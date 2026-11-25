namespace SonnetDB.Protocol;

/// <summary>显式远程 SQL 事务内有序 DML 批处理的 REST 传输合同。</summary>
public static class SqlTransactionDmlBatchProtocol
{
    /// <summary>成功开始事务时在响应中声明能力的标头。</summary>
    public const string CapabilityHeader = "X-SonnetDB-Transaction-Dml-Batch";

    /// <summary>支持逐条结果和受影响行数条件的协议版本。</summary>
    public const string Version = "1";

    /// <summary>单个批处理请求最多包含的命令数。</summary>
    public const int MaxStatements = 256;

    /// <summary>单个批处理请求 JSON 的 UTF-8 字节上限。</summary>
    public const int MaxRequestBytes = 4 * 1024 * 1024;

    /// <summary>批处理最后一条 NDJSON 记录的类型。</summary>
    public const string EndRecordType = "batch_end";

    /// <summary>所有命令均已执行。</summary>
    public const string Completed = "completed";

    /// <summary>在首个受影响行数与期望不符的命令之后停止。</summary>
    public const string RecordsAffectedMismatch = "records_affected_mismatch";
}
