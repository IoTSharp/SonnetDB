namespace SonnetDB.Sql.Ast;

/// <summary>从只读查询的完整声明结果原子创建关系表或会话临时关系表。</summary>
/// <param name="Name">保留原始拼写的目标名称。</param>
/// <param name="Query">用于声明推断和数据物化的 SELECT。</param>
/// <param name="IsTemporary">是否创建当前连接会话的临时对象。</param>
/// <param name="IfNotExists">同名目标存在时是否不执行创建。</param>
public sealed record CreateTableAsStatement(
    string Name,
    SelectStatement Query,
    bool IsTemporary = false,
    bool IfNotExists = false) : SqlStatement
{
    /// <summary>保留逐段引号状态的目标关系表身份。</summary>
    public SqlQualifiedObjectName? QualifiedName { get; init; }

    /// <summary>目标名称是否使用双引号。</summary>
    public bool NameIsQuoted { get; init; }
}
