using SonnetDB.Tables;
using System.Diagnostics;

namespace SonnetDB.Graphs;

/// <summary>关系映射图读取的显式 scan/result 预算。</summary>
public sealed record RelationalGraphAccessOptions
{
    /// <summary>允许 scan fallback 检查的最大关系行数。</summary>
    public int MaxScanRows { get; init; } = 10_000;

    /// <summary>单次 seek/expand 允许返回的最大行数。</summary>
    public int MaxResults { get; init; } = 10_000;

    /// <summary>允许一次 scan fallback 占用的最长墙钟时间。</summary>
    public TimeSpan MaxScanDuration { get; init; } = TimeSpan.FromMilliseconds(50);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxScanRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxResults);
        if (MaxScanDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxScanDuration));
    }
}

/// <summary>关系映射图的一步访问计划。</summary>
/// <param name="AccessPath"><c>relation_primary_key_seek</c>、<c>relation_index_seek</c> 或 <c>relation_scan_fallback</c>。</param>
/// <param name="IndexName">使用的二级索引名；其它路径为 <c>null</c>。</param>
/// <param name="Direction">边扩展方向；顶点 seek 为 <c>null</c>。</param>
public sealed record RelationalGraphAccessPlan(
    string AccessPath,
    string? IndexName,
    GraphDirection? Direction);

/// <summary>关系映射图读取结果及实际访问路径。</summary>
/// <param name="Rows">直接来自权威关系表的行，不是图副本。</param>
/// <param name="AccessPlans">本次读取执行的访问步骤。</param>
/// <param name="ExaminedRows">实际从关系存储读取并检查的候选行数。</param>
public sealed record RelationalGraphReadResult(
    IReadOnlyList<TableRow> Rows,
    IReadOnlyList<RelationalGraphAccessPlan> AccessPlans,
    int ExaminedRows);

/// <summary>
/// 在关系表主键/二级索引之上执行 property graph 点查和邻接扩展，不维护第二份图数据。
/// </summary>
public sealed class RelationalGraphAccessor
{
    private readonly TableManager _tables;

    internal RelationalGraphAccessor(TableManager tables, PropertyGraphDefinition definition)
    {
        _tables = tables;
        Definition = definition;
    }

    /// <summary>当前访问器绑定的映射定义。</summary>
    public PropertyGraphDefinition Definition { get; }

