using System.Data;

namespace SonnetDB.Parity.Adapters;

/// <summary>
/// 关系型支柱的语义操作集合。注意这是**语义接口**而非裸 SQL 透传：
/// 每个适配器把这些方法翻译为自己方言（SonnetDB <c>INT/STRING</c> vs Postgres <c>BIGINT/TEXT</c>），
/// 从而让 <see cref="Runner.ResultDiffer"/> 始终比较同构的强类型行。
/// </summary>
/// <remarks>
/// PR #127 仅承载 hello-world 冒烟所需的最小面：建表 / 批量插入 / 排序读全表 / 清理。
/// PR #128 关系型场景套件会把行模型推广为按列定型的通用结构以支持任意 schema。
/// </remarks>
public interface IRelationalOps
{
    /// <summary>当前后端使用的 SQL 方言。</summary>
    RelationalDialect Dialect { get; }

    /// <summary>
    /// 执行不返回结果集的 SQL，并返回受影响行数。
    /// </summary>
    /// <param name="sql">SQL 文本。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数；DDL 通常为 0。</returns>
    Task<int> ExecuteAsync(string sql, CancellationToken ct);

    /// <summary>
    /// 执行查询 SQL，并把结果规范化为跨后端可比较的行集合。
    /// </summary>
    /// <param name="sql">SQL 文本。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>查询结果。</returns>
    Task<RelationalSqlResult> QueryAsync(string sql, CancellationToken ct);

    /// <summary>
    /// 打开一个独立会话，用于并发可见性、隔离级别等需要多连接的场景。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>独立关系型会话。</returns>
    Task<IRelationalSession> OpenSessionAsync(CancellationToken ct);

    /// <summary>
    /// 确保 <c>devices(id, name)</c> 表存在且为空（已存在则先 drop 再 create）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    Task EnsureDeviceTableAsync(CancellationToken ct);

    /// <summary>
    /// 向 <c>devices</c> 批量插入若干行。
    /// </summary>
    /// <param name="rows">待插入的行集合。</param>
    /// <param name="ct">取消令牌。</param>
    Task InsertDevicesAsync(IReadOnlyList<RelationalRow> rows, CancellationToken ct);

    /// <summary>
    /// 读取 <c>devices</c> 全表，按 <c>id</c> 升序返回。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>按 <c>id</c> 升序排列的行集合。</returns>
    Task<IReadOnlyList<RelationalRow>> SelectDevicesOrderByIdAsync(CancellationToken ct);

    /// <summary>
    /// 删除 <c>devices</c> 表（清理，幂等）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    Task DropDeviceTableAsync(CancellationToken ct);
}

/// <summary>
/// 关系型冒烟场景使用的强类型行：一个 64 位整型主键 + 一个字符串名称。
/// 记录类型的值相等语义让 <see cref="Runner.ResultDiffer"/> 与断言天然简洁。
/// </summary>
/// <param name="Id">行主键。</param>
/// <param name="Name">设备名称。</param>
public sealed record RelationalRow(long Id, string Name);

/// <summary>
/// 关系型 SQL 方言标识，用于场景选择类型名、RETURNING 等语法差异。
/// </summary>
public enum RelationalDialect
{
    /// <summary>SonnetDB SQL 方言。</summary>
    SonnetDb,

    /// <summary>PostgreSQL SQL 方言。</summary>
    Postgres,
}

/// <summary>
/// 通用关系型查询结果。
/// </summary>
/// <param name="Columns">结果列名。</param>
/// <param name="Rows">规范化后的行集合。</param>
/// <param name="RecordsAffected">受影响行数；SELECT 通常为 -1。</param>
public sealed record RelationalSqlResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<RelationalSqlRow> Rows,
    int RecordsAffected);

/// <summary>
/// 通用关系型结果行，按列序存放规范化值。
/// </summary>
/// <param name="Values">列值集合。</param>
public sealed record RelationalSqlRow(IReadOnlyList<object?> Values);

/// <summary>
/// 独立关系型会话，封装单条物理连接和可选事务。
/// </summary>
public interface IRelationalSession : IAsyncDisposable
{
    /// <summary>
    /// 在当前会话中执行不返回结果集的 SQL。
    /// </summary>
    /// <param name="sql">SQL 文本。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受影响行数。</returns>
    Task<int> ExecuteAsync(string sql, CancellationToken ct);

    /// <summary>
    /// 在当前会话中执行查询 SQL。
    /// </summary>
    /// <param name="sql">SQL 文本。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>查询结果。</returns>
    Task<RelationalSqlResult> QueryAsync(string sql, CancellationToken ct);

    /// <summary>
    /// 开启事务。
    /// </summary>
    /// <param name="isolationLevel">隔离级别。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>事务句柄。</returns>
    Task<IRelationalTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken ct);
}

/// <summary>
/// 关系型事务句柄。
/// </summary>
public interface IRelationalTransaction : IAsyncDisposable
{
    /// <summary>
    /// 提交事务。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    Task CommitAsync(CancellationToken ct);

    /// <summary>
    /// 回滚事务。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    Task RollbackAsync(CancellationToken ct);
}
