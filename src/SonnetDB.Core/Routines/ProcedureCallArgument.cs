using SonnetDB.Sql.Ast;

namespace SonnetDB.Routines;

/// <summary>类型化过程调用的一个命名参数；不包含 ADO.NET 或传输层状态。</summary>
/// <param name="Name">参数名，允许一个 @ 或 : 前缀。</param>
/// <param name="Value">输入值；OUT 的输入值被忽略。</param>
/// <param name="Direction">与过程声明一致的参数方向。</param>
public sealed record ProcedureCallArgument(
    string Name,
    object? Value,
    SqlProcedureParameterDirection Direction = SqlProcedureParameterDirection.Input);
