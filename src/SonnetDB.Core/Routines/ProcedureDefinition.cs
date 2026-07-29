using SonnetDB.Sql;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Routines;

/// <summary>持久化 SQL 过程的不可变定义。</summary>
public sealed class ProcedureDefinition
{
    private ProcedureDefinition(
        string name,
        IReadOnlyList<SqlProcedureParameter> parameters,
        string bodySql,
        IReadOnlyList<SqlStatement> statements,
        SqlRoutineAnalysis analysis,
        long createdAtUtcTicks)
    {
        Name = name;
        Parameters = parameters;
        BodySql = bodySql;
        Statements = statements;
        ObjectDependencies = analysis.ObjectDependencies;
        ProcedureDependencies = analysis.ProcedureDependencies;
        RequiresWrite = analysis.RequiresWrite;
        CreatedAtUtcTicks = createdAtUtcTicks;
    }

    /// <summary>过程名称。</summary>
    public string Name { get; }

    /// <summary>语言标识；首版固定为 <c>SQL</c>。</summary>
    public string Language => "SQL";

    /// <summary>按声明顺序排列的 IN 参数。</summary>
    public IReadOnlyList<SqlProcedureParameter> Parameters { get; }

    /// <summary>不含外围 BEGIN/END 的 SQL body。</summary>
    public string BodySql { get; }

    /// <summary>已经解析的 SQL body AST。</summary>
    public IReadOnlyList<SqlStatement> Statements { get; }

    /// <summary>直接引用的数据对象名称。</summary>
    public IReadOnlyList<string> ObjectDependencies { get; }

    /// <summary>直接调用的 SQL 过程名称。</summary>
    public IReadOnlyList<string> ProcedureDependencies { get; }

    /// <summary>body 是否包含关系表写语句。</summary>
    public bool RequiresWrite { get; }

    /// <summary>创建时间（UTC ticks）。</summary>
    public long CreatedAtUtcTicks { get; }

    /// <summary>从 CREATE PROCEDURE AST 创建并校验定义。</summary>
    /// <param name="statement">CREATE PROCEDURE AST。</param>
    /// <param name="createdAtUtcTicks">创建时间；0 表示当前 UTC。</param>
    /// <returns>不可变过程定义。</returns>
    public static ProcedureDefinition Create(
        CreateProcedureStatement statement,
        long createdAtUtcTicks = 0)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (!string.Equals(statement.Language, "SQL", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SQL 过程首版只支持 LANGUAGE SQL。", nameof(statement));
        return Create(
            statement.Name,
            statement.Parameters,
            statement.BodySql,
            statement.Body,
            createdAtUtcTicks);
    }

    internal static ProcedureDefinition Restore(
        string name,
        IReadOnlyList<SqlProcedureParameter> parameters,
        string bodySql,
        long createdAtUtcTicks)
        => Create(name, parameters, bodySql, SqlParser.ParseScript(bodySql), createdAtUtcTicks);

    private static ProcedureDefinition Create(
        string name,
        IReadOnlyList<SqlProcedureParameter> parameters,
        string bodySql,
        IReadOnlyList<SqlStatement> statements,
        long createdAtUtcTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodySql);
        ArgumentNullException.ThrowIfNull(statements);
        if (createdAtUtcTicks < 0 || createdAtUtcTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(nameof(createdAtUtcTicks));

        var analysis = SqlRoutineAnalyzer.AnalyzeProcedure(statements, parameters);
        return new ProcedureDefinition(
            name,
            parameters.ToArray(),
            bodySql.Trim(),
            statements.ToArray(),
            analysis,
            createdAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : createdAtUtcTicks);
    }
}
