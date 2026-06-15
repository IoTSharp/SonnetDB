using System.Data;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Relational;

/// <summary>
/// 关系型 parity 场景基类：封装能力检查、清理和常见 SQL 方言差异。
/// </summary>
public abstract class RelationalScenarioBase : IScenario
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract Capability Required { get; }

    /// <inheritdoc />
    public async Task<ScenarioResult> RunAsync(IDataPlane plane, ScenarioContext ctx)
    {
        ArgumentNullException.ThrowIfNull(plane);
        ArgumentNullException.ThrowIfNull(ctx);

        if ((plane.Capabilities & Required) != Required)
        {
            return ScenarioSkipped(
                $"backend '{plane.BackendName}' lacks required capabilities: {DescribeCapabilities(Required & ~plane.Capabilities)}");
        }

        var ops = plane.Relational;
        try
        {
            return await RunRelationalAsync(ops, ctx).ConfigureAwait(false);
        }
        finally
        {
            await CleanupAsync(ops, ctx.Cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 执行当前关系型场景。
    /// </summary>
    /// <param name="ops">关系型操作集合。</param>
    /// <param name="ctx">场景上下文。</param>
    /// <returns>场景结果。</returns>
    protected abstract Task<ScenarioResult> RunRelationalAsync(IRelationalOps ops, ScenarioContext ctx);

    /// <summary>
    /// 返回场景使用的表名列表；基类会在 finally 中按反序 drop。
    /// </summary>
    /// <returns>表名列表。</returns>
    protected virtual IReadOnlyList<string> TablesToDrop => [];

    /// <summary>
    /// 构造跳过结果。
    /// </summary>
    /// <param name="reason">跳过原因。</param>
    /// <returns>场景结果。</returns>
    protected static ScenarioResult ScenarioSkipped(string reason)
        => new() { Pass = true, GapReason = reason };

    /// <summary>
    /// 构造基于查询结果的场景结果。
    /// </summary>
    /// <param name="result">查询结果。</param>
    /// <param name="expectedRows">期望行。</param>
    /// <returns>场景结果。</returns>
    protected static ScenarioResult ScenarioFromRows(
        RelationalSqlResult result,
        params RelationalSqlRow[] expectedRows)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(expectedRows);

        var pass = ResultRowsEqual(result.Rows, expectedRows);
        var scenarioResult = new ScenarioResult
        {
            Pass = pass,
            SqlResult = result,
        };
        scenarioResult.Metrics["row_count"] = result.Rows.Count;
        return scenarioResult;
    }

    /// <summary>
    /// 构造规范化 SQL 行。
    /// </summary>
    /// <param name="values">列值。</param>
    /// <returns>SQL 行。</returns>
    protected static RelationalSqlRow Row(params object?[] values)
        => new(values);

    /// <summary>
    /// 返回当前后端的 64 位整数类型名。
    /// </summary>
    /// <param name="ops">关系型操作集合。</param>
    /// <returns>类型名。</returns>
    protected static string IntType(IRelationalOps ops)
        => ops.Dialect == RelationalDialect.Postgres ? "BIGINT" : "INT";

    /// <summary>
    /// 返回当前后端的字符串类型名。
    /// </summary>
    /// <param name="ops">关系型操作集合。</param>
    /// <returns>类型名。</returns>
    protected static string StringType(IRelationalOps ops)
        => ops.Dialect == RelationalDialect.Postgres ? "TEXT" : "STRING";

    /// <summary>
    /// 返回当前后端的字符串类型名；保留 TextType 便于场景表达 SQL 常用术语。
    /// </summary>
    /// <param name="ops">关系型操作集合。</param>
    /// <returns>类型名。</returns>
    protected static string TextType(IRelationalOps ops) => StringType(ops);

    /// <summary>
    /// 返回当前后端的布尔类型名。
    /// </summary>
    /// <param name="ops">关系型操作集合。</param>
    /// <returns>类型名。</returns>
    protected static string BoolType(IRelationalOps ops)
        => ops.Dialect == RelationalDialect.Postgres ? "BOOLEAN" : "BOOL";

    /// <summary>
    /// 返回 SQL 布尔字面量。
    /// </summary>
    /// <param name="value">布尔值。</param>
    /// <returns>SQL 字面量。</returns>
    protected static string BoolLiteral(bool value) => value ? "TRUE" : "FALSE";

    /// <summary>
    /// 执行表清理，供需要提前清理的场景使用；基类 finally 仍会做兜底清理。
    /// </summary>
    /// <param name="ops">关系型操作集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="tables">表名列表。</param>
    protected static async Task DropTablesAsync(IRelationalOps ops, CancellationToken ct, params string[] tables)
    {
        foreach (var table in tables)
        {
            try { await ops.ExecuteAsync($"DROP TABLE IF EXISTS {table}", ct).ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// 构造基于自定义 pass 判定的 SQL 场景结果。
    /// </summary>
    /// <param name="result">查询结果。</param>
    /// <param name="pass">是否通过。</param>
    /// <returns>场景结果。</returns>
    protected static ScenarioResult FromSql(RelationalSqlResult result, bool pass)
    {
        var scenarioResult = new ScenarioResult
        {
            Pass = pass,
            SqlResult = result,
        };
        scenarioResult.Metrics["row_count"] = result.Rows.Count;
        return scenarioResult;
    }

    /// <summary>
    /// 按方言执行事务内查询，主要用于 ReadCommitted 场景。
    /// </summary>
    /// <param name="session">关系型会话。</param>
    /// <param name="isolationLevel">隔离级别。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>事务。</returns>
    protected static Task<IRelationalTransaction> BeginTransactionAsync(
        IRelationalSession session,
        IsolationLevel isolationLevel,
        CancellationToken ct)
        => session.BeginTransactionAsync(isolationLevel, ct);

    private async Task CleanupAsync(IRelationalOps ops, CancellationToken ct)
    {
        foreach (var table in TablesToDrop.Reverse())
        {
            try { await ops.ExecuteAsync($"DROP TABLE IF EXISTS {table}", ct).ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static bool ResultRowsEqual(IReadOnlyList<RelationalSqlRow> actual, IReadOnlyList<RelationalSqlRow> expected)
    {
        if (actual.Count != expected.Count)
            return false;
        for (var i = 0; i < actual.Count; i++)
        {
            if (actual[i].Values.Count != expected[i].Values.Count)
                return false;
            for (var j = 0; j < actual[i].Values.Count; j++)
            {
                if (!Equals(actual[i].Values[j], expected[i].Values[j]))
                    return false;
            }
        }

        return true;
    }

    private static string DescribeCapabilities(Capability capabilities)
        => capabilities == Capability.None ? "none" : capabilities.ToString();
}