    /// <summary>解释顶点 key 的关系访问路径，不读取关系行。</summary>
    /// <param name="vertexTable">顶点表名称。</param>
    /// <returns>主键或唯一索引点查计划。</returns>
    public RelationalGraphAccessPlan ExplainVertexAccess(string vertexTable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexTable);
        PropertyGraphVertexTable mapping = Definition.TryGetVertexTable(vertexTable)
            ?? throw new InvalidOperationException(
                $"property graph '{Definition.Name}' 没有 vertex table '{vertexTable}'。");
        return PlanKeyAccess(_tables.Open(mapping.TableName).Schema, mapping.KeyColumns, direction: null);
    }

    /// <summary>在显式行数/时间预算内扫描一个顶点表。</summary>
    /// <param name="vertexTable">顶点表名称。</param>
    /// <param name="options">访问预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>直接来自关系表的顶点行与 scan fallback 诊断。</returns>
    public RelationalGraphReadResult ScanVertices(
        string vertexTable,
        RelationalGraphAccessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexTable);
        RelationalGraphAccessOptions accessOptions = options ?? new RelationalGraphAccessOptions();
        accessOptions.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        PropertyGraphVertexTable mapping = Definition.TryGetVertexTable(vertexTable)
            ?? throw new InvalidOperationException(
                $"property graph '{Definition.Name}' 没有 vertex table '{vertexTable}'。");

        long scanStarted = Stopwatch.GetTimestamp();
        int rowLimit = Math.Min(accessOptions.MaxScanRows, accessOptions.MaxResults);
        IReadOnlyList<TableRow> rows = _tables.Open(mapping.TableName)
            .Scan(IncrementUnlessMax(rowLimit));
        if (rows.Count > accessOptions.MaxScanRows)
        {
            throw new GraphTraversalLimitExceededException(
                $"Relational graph vertex scan 超过上限 {accessOptions.MaxScanRows} 行。");
        }
        if (rows.Count > accessOptions.MaxResults)
        {
            throw new GraphTraversalLimitExceededException(
                $"Relational graph vertex scan 结果超过上限 {accessOptions.MaxResults} 行。");
        }
        ThrowIfScanDurationExceeded(scanStarted, accessOptions.MaxScanDuration);
        cancellationToken.ThrowIfCancellationRequested();
        return new RelationalGraphReadResult(
            rows,
            [new RelationalGraphAccessPlan("relation_scan_fallback", null, null)],
            rows.Count);
    }

    /// <summary>按映射顶点 key 定位关系行。</summary>
    /// <param name="vertexTable">顶点表名称。</param>
    /// <param name="keyValues">与映射 KEY 顺序一致的值。</param>
    /// <param name="options">访问预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>零或一行及实际访问路径。</returns>
    public RelationalGraphReadResult SeekVertex(
        string vertexTable,
        IReadOnlyList<object?> keyValues,
        RelationalGraphAccessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexTable);
        ArgumentNullException.ThrowIfNull(keyValues);
        RelationalGraphAccessOptions accessOptions = options ?? new RelationalGraphAccessOptions();
        accessOptions.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        PropertyGraphVertexTable mapping = Definition.TryGetVertexTable(vertexTable)
            ?? throw new InvalidOperationException(
                $"property graph '{Definition.Name}' 没有 vertex table '{vertexTable}'。");
        if (keyValues.Count != mapping.KeyColumns.Count)
            throw new ArgumentException("vertex key 值数量与映射 KEY 列数量不一致。", nameof(keyValues));

        TableStore store = _tables.Open(mapping.TableName);
        TableSchema schema = store.Schema;
        RelationalGraphAccessPlan plan = PlanKeyAccess(schema, mapping.KeyColumns, direction: null);
        IReadOnlyList<TableRow> rows;
        if (plan.AccessPath == "relation_primary_key_seek")
        {
            TableRow? row = store.GetByPrimaryKey(keyValues);
            rows = row is null ? [] : [row];
        }
        else
        {
            TableIndex index = schema.TryGetIndex(plan.IndexName!)
                ?? throw new InvalidOperationException($"关系索引 '{plan.IndexName}' 不存在。");
            rows = store.GetByIndex(index, keyValues, limit: 1);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new RelationalGraphReadResult(rows, [plan], rows.Count);
    }

    /// <summary>解释边表在指定方向上的 endpoint 访问路径，不读取关系行。</summary>
    /// <param name="edgeTable">边表名称。</param>
    /// <param name="direction">扩展方向。</param>
    /// <returns>一个或两个可审计访问步骤。</returns>
    public IReadOnlyList<RelationalGraphAccessPlan> ExplainEdgeAccess(
        string edgeTable,
        GraphDirection direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeTable);
        PropertyGraphEdgeTable mapping = Definition.TryGetEdgeTable(edgeTable)
            ?? throw new InvalidOperationException(
                $"property graph '{Definition.Name}' 没有 edge table '{edgeTable}'。");
        TableSchema schema = _tables.Open(mapping.TableName).Schema;
        return Directions(direction)
            .Select(item => item == GraphDirection.Outgoing
                ? PlanEndpointAccess(schema, mapping.SourceColumns, item)
                : PlanEndpointAccess(schema, mapping.DestinationColumns, item))
            .ToArray();
    }

    /// <summary>按 source/destination key 扩展关系边表。</summary>
    /// <param name="edgeTable">边表名称。</param>
    /// <param name="direction">出边、入边或双向。</param>
    /// <param name="endpointKeyValues">与对应 vertex KEY 顺序一致的值。</param>
    /// <param name="options">访问预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中边行及每个方向的实际访问路径。</returns>
    public RelationalGraphReadResult ExpandEdges(
        string edgeTable,
        GraphDirection direction,
        IReadOnlyList<object?> endpointKeyValues,
        RelationalGraphAccessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeTable);
        ArgumentNullException.ThrowIfNull(endpointKeyValues);
        RelationalGraphAccessOptions accessOptions = options ?? new RelationalGraphAccessOptions();
        accessOptions.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        PropertyGraphEdgeTable mapping = Definition.TryGetEdgeTable(edgeTable)
            ?? throw new InvalidOperationException(
                $"property graph '{Definition.Name}' 没有 edge table '{edgeTable}'。");
        int expectedKeyCount = direction switch
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
        if (endpointKeyValues.Count != expectedKeyCount)
        {
            throw new ArgumentException(
                "endpoint key 值数量与所选方向的 KEY 列数量不一致。",
                nameof(endpointKeyValues));
        }

        TableStore store = _tables.Open(mapping.TableName);
        var rows = new List<TableRow>();
        var plans = new List<RelationalGraphAccessPlan>();
        var seenPrimaryKeys = new HashSet<string>(StringComparer.Ordinal);
        int examinedRows = 0;
        int remainingScanRows = accessOptions.MaxScanRows;
        long? scanStarted = null;
        foreach (GraphDirection item in Directions(direction))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> endpointColumns = item == GraphDirection.Outgoing
                ? mapping.SourceColumns
                : mapping.DestinationColumns;
            RelationalGraphAccessPlan plan = PlanEndpointAccess(store.Schema, endpointColumns, item);
            plans.Add(plan);
            if (plan.AccessPath == "relation_scan_fallback" && remainingScanRows <= 0)
            {
                throw new GraphTraversalLimitExceededException(
                    $"Relational graph scan fallback 超过总预算 {accessOptions.MaxScanRows} 行。");
            }
            if (plan.AccessPath == "relation_scan_fallback")
                scanStarted ??= Stopwatch.GetTimestamp();
            (IReadOnlyList<TableRow> Rows, int ExaminedRows) read = ReadCandidates(
                store,
                endpointColumns,
                endpointKeyValues,
                plan,
                plan.AccessPath == "relation_scan_fallback"
                    ? accessOptions with { MaxScanRows = Math.Max(1, remainingScanRows) }
                    : accessOptions,
                scanStarted,
                cancellationToken);
            examinedRows = checked(examinedRows + read.ExaminedRows);
            if (plan.AccessPath == "relation_scan_fallback")
                remainingScanRows = checked(remainingScanRows - read.ExaminedRows);
            foreach (TableRow row in read.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string identity = Convert.ToHexString(row.PrimaryKey.Span);
                if (seenPrimaryKeys.Add(identity))
                    rows.Add(row);
                if (rows.Count > accessOptions.MaxResults)
                {
                    throw new GraphTraversalLimitExceededException(
                        $"Relational graph expand 结果超过上限 {accessOptions.MaxResults}。");
                }
            }
        }
        return new RelationalGraphReadResult(rows, plans, examinedRows);
    }

    private static (IReadOnlyList<TableRow> Rows, int ExaminedRows) ReadCandidates(
        TableStore store,
        IReadOnlyList<string> endpointColumns,
        IReadOnlyList<object?> endpointKeyValues,
        RelationalGraphAccessPlan plan,
        RelationalGraphAccessOptions options,
        long? scanStarted,
        CancellationToken cancellationToken)
    {
        if (plan.AccessPath == "relation_primary_key_seek")
        {
            TableRow? row = store.GetByPrimaryKey(endpointKeyValues);
            return row is null ? ([], 0) : ([row], 1);
        }
        if (plan.AccessPath == "relation_index_seek")
        {
            TableIndex index = store.Schema.TryGetIndex(plan.IndexName!)
                ?? throw new InvalidOperationException($"关系索引 '{plan.IndexName}' 不存在。");
            IReadOnlyList<TableRow> rows = store.GetByIndexPrefix(
                index,
                endpointKeyValues,
                IncrementUnlessMax(options.MaxResults));
            return (rows, rows.Count);
        }

        long started = scanStarted ?? Stopwatch.GetTimestamp();
        IReadOnlyList<TableRow> scanned = store.Scan(IncrementUnlessMax(options.MaxScanRows));
        if (scanned.Count > options.MaxScanRows)
        {
            throw new GraphTraversalLimitExceededException(
                $"Relational graph scan fallback 超过上限 {options.MaxScanRows} 行。");
        }
        ThrowIfScanDurationExceeded(started, options.MaxScanDuration);
        int[] ordinals = endpointColumns.Select(column => store.Schema.TryGetColumn(column)!.Ordinal).ToArray();
        var matches = new List<TableRow>();
        foreach (TableRow row in scanned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfScanDurationExceeded(started, options.MaxScanDuration);
            bool match = true;
            for (int index = 0; index < ordinals.Length; index++)
            {
                if (!ValuesEqual(row.Values[ordinals[index]], endpointKeyValues[index]))
                {
                    match = false;
                    break;
                }
            }
            if (match)
                matches.Add(row);
        }
        return (matches, scanned.Count);
    }

    private static RelationalGraphAccessPlan PlanKeyAccess(
        TableSchema schema,
        IReadOnlyList<string> columns,
        GraphDirection? direction)
    {
        if (schema.PrimaryKey.SequenceEqual(columns, StringComparer.Ordinal))
            return new RelationalGraphAccessPlan("relation_primary_key_seek", null, direction);
        TableIndex index = schema.Indexes.First(candidate =>
            candidate.IsUnique && candidate.Columns.SequenceEqual(columns, StringComparer.Ordinal));
        return new RelationalGraphAccessPlan("relation_index_seek", index.Name, direction);
    }

    private static RelationalGraphAccessPlan PlanEndpointAccess(
        TableSchema schema,
        IReadOnlyList<string> columns,
        GraphDirection direction)
    {
        if (schema.PrimaryKey.SequenceEqual(columns, StringComparer.Ordinal))
            return new RelationalGraphAccessPlan("relation_primary_key_seek", null, direction);
        TableIndex? index = schema.Indexes
            .Where(candidate => HasPrefix(candidate.Columns, columns))
            .OrderBy(static candidate => candidate.Columns.Count)
            .ThenBy(static candidate => candidate.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return index is null
            ? new RelationalGraphAccessPlan("relation_scan_fallback", null, direction)
            : new RelationalGraphAccessPlan("relation_index_seek", index.Name, direction);
    }

    private static bool HasPrefix(IReadOnlyList<string> candidate, IReadOnlyList<string> prefix)
    {
        if (candidate.Count < prefix.Count)
            return false;
        for (int index = 0; index < prefix.Count; index++)
            if (!string.Equals(candidate[index], prefix[index], StringComparison.Ordinal))
                return false;
        return true;
    }

    private static IReadOnlyList<GraphDirection> Directions(GraphDirection direction)
        => direction switch
        {
            GraphDirection.Outgoing => [GraphDirection.Outgoing],
            GraphDirection.Incoming => [GraphDirection.Incoming],
            GraphDirection.Both => [GraphDirection.Outgoing, GraphDirection.Incoming],
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };

    private static bool ValuesEqual(object? left, object? right)
        => left is byte[] leftBytes && right is byte[] rightBytes
            ? leftBytes.AsSpan().SequenceEqual(rightBytes)
            : Equals(left, right);

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
