using System.Collections.ObjectModel;
using System.Diagnostics;
using SonnetDB.Tables;

namespace SonnetDB.Graphs;

/// <summary>
/// 关系映射图的一次 statement snapshot。会话创建时固定定义涉及的全部关系表，
/// 后续 anchor、邻接扩展和目标点查只读取这些快照。
/// </summary>
internal sealed class RelationalGraphReadSession : IDisposable
{
    private readonly object _sync = new();
    private IReadOnlyDictionary<string, TableReadBinding>? _bindings;

    internal RelationalGraphReadSession(
        TableManager tables,
        PropertyGraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(tables);
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        string[] tableNames = definition.VertexTables
            .Select(static mapping => mapping.TableName)
            .Concat(definition.EdgeTables.Select(static mapping => mapping.TableName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyDictionary<string, TableReadBinding> bindings = tables.AcquireReadSnapshots(tableNames);
        _bindings = bindings;
        SnapshotSequences = new ReadOnlyDictionary<string, long>(bindings.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Snapshot.Snapshot.Sequence,
            StringComparer.Ordinal));
    }

    internal PropertyGraphDefinition Definition { get; }

    /// <summary>各映射表在同一 statement 捕获窗口取得的 KV 序列。</summary>
    internal IReadOnlyDictionary<string, long> SnapshotSequences { get; }

    internal RelationalGraphCursor OpenNodeCursor(RelationalGraphNodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.VertexTable);
        RelationalGraphAccessOptions options = plan.Options ?? new RelationalGraphAccessOptions();
        options.Validate();
        PropertyGraphVertexTable mapping = Definition.TryGetVertexTable(plan.VertexTable)
            ?? throw new InvalidOperationException(
                $"property graph '{Definition.Name}' 没有 vertex table '{plan.VertexTable}'。");
        TableReadBinding binding = GetBinding(mapping.TableName);
        var state = new RelationalGraphCursorState();

        if (plan.KeyValues is null)
        {
            var access = new RelationalGraphAccessPlan("relation_scan_fallback", null, null);
            return new RelationalGraphCursor(
                ScanRows(binding, options, state),
                [access],
                options,
                state);
        }
        if (plan.KeyValues.Count != mapping.KeyColumns.Count)
            throw new ArgumentException("vertex key 值数量与映射 KEY 列数量不一致。", nameof(plan));

        RelationalGraphAccessPlan seek = RelationalGraphAccessor.PlanKeyAccess(
            binding.Snapshot.Schema,
            mapping.KeyColumns,
            direction: null);
        return new RelationalGraphCursor(
            SeekRows(binding, seek, plan.KeyValues, options, state),
            [seek],
            options,
            state);
    }

