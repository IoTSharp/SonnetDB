using SonnetDB.Sql;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Views;

/// <summary>
/// 一个持久化逻辑视图的不可变定义。
/// </summary>
public sealed class ViewDefinition
{
    private ViewDefinition(
        string name,
        string definitionSql,
        SelectStatement query,
        IReadOnlyList<string> dependencies,
        long createdAtUtcTicks)
    {
        Name = name;
        DefinitionSql = definitionSql;
        Query = query;
        Dependencies = dependencies;
        CreatedAtUtcTicks = createdAtUtcTicks;
    }

    /// <summary>视图名称（区分大小写）。</summary>
    public string Name { get; }

    /// <summary>不含 <c>CREATE VIEW ... AS</c> 前缀的 SELECT 定义文本。</summary>
    public string DefinitionSql { get; }

    /// <summary>从 <see cref="DefinitionSql"/> 解析得到的 SELECT AST。</summary>
    public SelectStatement Query { get; }

    /// <summary>视图直接引用的数据源名称，按字典序排列。</summary>
    public IReadOnlyList<string> Dependencies { get; }

    /// <summary>视图创建时间（UTC ticks）。</summary>
    public long CreatedAtUtcTicks { get; }

    /// <summary>
    /// 从 SELECT SQL 创建并校验逻辑视图定义。
    /// </summary>
    /// <param name="name">视图名称。</param>
    /// <param name="definitionSql">不含 <c>CREATE VIEW ... AS</c> 前缀的 SELECT SQL。</param>
    /// <param name="createdAtUtcTicks">创建时间（UTC ticks）；为 0 时使用当前时间。</param>
    /// <returns>校验通过的视图定义。</returns>
    /// <exception cref="ArgumentException">名称或 SQL 为空，或者定义包含参数占位符时抛出。</exception>
    /// <exception cref="SqlParseException">定义不是合法 SELECT 时抛出。</exception>
    public static ViewDefinition Create(
        string name,
        string definitionSql,
        long createdAtUtcTicks = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionSql);

        var query = SqlParser.Parse(definitionSql) as SelectStatement
            ?? throw new ArgumentException("逻辑视图定义必须是 SELECT 语句。", nameof(definitionSql));
        return Create(name, definitionSql, query, createdAtUtcTicks);
    }

    internal static ViewDefinition Create(
        string name,
        string definitionSql,
        SelectStatement query,
        long createdAtUtcTicks = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionSql);
        ArgumentNullException.ThrowIfNull(query);
        if (createdAtUtcTicks < 0 || createdAtUtcTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(nameof(createdAtUtcTicks));

        var analysis = ViewDependencyCollector.Analyze(query);
        if (analysis.HasParameters)
            throw new ArgumentException("持久化视图定义不能包含参数占位符。", nameof(definitionSql));

        return new ViewDefinition(
            name,
            definitionSql.Trim(),
            query,
            analysis.Dependencies,
            createdAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : createdAtUtcTicks);
    }
}
