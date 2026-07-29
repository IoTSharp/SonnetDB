using SonnetDB.Sql;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Routines;

/// <summary>持久化关系表 AFTER ROW 触发器的不可变定义。</summary>
public sealed class TriggerDefinition
{
    private TriggerDefinition(
        string name,
        string tableName,
        SqlTriggerEvent triggerEvent,
        SqlExpression? when,
        string? whenSql,
        string bodySql,
        IReadOnlyList<SqlStatement> statements,
        SqlRoutineAnalysis analysis,
        long createdAtUtcTicks)
    {
        Name = name;
        TableName = tableName;
        Event = triggerEvent;
        When = when;
        WhenSql = whenSql;
        BodySql = bodySql;
        Statements = statements;
        ObjectDependencies = analysis.ObjectDependencies
            .Append(tableName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        RowColumns = analysis.RowColumns;
        CreatedAtUtcTicks = createdAtUtcTicks;
    }

    /// <summary>触发器名称。</summary>
    public string Name { get; }
    /// <summary>目标关系表名称。</summary>
    public string TableName { get; }
    /// <summary>AFTER 行事件。</summary>
    public SqlTriggerEvent Event { get; }
    /// <summary>可选 WHEN 条件 AST。</summary>
    public SqlExpression? When { get; }
    /// <summary>可选 WHEN 条件的规范化 SQL。</summary>
    public string? WhenSql { get; }
    /// <summary>不含外围 BEGIN/END 的受限 SQL body。</summary>
    public string BodySql { get; }
    /// <summary>已经解析的 SQL body AST。</summary>
    public IReadOnlyList<SqlStatement> Statements { get; }
    /// <summary>目标表及 body 直接引用的数据对象。</summary>
    public IReadOnlyList<string> ObjectDependencies { get; }
    /// <summary>通过 OLD/NEW 引用的目标表列。</summary>
    public IReadOnlyList<string> RowColumns { get; }
    /// <summary>语言标识；首版固定为 SQL。</summary>
    public string Language => "SQL";
    /// <summary>创建时间（UTC ticks）。</summary>
    public long CreatedAtUtcTicks { get; }

    /// <summary>从 CREATE TRIGGER AST 创建并校验定义。</summary>
    /// <param name="statement">CREATE TRIGGER AST。</param>
    /// <param name="createdAtUtcTicks">创建时间；0 表示当前 UTC。</param>
    /// <returns>不可变触发器定义。</returns>
    public static TriggerDefinition Create(
        CreateTriggerStatement statement,
        long createdAtUtcTicks = 0)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (!string.Equals(statement.Language, "SQL", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("触发器首版只支持 LANGUAGE SQL。", nameof(statement));
        return Create(
            statement.Name,
            statement.TableName,
            statement.Event,
            statement.When,
            statement.WhenSql,
            statement.BodySql,
            statement.Body,
            createdAtUtcTicks);
    }

    internal static TriggerDefinition Restore(
        string name,
        string tableName,
        SqlTriggerEvent triggerEvent,
        string? whenSql,
        string bodySql,
        long createdAtUtcTicks)
        => Create(
            name,
            tableName,
            triggerEvent,
            whenSql is null ? null : SqlParser.ParsePredicate(whenSql),
            whenSql,
            bodySql,
            SqlParser.ParseScript(bodySql),
            createdAtUtcTicks);

    private static TriggerDefinition Create(
        string name,
        string tableName,
        SqlTriggerEvent triggerEvent,
        SqlExpression? when,
        string? whenSql,
        string bodySql,
        IReadOnlyList<SqlStatement> statements,
        long createdAtUtcTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodySql);
        ArgumentNullException.ThrowIfNull(statements);
        if (createdAtUtcTicks < 0 || createdAtUtcTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(nameof(createdAtUtcTicks));

        var analysis = SqlRoutineAnalyzer.AnalyzeTrigger(statements, when, triggerEvent);
        return new TriggerDefinition(
            name,
            tableName,
            triggerEvent,
            when,
            whenSql,
            bodySql.Trim(),
            statements.ToArray(),
            analysis,
            createdAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : createdAtUtcTicks);
    }
}
