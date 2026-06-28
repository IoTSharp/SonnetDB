using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using SonnetDB.Tables;

namespace SonnetDB.Data.Internal;

/// <summary>
/// 内部连接实现契约。<see cref="SndbConnection"/> 是公开 facade，
/// 真正的工作分别由 <c>EmbeddedConnectionImpl</c> 与 <c>RemoteConnectionImpl</c> 完成。
/// </summary>
internal interface IConnectionImpl : IDisposable
{
    string DataSource { get; }
    string Database { get; }
    string ServerVersion { get; }
    ConnectionState State { get; }

    void Open();
    ValueTask OpenAsync(CancellationToken cancellationToken);
    void Close();

    /// <summary>同步执行 SQL，并将结果分发为 <see cref="IExecutionResult"/>。</summary>
    IExecutionResult Execute(string sql, SndbParameterCollection parameters, CommandBehavior behavior, object? transactionState);

    /// <summary>异步执行 SQL，并将结果分发为 <see cref="IExecutionResult"/>。</summary>
    Task<IExecutionResult> ExecuteAsync(
        string sql,
        SndbParameterCollection parameters,
        CommandBehavior behavior,
        object? transactionState,
        CancellationToken cancellationToken);

    /// <summary>
    /// 批量入库快路径（绕开 SQL Lexer→Parser→Planner）。
    /// 由 <see cref="SndbCommand"/> 在 <see cref="System.Data.CommandType.TableDirect"/> 下分发。
    /// </summary>
    /// <param name="commandText">原始 <c>CommandText</c>，可能包含可选的首行 measurement 前缀。</param>
    /// <param name="parameters">命令参数；目前识别 <c>measurement</c> / <c>onerror</c> / <c>flush</c>。</param>
    /// <returns>结果集中只含 <see cref="IExecutionResult.RecordsAffected"/>（写入行数）。</returns>
    IExecutionResult ExecuteBulk(string commandText, SndbParameterCollection parameters, object? transactionState);

    /// <summary>异步执行批量入库快路径。</summary>
    Task<IExecutionResult> ExecuteBulkAsync(
        string commandText,
        SndbParameterCollection parameters,
        object? transactionState,
        CancellationToken cancellationToken);

    /// <summary>开始一段轻事务，返回具体实现持有的事务状态。</summary>
    object BeginTransaction(IsolationLevel isolationLevel);

    /// <summary>提交一段轻事务。</summary>
    void CommitTransaction(object transactionState);

    /// <summary>异步提交一段轻事务。</summary>
    Task CommitTransactionAsync(object transactionState, CancellationToken cancellationToken);

    /// <summary>读取关系表 schema 快照，供 ADO.NET 元数据 API 使用。</summary>
    IReadOnlyList<TableSchema> SnapshotTables();

    /// <summary>回滚一段轻事务。</summary>
    void RollbackTransaction(object transactionState);

    /// <summary>异步回滚一段轻事务。</summary>
    Task RollbackTransactionAsync(object transactionState, CancellationToken cancellationToken);
}

/// <summary>
/// 单个 SQL 语句的执行结果，由具体连接实现产生。
/// </summary>
internal interface IExecutionResult : IDisposable
{
    /// <summary>受影响行数。INSERT/DELETE 为非负整数；SELECT 为 -1；DDL 通常为 0。</summary>
    int RecordsAffected { get; }

    /// <summary>列名（SELECT 才有；非 SELECT 返回空数组）。</summary>
    IReadOnlyList<string> Columns { get; }

    /// <summary>读取下一行，返回 <c>false</c> 表示已结束。</summary>
    bool ReadNextRow();

    /// <summary>异步读取下一行，返回 <c>false</c> 表示已结束。</summary>
    ValueTask<bool> ReadNextRowAsync(CancellationToken cancellationToken);

    /// <summary>读取指定列在当前行的值。</summary>
    object? GetValue(int ordinal);

    /// <summary>
    /// 返回指定列的运行时类型推断。嵌入式模式可预扫描全部行；
    /// 远程流式模式在未读到行之前可能返回 <see cref="object"/>。
    /// </summary>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
    Type GetFieldType(int ordinal);
}
