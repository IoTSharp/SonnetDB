namespace SonnetDB.Exceptions;

/// <summary>过程或触发器执行违反稳定运行时合同时抛出的异常。</summary>
public sealed class RoutineExecutionException : Exception
{
    /// <summary>创建带稳定错误码的例程执行异常。</summary>
    /// <param name="code">稳定错误码。</param>
    /// <param name="message">错误消息。</param>
    /// <param name="innerException">可选内部异常。</param>
    public RoutineExecutionException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>可供嵌入式和远程调用方稳定判断的错误码。</summary>
    public string Code { get; }
}

/// <summary>过程与触发器稳定错误码。</summary>
public static class RoutineErrorCodes
{
    /// <summary>过程不存在。</summary>
    public const string ProcedureNotFound = "procedure_not_found";
    /// <summary>触发器不存在。</summary>
    public const string TriggerNotFound = "trigger_not_found";
    /// <summary>过程参数无效。</summary>
    public const string InvalidArguments = "routine_invalid_arguments";
    /// <summary>过程直接或间接递归。</summary>
    public const string RecursiveCall = "routine_recursive_call";
    /// <summary>触发器调用形成递归。</summary>
    public const string TriggerRecursion = "trigger_recursion";
    /// <summary>调用深度超过上限。</summary>
    public const string DepthLimit = "routine_depth_limit";
    /// <summary>执行语句数超过上限。</summary>
    public const string StatementLimit = "routine_statement_limit";
    /// <summary>结果行数超过上限。</summary>
    public const string ResultRowLimit = "routine_result_row_limit";
    /// <summary>调用被取消。</summary>
    public const string Cancelled = "routine_cancelled";
    /// <summary>调用方权限不足。</summary>
    public const string Forbidden = "routine_forbidden";
    /// <summary>依赖不存在或仍被引用。</summary>
    public const string Dependency = "routine_dependency";
    /// <summary>触发器 OLD/NEW 上下文无效。</summary>
    public const string TriggerContext = "trigger_context";
    /// <summary>body 内部语句执行失败。</summary>
    public const string ExecutionFailed = "routine_execution_failed";
    /// <summary>调用方显式回滚或放弃了包含例程动作的事务。</summary>
    public const string RolledBack = "routine_rolled_back";
    /// <summary>底层事务提交或回滚结果未知；须重启恢复后核对业务幂等键。</summary>
    public const string CommitUnknown = "routine_commit_unknown";
}
