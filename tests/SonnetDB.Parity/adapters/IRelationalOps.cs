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
