namespace SonnetDB.Sql.Ast;

/// <summary>创建单 schema、Int64、非循环的独立序列。</summary>
/// <param name="Name">保留原始拼写的名称。</param>
/// <param name="Start">第一次取号的值。</param>
/// <param name="Increment">非零步长。</param>
/// <param name="IfNotExists">存在时是否保持原定义。</param>
/// <param name="NameIsQuoted">名称是否使用双引号。</param>
public sealed record CreateSequenceStatement(string Name, long Start = 1, long Increment = 1,
    bool IfNotExists = false, bool NameIsQuoted = false) : SqlStatement;

/// <summary>删除独立序列；不撤销已经返回的号码。</summary>
/// <param name="Name">序列名称。</param>
/// <param name="IfExists">不存在时是否忽略。</param>
/// <param name="NameIsQuoted">名称是否使用双引号。</param>
public sealed record DropSequenceStatement(string Name, bool IfExists = false, bool NameIsQuoted = false) : SqlStatement;
