using SonnetDB.Sql;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Routines;

/// <summary>持久化关系表触发器的不可变定义。</summary>
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
        long createdAtUtcTicks,
        SqlTriggerTiming timing, SqlTriggerLevel level, string? oldTableName, string? newTableName,
        bool isConstraint, bool initiallyDeferred)
    {
        Name = name;
        TableName = tableName;
        Event = triggerEvent;
        Timing = timing;
        Level = level;
        OldTableName = oldTableName;
        NewTableName = newTableName;
        IsConstraint = isConstraint;
        InitiallyDeferred = initiallyDeferred;
        When = when;
        WhenSql = whenSql;
        BodySql = bodySql;
        Statements = statements;
        ObjectDependencies = analysis.ObjectDependencies
            .Where(value => !string.Equals(value, oldTableName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, newTableName, StringComparison.OrdinalIgnoreCase))
            .Append(tableName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        RowColumns = analysis.RowColumns;
        CreatedAtUtcTicks = createdAtUtcTicks;
        ExecutionOrder = createdAtUtcTicks;
    }

    private TriggerDefinition(TriggerDefinition source, string name, bool enabled, long order)
    {
        Name = name;
        Enabled = enabled;
        ExecutionOrder = order;
        TableName = source.TableName;
        Event = source.Event;
        Timing = source.Timing;
        Level = source.Level;
        OldTableName = source.OldTableName;
        NewTableName = source.NewTableName;
        IsConstraint = source.IsConstraint;
        InitiallyDeferred = source.InitiallyDeferred;
        When = source.When;
        WhenSql = source.WhenSql;
        BodySql = source.BodySql;
        Statements = source.Statements;
        ObjectDependencies = source.ObjectDependencies;
        RowColumns = source.RowColumns;
        CreatedAtUtcTicks = source.CreatedAtUtcTicks;
    }

    /// <summary>是否参与后续语句的触发；禁用定义继续保护其对象依赖。</summary>
    public bool Enabled { get; } = true;
    /// <summary>同表、事件、时机、粒度和提交阶段内的持久化执行顺序；启停和重命名不改变此值。</summary>
    public long ExecutionOrder { get; }

    internal TriggerDefinition WithLifecycle(string? name = null, bool? enabled = null, long? order = null)
        => new(this, name ?? Name, enabled ?? Enabled, order ?? ExecutionOrder);

    /// <summary>触发器名称。</summary>
    public string Name { get; }
    /// <summary>目标关系表名称。</summary>
    public string TableName { get; }
    /// <summary>关系表变更事件。</summary>
    public SqlTriggerEvent Event { get; }
    /// <summary>执行时机。</summary>
    public SqlTriggerTiming Timing { get; }
    /// <summary>执行粒度。</summary>
    public SqlTriggerLevel Level { get; }
    /// <summary>只读 OLD TABLE 别名。</summary>
    public string? OldTableName { get; }
    /// <summary>只读 NEW TABLE 别名。</summary>
    public string? NewTableName { get; }
    /// <summary>是否为提交阶段校验的约束触发器。</summary>
    public bool IsConstraint { get; }
    /// <summary>是否在事务提交阶段执行。</summary>
    public bool InitiallyDeferred { get; }
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
            createdAtUtcTicks, statement.Timing, statement.Level, statement.OldTableName, statement.NewTableName,
            statement.IsConstraint, statement.InitiallyDeferred);
    }

    internal static TriggerDefinition Restore(
        string name,
        string tableName,
        SqlTriggerEvent triggerEvent,
        string? whenSql,
        string bodySql,
        long createdAtUtcTicks, bool enabled = true, long? executionOrder = null,
        SqlTriggerTiming timing = SqlTriggerTiming.After, SqlTriggerLevel level = SqlTriggerLevel.Row,
        string? oldTableName = null, string? newTableName = null,
        bool isConstraint = false, bool initiallyDeferred = false)
        => Create(
            name,
            tableName,
            triggerEvent,
            whenSql is null ? null : SqlParser.ParsePredicate(whenSql),
            whenSql,
            bodySql,
            SqlParser.ParseScript(bodySql),
            createdAtUtcTicks, timing, level, oldTableName, newTableName, isConstraint, initiallyDeferred)
            .WithLifecycle(enabled: enabled, order: executionOrder);

    private static TriggerDefinition Create(
        string name,
        string tableName,
        SqlTriggerEvent triggerEvent,
        SqlExpression? when,
        string? whenSql,
        string bodySql,
        IReadOnlyList<SqlStatement> statements,
        long createdAtUtcTicks,
        SqlTriggerTiming timing, SqlTriggerLevel level, string? oldTableName, string? newTableName,
        bool isConstraint, bool initiallyDeferred)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodySql);
        ArgumentNullException.ThrowIfNull(statements);
        if (createdAtUtcTicks < 0 || createdAtUtcTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(nameof(createdAtUtcTicks));

        if (!Enum.IsDefined(timing) || !Enum.IsDefined(level) || !Enum.IsDefined(triggerEvent))
            throw new ArgumentException("触发器时机、粒度或事件无效。");
        if (timing == SqlTriggerTiming.Before && (level != SqlTriggerLevel.Row || triggerEvent == SqlTriggerEvent.Delete))
            throw new ArgumentException("BEFORE 仅支持关系表 INSERT/UPDATE FOR EACH ROW。");
        if (isConstraint != initiallyDeferred)
            throw new ArgumentException("当前约束触发器必须使用 DEFERRABLE INITIALLY DEFERRED，普通触发器不可延迟。");
        if (isConstraint && (timing != SqlTriggerTiming.After || level != SqlTriggerLevel.Row))
            throw new ArgumentException("CONSTRAINT TRIGGER 仅支持 AFTER FOR EACH ROW。");
        if (level == SqlTriggerLevel.Row && (oldTableName is not null || newTableName is not null))
            throw new ArgumentException("transition tables 仅支持 AFTER FOR EACH STATEMENT。");
        if ((triggerEvent == SqlTriggerEvent.Insert && oldTableName is not null)
            || (triggerEvent == SqlTriggerEvent.Delete && newTableName is not null))
            throw new ArgumentException("INSERT 不提供 OLD TABLE，DELETE 不提供 NEW TABLE。");
        if (level == SqlTriggerLevel.Statement && when is not null)
            throw new ArgumentException("语句级触发器使用 body 查询过滤 transition tables，不支持行级 WHEN。");
        foreach (string? alias in new[] { oldTableName, newTableName })
            if (alias is not null && (string.IsNullOrWhiteSpace(alias)
                || string.Equals(alias, "OLD", StringComparison.OrdinalIgnoreCase)
                || string.Equals(alias, "NEW", StringComparison.OrdinalIgnoreCase)
                || string.Equals(alias, tableName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("transition table 别名不得为空、OLD、NEW 或目标表名。");
        if (oldTableName is not null && string.Equals(oldTableName, newTableName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("OLD TABLE 与 NEW TABLE 别名必须不同。");

        var analysis = SqlRoutineAnalyzer.AnalyzeTrigger(statements, when, triggerEvent, timing);
        if (level == SqlTriggerLevel.Statement && analysis.RowColumns.Count != 0)
            throw new ArgumentException("语句级触发器不能引用 OLD/NEW 行上下文。");
        return new TriggerDefinition(
            name,
            tableName,
            triggerEvent,
            when,
            whenSql,
            bodySql.Trim(),
            statements.ToArray(),
            analysis,
            createdAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : createdAtUtcTicks,
            timing, level, oldTableName, newTableName, isConstraint, initiallyDeferred);
    }
}
