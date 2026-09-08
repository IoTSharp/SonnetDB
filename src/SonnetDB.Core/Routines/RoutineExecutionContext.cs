using SonnetDB.Exceptions;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Routines;

internal sealed class RoutineExecutionContext
{
    private static readonly AsyncLocal<RoutineExecutionContext?> CurrentSlot = new();
    private readonly List<string> _procedureStack = [];
    private readonly List<string> _triggerStack = [];

    private RoutineExecutionContext(SqlExecutionOptions options)
    {
        options.Validate();
        Options = options;
    }

    public static RoutineExecutionContext? Current => CurrentSlot.Value;

    public SqlExecutionOptions Options { get; }

    public int StatementsExecuted { get; private set; }

    public int ResultRows { get; private set; }

    internal string[] ProcedureAncestry => _procedureStack.ToArray();
    internal string[] TriggerAncestry => _triggerStack.ToArray();

    internal static IDisposable EnterDeferred(RoutineExecutionContext origin, string[] procedures, string[] triggers)
    {
        var scope = new DeferredScope(CurrentSlot.Value, origin, origin.ProcedureAncestry, origin.TriggerAncestry);
        origin._procedureStack.Clear();
        origin._procedureStack.AddRange(procedures);
        origin._triggerStack.Clear();
        origin._triggerStack.AddRange(triggers);
        CurrentSlot.Value = origin;
        return scope;
    }

    public string CallChain
        => string.Join(
            " > ",
            _procedureStack.Select(static name => "procedure:" + name)
                .Concat(_triggerStack.Select(static name => "trigger:" + name)));

    public static RootScope EnterRoot(SqlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (CurrentSlot.Value is not null)
            return new RootScope(null);
        var context = new RoutineExecutionContext(options);
        CurrentSlot.Value = context;
        return new RootScope(context);
    }

    public StackScope EnterProcedure(string name)
    {
        CheckCancellation();
        if (_procedureStack.Contains(name, StringComparer.Ordinal))
        {
            throw new RoutineExecutionException(
                RoutineErrorCodes.RecursiveCall,
                $"拒绝过程递归调用：{string.Join(" -> ", _procedureStack.Append(name))}。");
        }
        EnsureDepth();
        _procedureStack.Add(name);
        return new StackScope(_procedureStack);
    }

    public StackScope EnterTrigger(string name)
    {
        CheckCancellation();
        if (_triggerStack.Contains(name, StringComparer.Ordinal))
        {
            throw new RoutineExecutionException(
                RoutineErrorCodes.TriggerRecursion,
                $"拒绝触发器递归调用：{string.Join(" -> ", _triggerStack.Append(name))}。");
        }
        EnsureDepth();
        _triggerStack.Add(name);
        return new StackScope(_triggerStack);
    }

    public void ConsumeStatement()
    {
        CheckCancellation();
        ConsumeStatementCore();
        if (SqlTransactionContext.Current is { IsExecutingDeferredTriggers: true, CommitContext: { } commit }
            && !ReferenceEquals(commit, this))
            commit.ConsumeStatementCore();
    }

    private void ConsumeStatementCore()
    {
        StatementsExecuted++;
        if (StatementsExecuted > Options.MaxRoutineStatements)
        {
            throw new RoutineExecutionException(
                RoutineErrorCodes.StatementLimit,
                $"例程调用链执行语句数超过上限 {Options.MaxRoutineStatements}。");
        }
    }

    public void AddResultRows(int count)
    {
        AddResultRowsCore(count);
        if (SqlTransactionContext.Current is { IsExecutingDeferredTriggers: true, CommitContext: { } commit }
            && !ReferenceEquals(commit, this))
            commit.AddResultRowsCore(count);
    }

    private void AddResultRowsCore(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        long total = (long)ResultRows + count;
        ResultRows = (int)Math.Min(int.MaxValue, total);
        if (total > Options.MaxRoutineResultRows)
        {
            throw new RoutineExecutionException(
                RoutineErrorCodes.ResultRowLimit,
                $"例程调用链结果行数超过上限 {Options.MaxRoutineResultRows}。");
        }
    }

    public void CheckCancellation()
    {
        SqlTransactionContext.Current?.CheckCommitDeadline();
        if (Options.CancellationToken.IsCancellationRequested)
        {
            throw new RoutineExecutionException(
                RoutineErrorCodes.Cancelled,
                "例程调用已取消。",
                new OperationCanceledException(Options.CancellationToken));
        }
    }

    private sealed class DeferredScope(RoutineExecutionContext? previous, RoutineExecutionContext origin,
        string[] procedures, string[] triggers) : IDisposable
    {
        public void Dispose()
        {
            origin._procedureStack.Clear();
            origin._procedureStack.AddRange(procedures);
            origin._triggerStack.Clear();
            origin._triggerStack.AddRange(triggers);
            CurrentSlot.Value = previous;
        }
    }

    private void EnsureDepth()
    {
        int limit = Math.Min(Options.MaxRoutineDepth,
            SqlTransactionContext.Current?.CommitContext?.Options.MaxRoutineDepth ?? int.MaxValue);
        if (_procedureStack.Count + _triggerStack.Count >= limit)
        {
            throw new RoutineExecutionException(
                RoutineErrorCodes.DepthLimit,
                $"例程调用链深度超过上限 {limit}。");
        }
    }

    public readonly struct RootScope : IDisposable
    {
        private readonly RoutineExecutionContext? _owned;

        internal RootScope(RoutineExecutionContext? owned)
            => _owned = owned;

        public void Dispose()
        {
            if (_owned is not null && ReferenceEquals(CurrentSlot.Value, _owned))
                CurrentSlot.Value = null;
        }
    }

    public readonly struct StackScope : IDisposable
    {
        private readonly List<string> _stack;

        internal StackScope(List<string> stack)
            => _stack = stack;

        public void Dispose()
        {
            if (_stack.Count != 0)
                _stack.RemoveAt(_stack.Count - 1);
        }
    }
}