    internal RelationalGraphCursor OpenExpandCursor(RelationalGraphExpandPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.EdgeTable);
        ArgumentNullException.ThrowIfNull(plan.EndpointKeyValues);
        RelationalGraphAccessOptions options = plan.Options ?? new RelationalGraphAccessOptions();
        options.Validate();
        PropertyGraphEdgeTable mapping = Definition.TryGetEdgeTable(plan.EdgeTable)
            ?? throw new InvalidOperationException(
                $"property graph '{Definition.Name}' 没有 edge table '{plan.EdgeTable}'。");
        ValidateEndpointKeyCount(mapping, plan.Direction, plan.EndpointKeyValues.Count);
        TableReadBinding binding = GetBinding(mapping.TableName);
        RelationalGraphAccessPlan[] accessPlans = RelationalGraphAccessor.Directions(plan.Direction)
            .Select(direction => direction == GraphDirection.Outgoing
                ? RelationalGraphAccessor.PlanEndpointAccess(
                    binding.Snapshot.Schema,
                    mapping.SourceColumns,
                    direction)
                : RelationalGraphAccessor.PlanEndpointAccess(
                    binding.Snapshot.Schema,
                    mapping.DestinationColumns,
                    direction))
            .ToArray();
        var state = new RelationalGraphCursorState();
        return new RelationalGraphCursor(
            ExpandRows(binding, mapping, plan.EndpointKeyValues, accessPlans, options, state),
            accessPlans,
            options,
            state);
    }

    internal TableSchema GetSchema(string tableName)
        => GetBinding(tableName).Snapshot.Schema;

    public void Dispose()
    {
        IReadOnlyDictionary<string, TableReadBinding>? bindings;
        lock (_sync)
        {
            bindings = _bindings;
            _bindings = null;
        }
        if (bindings is null)
            return;
        foreach (TableReadBinding binding in bindings.Values)
            binding.Snapshot.Dispose();
    }

    private TableReadBinding GetBinding(string tableName)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_bindings is null, this);
            return _bindings.TryGetValue(tableName, out TableReadBinding? binding)
                ? binding
                : throw new InvalidOperationException(
                    $"property graph '{Definition.Name}' 的 statement snapshot 不含 table '{tableName}'。");
        }
    }

    private static IEnumerable<TableRow> ScanRows(
        TableReadBinding binding,
        RelationalGraphAccessOptions options,
        RelationalGraphCursorState state)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            foreach (TableRow row in binding.Store.EnumerateScan(
                binding.Snapshot.Snapshot,
                binding.Snapshot.Schema,
                IncrementUnlessMax(options.MaxScanRows)))
            {
                state.ExaminedRows = checked(state.ExaminedRows + 1);
                if (state.ExaminedRows > options.MaxScanRows)
                {
                    throw new GraphTraversalLimitExceededException(
                        $"Relational graph vertex scan 超过上限 {options.MaxScanRows} 行。");
                }
                ThrowIfScanDurationExceeded(started, options.MaxScanDuration);
                yield return row;
            }
        }
        finally
        {
            state.FallbackDuration += Stopwatch.GetElapsedTime(started);
        }
    }

    private static IEnumerable<TableRow> SeekRows(
        TableReadBinding binding,
        RelationalGraphAccessPlan plan,
        IReadOnlyList<object?> keyValues,
        RelationalGraphAccessOptions options,
        RelationalGraphCursorState state)
    {
        if (plan.AccessPath == "relation_primary_key_seek")
        {
            TableRow? row = binding.Store.GetByPrimaryKey(
                binding.Snapshot.Snapshot,
                binding.Snapshot.Schema,
                keyValues);
            if (row is not null)
            {
                state.ExaminedRows++;
                yield return row;
            }
            yield break;
        }

        TableIndex index = binding.Snapshot.Schema.TryGetIndex(plan.IndexName!)
            ?? throw new InvalidOperationException($"关系索引 '{plan.IndexName}' 不存在。");
        foreach (TableRow row in binding.Store.EnumerateByIndexPrefix(
            binding.Snapshot.Snapshot,
            binding.Snapshot.Schema,
            index,
            keyValues,
            IncrementUnlessMax(options.MaxResults)))
        {
            state.ExaminedRows = checked(state.ExaminedRows + 1);
            yield return row;
        }
    }

    private static IEnumerable<TableRow> ExpandRows(
        TableReadBinding binding,
        PropertyGraphEdgeTable mapping,
        IReadOnlyList<object?> endpointKeyValues,
        IReadOnlyList<RelationalGraphAccessPlan> plans,
        RelationalGraphAccessOptions options,
        RelationalGraphCursorState state)
    {
        HashSet<string>? seen = plans.Count > 1
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        long? scanStarted = null;
        try
        {
            foreach (RelationalGraphAccessPlan plan in plans)
            {
                IReadOnlyList<string> endpointColumns = plan.Direction == GraphDirection.Outgoing
                    ? mapping.SourceColumns
                    : mapping.DestinationColumns;
                IEnumerable<TableRow> candidates;
                if (plan.AccessPath is "relation_primary_key_seek" or "relation_index_seek")
                {
                    candidates = SeekRows(binding, plan, endpointKeyValues, options, state);
                }
                else
                {
                    scanStarted ??= Stopwatch.GetTimestamp();
                    candidates = ScanFallbackRows(
                        binding,
                        endpointColumns,
                        endpointKeyValues,
                        options,
                        state,
                        scanStarted.Value);
                }

                foreach (TableRow row in candidates)
                {
                    if (seen is null || seen.Add(Convert.ToHexString(row.PrimaryKey.Span)))
                        yield return row;
                }
            }
        }
        finally
        {
            if (scanStarted is { } started)
                state.FallbackDuration += Stopwatch.GetElapsedTime(started);
        }
    }

    private static IEnumerable<TableRow> ScanFallbackRows(
        TableReadBinding binding,
        IReadOnlyList<string> endpointColumns,
        IReadOnlyList<object?> endpointKeyValues,
        RelationalGraphAccessOptions options,
        RelationalGraphCursorState state,
        long started)
    {
        int[] ordinals = endpointColumns
            .Select(column => binding.Snapshot.Schema.TryGetColumn(column)!.Ordinal)
            .ToArray();
        int remaining = options.MaxScanRows - state.ExaminedRows;
        if (remaining <= 0)
        {
            throw new GraphTraversalLimitExceededException(
                $"Relational graph scan fallback 超过总预算 {options.MaxScanRows} 行。");
        }

        foreach (TableRow row in binding.Store.EnumerateScan(
            binding.Snapshot.Snapshot,
            binding.Snapshot.Schema,
            IncrementUnlessMax(remaining)))
        {
            state.ExaminedRows = checked(state.ExaminedRows + 1);
            if (state.ExaminedRows > options.MaxScanRows)
            {
                throw new GraphTraversalLimitExceededException(
                    $"Relational graph scan fallback 超过上限 {options.MaxScanRows} 行。");
            }
            ThrowIfScanDurationExceeded(started, options.MaxScanDuration);
            bool matches = true;
            for (int index = 0; index < ordinals.Length; index++)
            {
                if (!RelationalGraphAccessor.ValuesEqual(
                    row.Values[ordinals[index]],
                    endpointKeyValues[index]))
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
                yield return row;
        }
    }

    private static void ValidateEndpointKeyCount(
        PropertyGraphEdgeTable mapping,
        GraphDirection direction,
        int count)
    {
        int expected = direction switch
        {
            GraphDirection.Outgoing => mapping.SourceColumns.Count,
            GraphDirection.Incoming => mapping.DestinationColumns.Count,
            GraphDirection.Both when mapping.SourceColumns.Count == mapping.DestinationColumns.Count =>
                mapping.SourceColumns.Count,
            GraphDirection.Both => throw new ArgumentException(
                "source/destination KEY 列数量不同时，双向扩展必须拆成两次单向调用。",
                nameof(direction)),
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
        if (count != expected)
            throw new ArgumentException("endpoint key 值数量与所选方向的 KEY 列数量不一致。", nameof(count));
    }

    private static int IncrementUnlessMax(int value)
        => value == int.MaxValue ? int.MaxValue : value + 1;

    private static void ThrowIfScanDurationExceeded(long started, TimeSpan maximum)
    {
        if (Stopwatch.GetElapsedTime(started) > maximum)
        {
            throw new GraphTraversalLimitExceededException(
                $"Relational graph scan fallback 超过时间上限 {maximum.TotalMilliseconds:F0} ms。");
        }
    }
}

/// <summary>关系映射 logical plan 的有界分页 pull cursor。</summary>
internal sealed class RelationalGraphCursor : IGraphPullCursor<TableRow>
{
    private readonly object _sync = new();
    private readonly int _pageSize;
    private readonly int _maximumResults;
    private readonly RelationalGraphCursorState _state;
    private IEnumerator<TableRow>? _enumerator;
    private int _returned;
    private int _readInProgress;
    private bool _exhausted;
    private bool _faulted;

    internal RelationalGraphCursor(
        IEnumerable<TableRow> rows,
        IReadOnlyList<RelationalGraphAccessPlan> accessPlans,
        RelationalGraphAccessOptions options,
        RelationalGraphCursorState state)
    {
        ArgumentNullException.ThrowIfNull(rows);
        AccessPlans = accessPlans ?? throw new ArgumentNullException(nameof(accessPlans));
        ArgumentNullException.ThrowIfNull(options);
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _pageSize = options.PageSize;
        _maximumResults = options.MaxResults;
        _enumerator = rows.GetEnumerator();
    }

    internal IReadOnlyList<RelationalGraphAccessPlan> AccessPlans { get; }

    internal int ExaminedRows => _state.ExaminedRows;

    internal TimeSpan FallbackDuration => _state.FallbackDuration;

    public bool IsExhausted
    {
        get
        {
            lock (_sync)
                return _exhausted;
        }
    }

    public IReadOnlyList<TableRow> ReadNextPage(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _readInProgress, 1) != 0)
            throw new InvalidOperationException("同一个 relational Graph cursor 不能并发读取。");
        try
        {
            lock (_sync)
            {
                if (_exhausted)
                    return Array.Empty<TableRow>();
                if (_faulted)
                    throw new InvalidOperationException("Relational Graph cursor 已因读取故障终止。");
                ObjectDisposedException.ThrowIf(_enumerator is null, this);
                try
                {
                    var page = new List<TableRow>(_pageSize);
                    while (page.Count < _pageSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_returned >= _maximumResults)
                        {
                            if (_enumerator.MoveNext())
                            {
                                throw new GraphTraversalLimitExceededException(
                                    $"Relational graph 结果超过上限 {_maximumResults} 行。");
                            }
                            ExhaustLocked();
                            break;
                        }
                        if (!_enumerator.MoveNext())
                        {
                            ExhaustLocked();
                            break;
                        }
                        page.Add(_enumerator.Current);
                        _returned++;
                    }
                    return page;
                }
                catch
                {
                    _faulted = true;
                    ReleaseEnumeratorLocked();
                    throw;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _readInProgress, 0);
        }
    }

    public void Dispose()
    {
        lock (_sync)
            ReleaseEnumeratorLocked();
    }

    private void ExhaustLocked()
    {
        _exhausted = true;
        ReleaseEnumeratorLocked();
    }

    private void ReleaseEnumeratorLocked()
    {
        IEnumerator<TableRow>? enumerator = _enumerator;
        _enumerator = null;
        enumerator?.Dispose();
    }
}

internal sealed class RelationalGraphCursorState
{
    internal int ExaminedRows { get; set; }

    internal TimeSpan FallbackDuration { get; set; }
}
