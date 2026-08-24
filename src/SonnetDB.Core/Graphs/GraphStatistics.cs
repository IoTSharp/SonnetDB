using System.IO.Hashing;
using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;
using SonnetDB.Storage.Codecs;

namespace SonnetDB.Graphs;

/// <summary>Graph 统计刷新的扫描和内存预算。</summary>
public sealed record GraphStatisticsRefreshOptions
{
    /// <summary>每页最多扫描的 KV 条目数。</summary>
    public int PageSize { get; init; } = 512;

    /// <summary>每页最多复制的 key/value payload 字节数。</summary>
    public int MaxPageBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>允许扫描的最大 KV 条目数。</summary>
    public long MaxScannedEntries { get; init; } = 250_000_000;

    /// <summary>label、degree、property 和 value 统计组的合计上限。</summary>
    public int MaxStatisticGroups { get; init; } = 1_000_000;

    internal void Validate()
    {
        if (PageSize is <= 0 or > 4_096)
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (MaxPageBytes is <= 0 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxPageBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxScannedEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxStatisticGroups);
    }
}

/// <summary>Graph 统计刷新超过显式扫描或分组预算。</summary>
public sealed class GraphStatisticsLimitExceededException : InvalidOperationException
{
    internal GraphStatisticsLimitExceededException(string message) : base(message) { }
}

/// <summary>图统计刷新结果。</summary>
public sealed class GraphStatistics
{
    internal GraphStatistics(
        long sequence,
        long vertexCount,
        long edgeCount,
        IReadOnlyDictionary<LabelId, long> labelCardinality,
        IReadOnlyDictionary<int, long> degreeHistogram,
        IReadOnlyDictionary<GraphIndexStatisticKey, long> propertyIndexCardinality,
        IReadOnlyDictionary<GraphValueStatisticKey, long> valueCardinality)
    {
        Sequence = sequence;
        VertexCount = vertexCount;
        EdgeCount = edgeCount;
        LabelCardinality = labelCardinality;
        DegreeHistogram = degreeHistogram;
        PropertyIndexCardinality = propertyIndexCardinality;
        ValueCardinality = valueCardinality;
    }

    /// <summary>统计对应的稳定读序列号。</summary>
    public long Sequence { get; }

    /// <summary>顶点数量。</summary>
    public long VertexCount { get; }

    /// <summary>边数量。</summary>
    public long EdgeCount { get; }

    /// <summary>每个标签下的元素数量。</summary>
    public IReadOnlyDictionary<LabelId, long> LabelCardinality { get; }

    /// <summary>出度直方图，key 为 degree、value 为顶点数量。</summary>
    public IReadOnlyDictionary<int, long> DegreeHistogram { get; }

    /// <summary>label/property/value 索引命中数量。</summary>
    public IReadOnlyDictionary<GraphIndexStatisticKey, long> PropertyIndexCardinality { get; }

    /// <summary>不保留原始属性值的 label/property/value fingerprint 精确命中数量。</summary>
    public IReadOnlyDictionary<GraphValueStatisticKey, long> ValueCardinality { get; }

    /// <summary>返回指定索引计数相对于对应元素数量的选择率。</summary>
    /// <param name="key">索引统计键。</param>
    /// <returns>0 到 1 之间的选择率；统计缺失时返回 null。</returns>
    public double? Selectivity(GraphIndexStatisticKey key)
    {
        if (!Enum.IsDefined(key.ElementKind)
            || key.LabelId.Value <= 0
            || key.PropertyId <= 0
            || !Enum.IsDefined(key.ValueKind))
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }
        if (!PropertyIndexCardinality.TryGetValue(key, out long count))
            return null;
        long denominator = key.ElementKind == GraphElementType.Vertex ? VertexCount : EdgeCount;
        return denominator == 0 ? 0 : (double)count / denominator;
    }

    /// <summary>返回指定精确 value seek 的估计行数。</summary>
    /// <param name="elementKind">顶点或边。</param>
    /// <param name="labelId">标签标识符。</param>
    /// <param name="propertyId">属性标识符。</param>
    /// <param name="value">精确匹配值。</param>
    /// <returns>统计中存在该值时返回命中数，否则返回 0。</returns>
    public long EstimateSeekRows(
        GraphElementType elementKind,
        LabelId labelId,
        int propertyId,
        GraphPropertyValue value)
        => ValueCardinality.GetValueOrDefault(GraphValueStatisticKey.Create(
            elementKind,
            labelId,
            propertyId,
            value));
}

