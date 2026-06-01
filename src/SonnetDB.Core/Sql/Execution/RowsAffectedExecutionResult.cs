namespace SonnetDB.Sql.Execution;

/// <summary>
/// 关系表 <c>UPDATE</c> / <c>DELETE</c> / DDL 语句的受影响行数结果。
/// </summary>
/// <param name="Target">被操作的对象名称。</param>
/// <param name="RowsAffected">受影响行数。</param>
/// <param name="Operation">操作名称。</param>
public sealed record RowsAffectedExecutionResult(
    string Target,
    int RowsAffected,
    string Operation);
