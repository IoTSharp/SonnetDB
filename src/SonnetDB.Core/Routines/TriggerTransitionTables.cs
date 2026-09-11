using SonnetDB.Exceptions;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Routines;

// Scope-local aliases never enter the catalog. Nested triggers hide the caller's aliases.
internal sealed class TriggerTransitionTables : IDisposable
{
    private static readonly AsyncLocal<TriggerTransitionTables?> CurrentSlot = new();
    private readonly TriggerTransitionTables? _previous;
    private readonly TriggerDefinition? _definition;
    private readonly TableSchema? _schema;
    private readonly IReadOnlyList<TableRowChange> _changes;

    public TriggerTransitionTables(TriggerDefinition? definition, TableSchema? schema, IReadOnlyList<TableRowChange> changes)
    {
        _definition = definition;
        _schema = schema;
        _changes = changes;
        _previous = CurrentSlot.Value;
        CurrentSlot.Value = this;
    }

    public static TableSchema? FindSchema(string name)
        => CurrentSlot.Value is { } current && current.IsAlias(name) ? current._schema : null;

    private bool IsAlias(string name)
        => string.Equals(name, _definition?.OldTableName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, _definition?.NewTableName, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<object?[]> Read(string name)
    {
        var current = CurrentSlot.Value;
        if (current is null || !current.IsAlias(name))
            throw new RoutineExecutionException(RoutineErrorCodes.TriggerContext, "transition table 不在当前触发器作用域中。");
        bool old = string.Equals(name, current._definition?.OldTableName, StringComparison.OrdinalIgnoreCase);
        foreach (var change in current._changes)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            var values = old ? change.OldValues : change.NewValues;
            if (values is not null)
                yield return values as object?[] ?? values.ToArray();
        }
    }

    public void Dispose() => CurrentSlot.Value = _previous;
}

// Mutations already own immutable row images; the capture retains references without cloning a set.
internal sealed class TriggerTransitionBudget : IDisposable
{
    private static readonly AsyncLocal<TriggerTransitionBudget?> ActiveSlot = new();
    private readonly TriggerTransitionBudget? _parent = ActiveSlot.Value;
    private readonly bool _enabled;
    private readonly SqlExecutionOptions _options;
    private long _rows;
    private long _bytes;

    public TriggerTransitionBudget(bool enabled)
    {
        _enabled = enabled;
        _options = RoutineExecutionContext.Current?.Options ?? SqlExecutionOptions.Default;
        _rows = _parent?._rows ?? 0;
        _bytes = _parent?._bytes ?? 0;
        ActiveSlot.Value = this;
    }

    public void Add(IReadOnlyList<object?>? oldValues, IReadOnlyList<object?>? newValues)
    {
        if (!_enabled) return;
        RoutineExecutionContext.Current?.CheckCancellation();
        long bytes = Estimate(oldValues) + Estimate(newValues) + 64;
        if (_rows >= _options.MaxTriggerTransitionRows || bytes > _options.MaxTriggerTransitionBytes - _bytes)
            throw new RoutineExecutionException(RoutineErrorCodes.TransitionLimit,
                "transition tables 超出调用链行数或字节预算，语句已拒绝。");
        _rows++;
        _bytes += bytes;
    }

    public void CheckRowCapacity(int count)
    {
        if (_enabled && count > _options.MaxTriggerTransitionRows - _rows)
            throw new RoutineExecutionException(RoutineErrorCodes.TransitionLimit, "transition tables 超出调用链行数预算。");
    }

    internal static long Estimate(IReadOnlyList<object?>? values)
    {
        if (values is null) return 0;
        long bytes = 24 + (long)values.Count * 32;
        foreach (var value in values)
            bytes += value switch { string text => (long)text.Length * 2 + 24, byte[] blob => blob.Length + 24L, _ => 0 };
        return bytes;
    }

    public void Dispose() => ActiveSlot.Value = _parent;
}
