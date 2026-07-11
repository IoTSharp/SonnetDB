using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Document;

/// <summary>Document 参考 parity 场景契约。</summary>
public interface IDocumentParityScenario
{
    /// <summary>场景稳定名称。</summary>
    string Name { get; }

    /// <summary>在指定 Document 后端执行场景。</summary>
    Task<ScenarioResult> RunAsync(IDocumentOps ops, ScenarioContext context);
}

/// <summary>Document 参考 parity 场景基类。</summary>
public abstract class DocumentScenarioBase : IDocumentParityScenario
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract Task<ScenarioResult> RunAsync(IDocumentOps ops, ScenarioContext context);

    /// <summary>生成单次 run 独占的集合名。</summary>
    protected string Collection(ScenarioContext context, string suffix)
        => ("p173_" + context.RunId.Replace("-", "_", StringComparison.Ordinal) + "_" + suffix).ToLowerInvariant();

    /// <summary>构造可被现有 diff runner 比较的结果集。</summary>
    protected static ScenarioResult Rows(IReadOnlyList<string> columns, params object?[][] rows)
        => new()
        {
            Pass = true,
            SqlResult = new RelationalSqlResult(
                columns,
                rows.Select(static row => new RelationalSqlRow(row)).ToArray(),
                -1),
        };
}
