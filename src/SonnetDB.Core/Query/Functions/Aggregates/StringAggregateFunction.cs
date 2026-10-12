using SonnetDB.Catalog;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Query.Functions.Aggregates;

/// <summary>关系表的双参数字符串聚合；不声明 measurement 或窗口能力。</summary>
internal sealed class StringAggregateFunction(string name) : IAggregateFunction
{
    /// <summary>聚合函数名。</summary>
    public string Name { get; } = name;

    /// <summary>字符串聚合不使用数值聚合枚举。</summary>
    public Aggregator? LegacyAggregator => null;

    /// <summary>当前不接受 measurement FIELD 输入。</summary>
    public AggregateFieldTypes AcceptedFieldTypes => AggregateFieldTypes.None;

    /// <summary>明确拒绝尚未交付的 measurement 字符串聚合。</summary>
    /// <param name="call">聚合调用。</param>
    /// <param name="schema">measurement schema。</param>
    /// <returns>此入口始终抛出不支持异常。</returns>
    public string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        => throw new NotSupportedException($"{Name} 当前仅支持关系表聚合，不支持 measurement。");

    internal static bool IsBuiltIn(string functionName)
        => (string.Equals(functionName, "string_agg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(functionName, "group_concat", StringComparison.OrdinalIgnoreCase))
            && FunctionRegistry.GetFunctionKind(functionName) == FunctionKind.Aggregate
            && FunctionRegistry.TryGetAggregate(functionName, out var function)
            && function is StringAggregateFunction;

    internal static string ValidateCall(FunctionCallExpression call)
    {
        if (call.IsDistinct || call.Over is not null)
            throw new NotSupportedException($"{call.Name} 基础聚合不支持 DISTINCT 或 OVER。");
        if (call.IsStar || call.Arguments.Count != 2)
            throw new InvalidOperationException($"{call.Name}(value, delimiter) 需要 2 个参数。");
        return call.Arguments[1] switch
        {
            LiteralExpression { Kind: SqlLiteralKind.String } literal => literal.StringValue ?? string.Empty,
            LiteralExpression { Kind: SqlLiteralKind.Null } => string.Empty,
            _ => throw new InvalidOperationException($"{call.Name} 分隔符必须是 STRING/NULL 字面量或绑定参数。"),
        };
    }

    internal static object? Evaluate(
        FunctionCallExpression call,
        IReadOnlyList<object?[]> rows,
        Func<object?[], object?> evaluate,
        long fallbackLimitBytes)
    {
        string delimiter = ValidateCall(call);
        SqlQueryResources? resources = SqlQueryResources.Current;
        long limitBytes = resources?.Options.BlockingOperatorMemoryLimitBytes ?? fallbackLimitBytes;
        using var scratch = resources?.CreateReservation();
        List<string>? pieces = null;
        long scratchBytes = 0;
        long length = 0;
        for (int index = 0; index < rows.Count; index++)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            object? value = evaluate(rows[index]);
            SqlExecutor.ThrowIfCancellationRequested();
            if (value is null)
                continue;
            if (value is not string text)
                throw new InvalidOperationException($"{call.Name} 输入必须是 STRING 或 NULL；其它类型请显式 CAST。");

            long nextLength = checked(length + (pieces is null ? 0 : delimiter.Length) + text.Length);
            if (nextLength > int.MaxValue)
                throw new InvalidOperationException($"{call.Name} 结果超过字符串长度上限。");
            // 片段只求值一次。保守计入字符串、List 倍增期间的新旧引用数组和容器，先准入再 Add。
            long bytes = checked(56L + text.Length * 2L + (pieces is null ? 128 : 0));
            if (bytes > limitBytes - scratchBytes || scratch?.TryReserve(bytes) == false)
                throw MemoryLimit(call.Name);
            scratchBytes += bytes;
            pieces ??= new List<string>();
            pieces.Add(text);
            length = nextLength;
        }

        if (pieces is null)
            return null;

        long resultBytes = checked(24L + length * 2L);
        var retainedResult = resources?.CreateReservation();
        bool retained = false;
        try
        {
            if (resultBytes > limitBytes - scratchBytes
                || retainedResult?.TryReserve(resultBytes) == false)
                throw MemoryLimit(call.Name);
            string result = string.Create(checked((int)length), (Pieces: pieces, Delimiter: delimiter),
                static (destination, state) =>
                {
                    int offset = 0;
                    for (int index = 0; index < state.Pieces.Count; index++)
                    {
                        SqlExecutor.ThrowIfCancellationRequested();
                        if (index != 0)
                        {
                            state.Delimiter.AsSpan().CopyTo(destination[offset..]);
                            offset += state.Delimiter.Length;
                        }
                        string piece = state.Pieces[index];
                        piece.AsSpan().CopyTo(destination[offset..]);
                        offset += piece.Length;
                    }
                });
            // 成功结果继续占用根查询预算，避免不同分组/子查询逐一释放后绕过共享上限。
            // 根 SqlQueryResources.Dispose 归还这些保留字节；失败/取消则立即归还本次结果预留。
            retained = true;
            return result;
        }
        finally
        {
            if (!retained)
                retainedResult?.Dispose();
        }
    }

    private static InvalidOperationException MemoryLimit(string name)
        => new($"{name} 字符串聚合超过当前查询或数据库的内存预算。");
}