/// <summary>图属性索引统计的有界标识；不包含原始属性值。</summary>
/// <param name="elementKind">顶点或边。</param>
/// <param name="labelId">标签标识符。</param>
/// <param name="propertyId">属性标识符。</param>
/// <param name="valueKind">属性值类型。</param>
public readonly record struct GraphIndexStatisticKey
{
    /// <summary>创建经过校验的图属性索引统计键。</summary>
    /// <param name="elementKind">顶点或边。</param>
    /// <param name="labelId">标签标识符。</param>
    /// <param name="propertyId">属性标识符。</param>
    /// <param name="valueKind">属性值类型。</param>
    public GraphIndexStatisticKey(
        GraphElementType elementKind,
        LabelId labelId,
        int propertyId,
        GraphPropertyKind valueKind)
    {
        if (!Enum.IsDefined(elementKind))
            throw new ArgumentOutOfRangeException(nameof(elementKind));
        if (labelId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(labelId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(propertyId);
        if (!Enum.IsDefined(valueKind))
            throw new ArgumentOutOfRangeException(nameof(valueKind));
        ElementKind = elementKind;
        LabelId = labelId;
        PropertyId = propertyId;
        ValueKind = valueKind;
    }

    /// <summary>顶点或边。</summary>
    public GraphElementType ElementKind { get; }

    /// <summary>标签标识符。</summary>
    public LabelId LabelId { get; }

    /// <summary>属性标识符。</summary>
    public int PropertyId { get; }

    /// <summary>属性值类型。</summary>
    public GraphPropertyKind ValueKind { get; }
}

/// <summary>不暴露原始属性值的精确索引统计键。</summary>
public readonly record struct GraphValueStatisticKey
{
    private GraphValueStatisticKey(
        GraphElementType elementKind,
        LabelId labelId,
        int propertyId,
        GraphPropertyKind valueKind,
        ulong valueFingerprint)
    {
        ElementKind = elementKind;
        LabelId = labelId;
        PropertyId = propertyId;
        ValueKind = valueKind;
        ValueFingerprint = valueFingerprint;
    }

    /// <summary>顶点或边。</summary>
    public GraphElementType ElementKind { get; }

    /// <summary>标签标识符。</summary>
    public LabelId LabelId { get; }

    /// <summary>属性标识符。</summary>
    public int PropertyId { get; }

    /// <summary>属性值类型。</summary>
    public GraphPropertyKind ValueKind { get; }

    /// <summary>sortable scalar bytes 的稳定 64 位 fingerprint。</summary>
    public ulong ValueFingerprint { get; }

    /// <summary>从精确属性值创建不保留原值的统计键。</summary>
    /// <param name="elementKind">顶点或边。</param>
    /// <param name="labelId">标签标识符。</param>
    /// <param name="propertyId">属性标识符。</param>
    /// <param name="value">属性值。</param>
    /// <returns>可用于统计查找的 fingerprint 键。</returns>
    public static GraphValueStatisticKey Create(
        GraphElementType elementKind,
        LabelId labelId,
        int propertyId,
        GraphPropertyValue value)
    {
        if (!Enum.IsDefined(elementKind))
            throw new ArgumentOutOfRangeException(nameof(elementKind));
        if (labelId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(labelId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(propertyId);
        byte[] encoded = SortableScalarCodec.EncodeGraph(value);
        return new GraphValueStatisticKey(
            elementKind,
            labelId,
            propertyId,
            value.Kind,
            XxHash64.HashToUInt64(encoded));
    }
}

/// <summary>Graph EXPLAIN 的访问路径。</summary>
public enum GraphAccessPath : byte
{
    /// <summary>原生双向邻接前缀读取。</summary>
    NativeAdjacency = 1,

    /// <summary>原生 label/property 索引 seek。</summary>
    NativeIndexSeek = 2,

    /// <summary>统计缺失时的显式范围扫描回退。</summary>
    NativeScanFallback = 3,
}

/// <summary>基础 Graph EXPLAIN 结果。</summary>
public sealed record GraphExplain
{
    /// <summary>逻辑操作名称。</summary>
    public required string Operation { get; init; }

    /// <summary>实际选择的访问路径。</summary>
    public required GraphAccessPath AccessPath { get; init; }

    /// <summary>统计估算的返回行数；未知时为 null。</summary>
    public long? EstimatedRows { get; init; }

    /// <summary>是否直接使用原生邻接或索引。</summary>
    public bool IsNative { get; init; }

    /// <summary>回退原因；原生路径时为 null。</summary>
    public string? FallbackReason { get; init; }

    /// <summary>计划使用的快照序列号。</summary>
    public long SnapshotSequence { get; init; }

    /// <summary>估算来源，例如 refreshed、stale 或 statistics_missing。</summary>
    public string EstimateSource { get; init; } = "statistics_missing";

    /// <summary>用于估算的统计序列号；未提供统计时为 null。</summary>
    public long? StatisticsSequence { get; init; }
}

internal static class GraphStatisticsCalculator
{
    internal static GraphStatistics Refresh(KvReadSnapshot snapshot, CancellationToken cancellationToken)
        => Refresh(snapshot, new GraphStatisticsRefreshOptions(), cancellationToken);

    internal static GraphStatistics Refresh(
        KvReadSnapshot snapshot,
        GraphStatisticsRefreshOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        long vertexCount = 0;
        long edgeCount = 0;
        long scannedEntries = 0;
        long verticesWithOutgoing = 0;
        GraphElementId currentSource = default;
        int currentDegree = 0;
        var labels = new Dictionary<LabelId, long>();
        var histogram = new Dictionary<int, long>();
        var properties = new Dictionary<GraphIndexStatisticKey, long>();
        var values = new Dictionary<GraphValueStatisticKey, long>();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            PageSize = options.PageSize,
            MaxPageBytes = options.MaxPageBytes,
        });
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);
            if (page.Count == 0)
                break;
            foreach (KvEntry entry in page)
            {
                if (scannedEntries >= options.MaxScannedEntries)
                {
                    throw new GraphStatisticsLimitExceededException(
                        $"Graph statistics 扫描超过 MaxScannedEntries={options.MaxScannedEntries}。");
                }
                scannedEntries++;
                GraphStorageKey key = GraphKeyCodec.Decode(entry.Key.Span);
                switch (key.Kind)
                {
                    case GraphKeyKind.VertexRecord:
                        {
                            vertexCount++;
                            GraphVertexRecord record = GraphElementRecordCodec.DecodeVertex(entry.Value.Span);
                            foreach (LabelId label in record.Labels)
                                IncrementBounded(labels, label, options, labels, histogram, properties, values);
                            break;
                        }
                    case GraphKeyKind.EdgeRecord:
                        {
                            edgeCount++;
                            GraphEdgeRecord record = GraphElementRecordCodec.DecodeEdge(entry.Value.Span);
                            IncrementBounded(labels, record.LabelId, options, labels, histogram, properties, values);
                            break;
                        }
                    case GraphKeyKind.OutgoingAdjacency:
                        if (currentSource != key.SourceId)
                        {
                            FlushDegree(
                                currentSource,
                                currentDegree,
                                histogram,
                                options,
                                labels,
                                properties,
                                values,
                                ref verticesWithOutgoing);
                            currentSource = key.SourceId;
                            currentDegree = 0;
                        }
                        currentDegree = checked(currentDegree + 1);
                        break;
                    case GraphKeyKind.VertexPropertyIndex:
                        {
                            IncrementBounded(
                                properties,
                                new GraphIndexStatisticKey(
                                    GraphElementType.Vertex,
                                    key.LabelId,
                                    key.PropertyId,
                                    key.PropertyValue.Kind),
                                options,
                                labels,
                                histogram,
                                properties,
                                values);
                            IncrementBounded(
                                values,
                                GraphValueStatisticKey.Create(
                                    GraphElementType.Vertex,
                                    key.LabelId,
                                    key.PropertyId,
                                    key.PropertyValue),
                                options,
                                labels,
                                histogram,
                                properties,
                                values);
                            break;
                        }
                    case GraphKeyKind.EdgePropertyIndex:
                        {
                            IncrementBounded(
                                properties,
                                new GraphIndexStatisticKey(
                                    GraphElementType.Edge,
                                    key.LabelId,
                                    key.PropertyId,
                                    key.PropertyValue.Kind),
                                options,
                                labels,
                                histogram,
                                properties,
                                values);
                            IncrementBounded(
                                values,
                                GraphValueStatisticKey.Create(
                                    GraphElementType.Edge,
                                    key.LabelId,
                                    key.PropertyId,
                                    key.PropertyValue),
                                options,
                                labels,
                                histogram,
                                properties,
                                values);
                            break;
                        }
                }
            }
        }

        FlushDegree(
            currentSource,
            currentDegree,
            histogram,
            options,
            labels,
            properties,
            values,
            ref verticesWithOutgoing);
        long zeroDegreeVertices = vertexCount - verticesWithOutgoing;
        if (zeroDegreeVertices < 0)
            throw new InvalidDataException("Graph outgoing adjacency 引用了不存在的 vertex，不能刷新统计。");
        if (zeroDegreeVertices > 0)
            AddBounded(histogram, 0, zeroDegreeVertices, options, labels, histogram, properties, values);
        return new GraphStatistics(
            snapshot.Sequence,
            vertexCount,
            edgeCount,
            new Dictionary<LabelId, long>(labels),
            histogram,
            new Dictionary<GraphIndexStatisticKey, long>(properties),
            new Dictionary<GraphValueStatisticKey, long>(values));
    }

    private static void FlushDegree(
        GraphElementId source,
        int degree,
        Dictionary<int, long> histogram,
        GraphStatisticsRefreshOptions options,
        Dictionary<LabelId, long> labels,
        Dictionary<GraphIndexStatisticKey, long> properties,
        Dictionary<GraphValueStatisticKey, long> values,
        ref long verticesWithOutgoing)
    {
        if (source.Value <= 0)
            return;
        AddBounded(histogram, degree, 1, options, labels, histogram, properties, values);
        verticesWithOutgoing++;
    }

    private static void IncrementBounded<TKey>(
        Dictionary<TKey, long> destination,
        TKey key,
        GraphStatisticsRefreshOptions options,
        Dictionary<LabelId, long> labels,
        Dictionary<int, long> histogram,
        Dictionary<GraphIndexStatisticKey, long> properties,
        Dictionary<GraphValueStatisticKey, long> values)
        where TKey : notnull
        => AddBounded(destination, key, 1, options, labels, histogram, properties, values);

    private static void AddBounded<TKey>(
        Dictionary<TKey, long> destination,
        TKey key,
        long amount,
        GraphStatisticsRefreshOptions options,
        Dictionary<LabelId, long> labels,
        Dictionary<int, long> histogram,
        Dictionary<GraphIndexStatisticKey, long> properties,
        Dictionary<GraphValueStatisticKey, long> values)
        where TKey : notnull
    {
        if (!destination.ContainsKey(key)
            && checked(labels.Count + histogram.Count + properties.Count + values.Count) >= options.MaxStatisticGroups)
        {
            throw new GraphStatisticsLimitExceededException(
                $"Graph statistics 分组超过 MaxStatisticGroups={options.MaxStatisticGroups}。");
        }
        destination[key] = checked(destination.GetValueOrDefault(key) + amount);
    }
}
