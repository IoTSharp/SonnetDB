namespace SonnetDB.Tables;

using SonnetDB.Sql.Ast;

/// <summary>
/// 关系表外键声明。
/// </summary>
/// <param name="Name">外键名，在单表内唯一。</param>
/// <param name="Columns">本表外键列名，按声明顺序排列。</param>
/// <param name="PrincipalTable">被引用表名。</param>
/// <param name="PrincipalColumns">被引用列名，第一版要求等于被引用表主键。</param>
/// <param name="OnDelete">ON DELETE 动作；缺省 NoAction（拒绝）。</param>
public sealed record TableForeignKey(
    string Name,
    IReadOnlyList<string> Columns,
    string PrincipalTable,
    IReadOnlyList<string> PrincipalColumns,
    ForeignKeyAction OnDelete = ForeignKeyAction.NoAction);

/// <summary>
/// 创建或加载外键时使用的轻量声明。
/// </summary>
/// <param name="Name">外键名；为空时由 schema 自动生成。</param>
/// <param name="Columns">本表外键列名。</param>
/// <param name="PrincipalTable">被引用表名。</param>
/// <param name="PrincipalColumns">被引用列名。</param>
/// <param name="OnDelete">ON DELETE 动作；缺省 NoAction。</param>
public sealed record TableForeignKeyDefinition(
    string Name,
    IReadOnlyList<string> Columns,
    string PrincipalTable,
    IReadOnlyList<string> PrincipalColumns,
    ForeignKeyAction OnDelete = ForeignKeyAction.NoAction);
