namespace SonnetDB.Sql.Execution;

/// <summary>关系表轻事务的隔离级别。</summary>
public enum SqlTransactionIsolationLevel
{
    /// <summary>读取已提交数据并叠加本事务写入。</summary>
    ReadCommitted,

    /// <summary>以表级首次读版本在提交时校验的乐观可串行化隔离。</summary>
    Serializable,
}
