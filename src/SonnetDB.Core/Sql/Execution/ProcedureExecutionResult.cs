using System.Collections.ObjectModel;

namespace SonnetDB.Sql.Execution;

/// <summary>一次成功过程调用的完成快照；失败或取消时不会返回该对象。</summary>
/// <remarks>调用方已有事务仍由调用方提交；本快照不证明该事务已持久提交或其延迟约束已通过。</remarks>
public sealed class ProcedureExecutionResult
{
    internal ProcedureExecutionResult(
        IEnumerable<SelectExecutionResult> results,
        IDictionary<string, object?> outputs,
        long returnValue,
        int recordsAffected,
        bool hasDml,
        object? lastResult)
    {
        Results = Array.AsReadOnly(results.ToArray());
        OutputParameters = new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(outputs, StringComparer.OrdinalIgnoreCase));
        ReturnValue = returnValue;
        RecordsAffected = recordsAffected;
        HasDml = hasDml;
        LastResult = lastResult;
    }

    /// <summary>按过程执行顺序保留的 SELECT 与 RETURNING 结果；嵌套过程结果在调用位置展开。</summary>
    public IReadOnlyList<SelectExecutionResult> Results { get; }

    /// <summary>以声明名称保存的 OUT 与 INOUT 值；SQL NULL 为 null。</summary>
    public IReadOnlyDictionary<string, object?> OutputParameters { get; }

    /// <summary>RETURN 的 Int64 状态；未执行 RETURN 时为 0。</summary>
    public long ReturnValue { get; }

    /// <summary>全部 DML 的累计影响行数；仅查询时为 -1，无 DML 且无结果时为 0。</summary>
    public int RecordsAffected { get; }

    internal object? LastResult { get; }

    internal bool HasDml { get; }
}
