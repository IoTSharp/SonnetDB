namespace SonnetDB.Sql.Ast;

/// <summary>设置关系表自增列的下一次自动分配值。</summary>
/// <param name="TableName">关系表名。</param>
/// <param name="NextValue">大于已分配高水位的下一值。</param>
public sealed record AlterTableAutoIncrementStatement(string TableName, long NextValue) : SqlStatement
{
    /// <summary>表名是否使用双引号。</summary>
    public bool TableNameIsQuoted { get; init; }
}
