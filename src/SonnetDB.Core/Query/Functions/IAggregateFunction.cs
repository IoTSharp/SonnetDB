using SonnetDB.Catalog;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Query.Functions;

/// <summary>
/// 聚合函数的最小抽象，用于注册表解析与查询执行。
/// <para>
/// 内置 7 个聚合通过 <see cref="LegacyAggregator"/> 保留枚举映射；其中数值统计桥接
/// <c>QueryEngine</c> / <c>SelectExecutor</c> 快路径，first/last 与分类 min/max 由
/// <see cref="CreateAccumulator"/> 提供保留原始类型的累加器。PR #52 起的扩展聚合
/// （stddev / percentile / tdigest_agg / ...）同样由独立的可合并累加器实现。
/// </para>
/// </summary>
public interface IAggregateFunction : ISqlFunction
{
    /// <summary>
    /// 获取该聚合函数可接受的时序 FIELD 类型。
    /// <para>
    /// 默认值保持历史自定义聚合兼容性：接受 Float64、Int64 与 Boolean。
    /// 支持字符串、向量或地理点的实现必须显式声明对应能力。
    /// </para>
    /// </summary>
    AggregateFieldTypes AcceptedFieldTypes
        => AggregateFieldTypes.Numeric | AggregateFieldTypes.Boolean;

    /// <summary>
    /// 桥接到现有高性能执行路径的 legacy 聚合枚举。
    /// <para>过渡期字段：内置 7 个聚合通过该值复用 <c>QueryEngine</c> / <c>SelectExecutor</c> 快路径；
    /// 扩展聚合（PR #52+）返回 <c>null</c>，由 <see cref="CreateAccumulator"/> 接管执行。</para>
    /// </summary>
    Aggregator? LegacyAggregator { get; }

    /// <summary>
    /// 校验 SQL 调用并解析目标字段名。
    /// <para>返回 <c>null</c> 表示允许 <c>*</c> 形式（仅用于 <c>count(*)</c>）。</para>
    /// </summary>
    /// <param name="call">SQL 中的函数调用 AST 节点。</param>
    /// <param name="schema">目标 measurement 的 schema。</param>
    /// <returns>目标字段名；<c>count(*)</c> 形式返回 <c>null</c>。</returns>
    /// <exception cref="InvalidOperationException">参数个数、列存在性或类型不满足函数约束时抛出。</exception>
    string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema);

    /// <summary>
    /// 创建本次聚合调用的累加器；用于 selector、categorical 与扩展聚合。
    /// <para>返回 <c>null</c> 表示该调用仍走 legacy fast-path（基于 <see cref="LegacyAggregator"/>）。</para>
    /// <para>
    /// 实现可在创建时解析参数化常量（如 <c>percentile(field, 95)</c> 中的分位点 95），
    /// 不同参数会产生不同累加器配置。返回的累加器同样需要满足
    /// <see cref="IAggregateAccumulator.Merge"/> 的可合并性约束，便于跨段 / 跨桶组合。
    /// </para>
    /// </summary>
    /// <param name="call">SQL 中的函数调用 AST 节点。</param>
    /// <param name="schema">目标 measurement 的 schema。</param>
    /// <returns>新的累加器实例；当前调用使用 legacy 快路径时返回 <c>null</c>。</returns>
    IAggregateAccumulator? CreateAccumulator(FunctionCallExpression call, MeasurementSchema schema) => null;
}
