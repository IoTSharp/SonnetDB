using SonnetDB.Tables;

namespace SonnetDB.Graphs;

/// <summary>关系映射图读取的显式 scan/result 预算。</summary>
public sealed record RelationalGraphAccessOptions
{
    /// <summary>pull cursor 每页最多返回的关系行数。</summary>
    public int PageSize { get; init; } = 256;

    /// <summary>允许 scan fallback 检查的最大关系行数。</summary>
    public int MaxScanRows { get; init; } = 10_000;

    /// <summary>单次 seek/expand 允许返回的最大行数。</summary>
    public int MaxResults { get; init; } = 10_000;

    /// <summary>允许一次 scan fallback 占用的最长墙钟时间。</summary>
    public TimeSpan MaxScanDuration { get; init; } = TimeSpan.FromMilliseconds(50);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageSize);
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

    /// <summary>创建固定所有映射表的 statement snapshot。</summary>
    internal RelationalGraphReadSession BeginRead()
        => new(_tables, Definition);

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

        using RelationalGraphReadSession session = BeginRead();
        using RelationalGraphCursor cursor = GraphPlanExecutor.Execute(
            session,
            new RelationalGraphNodePlan(mapping.TableName, Options: accessOptions));
        IReadOnlyList<TableRow> rows = ReadAll(cursor, cancellationToken);
        return new RelationalGraphReadResult(rows, cursor.AccessPlans, cursor.ExaminedRows);
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

        using RelationalGraphReadSession session = BeginRead();
        using RelationalGraphCursor cursor = GraphPlanExecutor.Execute(
            session,
            new RelationalGraphNodePlan(mapping.TableName, keyValues, accessOptions with { MaxResults = 1 }));
        IReadOnlyList<TableRow> rows = ReadAll(cursor, cancellationToken);
        return new RelationalGraphReadResult(rows, cursor.AccessPlans, cursor.ExaminedRows);
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

        using RelationalGraphReadSession session = BeginRead();
        using RelationalGraphCursor cursor = GraphPlanExecutor.Execute(
            session,
            new RelationalGraphExpandPlan(
                mapping.TableName,
                direction,
                endpointKeyValues,
                accessOptions));
        IReadOnlyList<TableRow> rows = ReadAll(cursor, cancellationToken);
        return new RelationalGraphReadResult(rows, cursor.AccessPlans, cursor.ExaminedRows);
    }

    private static IReadOnlyList<TableRow> ReadAll(
        RelationalGraphCursor cursor,
        CancellationToken cancellationToken)
    {
        var rows = new List<TableRow>();
        while (true)
        {
            IReadOnlyList<TableRow> page = cursor.ReadNextPage(cancellationToken);
            if (page.Count == 0)
                return rows;
            rows.AddRange(page);
        }
    }

    internal static RelationalGraphAccessPlan PlanKeyAccess(
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

    internal static RelationalGraphAccessPlan PlanEndpointAccess(
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

    internal static IReadOnlyList<GraphDirection> Directions(GraphDirection direction)
        => direction switch
        {
            GraphDirection.Outgoing => [GraphDirection.Outgoing],
            GraphDirection.Incoming => [GraphDirection.Incoming],
            GraphDirection.Both => [GraphDirection.Outgoing, GraphDirection.Incoming],
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };

    internal static bool ValuesEqual(object? left, object? right)
        => left is byte[] leftBytes && right is byte[] rightBytes
            ? leftBytes.AsSpan().SequenceEqual(rightBytes)
            : Equals(left, right);
}
