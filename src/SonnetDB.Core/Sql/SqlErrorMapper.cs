using SonnetDB.Exceptions;
using SonnetDB.Tables;

namespace SonnetDB.Sql;

/// <summary>
/// 把 SQL 解析和执行异常转换为稳定、传输无关的错误结果。
/// </summary>
public static class SqlErrorMapper
{
    /// <summary>
    /// 将异常映射为可由嵌入式、ADO.NET 和远程端点共享的 SQL 错误结果。
    /// </summary>
    /// <param name="exception">待映射异常。</param>
    /// <param name="operation">失败的 SQL 操作阶段或操作名。</param>
    /// <param name="fallbackHint">未提供专用提示时使用的安全提示。</param>
    /// <returns>稳定错误结果。</returns>
    public static SqlErrorInfo Map(
        Exception exception,
        string operation,
        string? fallbackHint = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        return exception switch
        {
            SqlParseException parse => new SqlErrorInfo(
                parse.Code,
                parse.Message,
                parse.Operation,
                parse.Position,
                parse.Hint ?? fallbackHint),
            TableConstraintException constraint => new SqlErrorInfo(
                constraint.ErrorCode,
                constraint.Message,
                operation,
                hint: constraint.Hint ?? fallbackHint),
            OperationCanceledException => new SqlErrorInfo(
                SqlErrorCodes.Cancelled,
                exception.Message,
                operation,
                hint: fallbackHint ?? "取消当前请求后重新执行，或增大客户端取消预算。"),
            TimeoutException => new SqlErrorInfo(
                SqlErrorCodes.Timeout,
                exception.Message,
                operation,
                hint: fallbackHint ?? "缩小查询范围或增加客户端超时预算后重试。"),
            RoutineExecutionException routine when routine.Code == RoutineErrorCodes.Cancelled
                && routine.InnerException is TimeoutException => new SqlErrorInfo(
                    SqlErrorCodes.Timeout,
                    routine.Message,
                    operation,
                    hint: fallbackHint ?? "缩小查询范围或增加客户端超时预算后重试。"),
            RoutineExecutionException routine when routine.Code == RoutineErrorCodes.Cancelled => new SqlErrorInfo(
                SqlErrorCodes.Cancelled,
                routine.Message,
                operation,
                hint: fallbackHint ?? "取消当前请求后重新执行，或增大客户端取消预算。"),
            RoutineExecutionException routine => new SqlErrorInfo(
                routine.Code,
                routine.Message,
                operation,
                hint: fallbackHint ?? "检查 SQL 操作和数据库状态后重试。"),
            _ => new SqlErrorInfo(
                SqlErrorCodes.Execution,
                exception.Message,
                operation,
                hint: fallbackHint ?? "检查 SQL 操作和数据库状态后重试。"),
        };
    }
}
