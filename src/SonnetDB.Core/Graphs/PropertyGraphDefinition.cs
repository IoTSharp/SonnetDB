using System.Collections.Frozen;

namespace SonnetDB.Graphs;

/// <summary>关系表映射图中的顶点表声明。</summary>
/// <param name="TableName">权威关系表名称。</param>
/// <param name="KeyColumns">稳定且唯一的顶点键列。</param>
/// <param name="Label">顶点 label。</param>
/// <param name="PropertyColumns">作为图属性公开的关系列。</param>
public sealed record PropertyGraphVertexTable(
    string TableName,
    IReadOnlyList<string> KeyColumns,
    string Label,
    IReadOnlyList<string> PropertyColumns);

/// <summary>关系表映射图中的边表声明。</summary>
/// <param name="TableName">权威关系表名称。</param>
/// <param name="KeyColumns">稳定且唯一的边键列。</param>
/// <param name="SourceTable">源顶点表名称。</param>
/// <param name="SourceColumns">边表中的 source key 列。</param>
/// <param name="SourceReferenceColumns">源顶点表中的被引用键列。</param>
/// <param name="DestinationTable">目标顶点表名称。</param>
/// <param name="DestinationColumns">边表中的 destination key 列。</param>
/// <param name="DestinationReferenceColumns">目标顶点表中的被引用键列。</param>
/// <param name="Label">边 label。</param>
/// <param name="PropertyColumns">作为图属性公开的关系列。</param>
public sealed record PropertyGraphEdgeTable(
    string TableName,
    IReadOnlyList<string> KeyColumns,
    string SourceTable,
    IReadOnlyList<string> SourceColumns,
    IReadOnlyList<string> SourceReferenceColumns,
    string DestinationTable,
    IReadOnlyList<string> DestinationColumns,
    IReadOnlyList<string> DestinationReferenceColumns,
    string Label,
    IReadOnlyList<string> PropertyColumns);

/// <summary>
/// SQL/PGQ property graph 的持久化关系映射。定义只引用关系 schema，不持有或复制关系行。
/// </summary>
public sealed class PropertyGraphDefinition
{
    private readonly FrozenDictionary<string, PropertyGraphVertexTable> _verticesByTable;
    private readonly FrozenDictionary<string, PropertyGraphEdgeTable> _edgesByTable;

    private PropertyGraphDefinition(
        string name,
        IReadOnlyList<PropertyGraphVertexTable> vertexTables,
        IReadOnlyList<PropertyGraphEdgeTable> edgeTables,
        long createdAtUtcTicks)
    {
        Name = name;
        VertexTables = vertexTables;
        EdgeTables = edgeTables;
        CreatedAtUtcTicks = createdAtUtcTicks;
        _verticesByTable = vertexTables.ToFrozenDictionary(static item => item.TableName, StringComparer.Ordinal);
        _edgesByTable = edgeTables.ToFrozenDictionary(static item => item.TableName, StringComparer.Ordinal);
    }

    /// <summary>映射图名称。</summary>
    public string Name { get; }

    /// <summary>顶点表映射。</summary>
    public IReadOnlyList<PropertyGraphVertexTable> VertexTables { get; }

    /// <summary>边表映射。</summary>
    public IReadOnlyList<PropertyGraphEdgeTable> EdgeTables { get; }

    /// <summary>创建时间 UTC ticks。</summary>
    public long CreatedAtUtcTicks { get; }

    /// <summary>按关系表名查找顶点映射。</summary>
    /// <param name="tableName">关系表名称。</param>
    /// <returns>找到时返回映射，否则返回 <c>null</c>。</returns>
    public PropertyGraphVertexTable? TryGetVertexTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return _verticesByTable.GetValueOrDefault(tableName);
    }

    /// <summary>按关系表名查找边映射。</summary>
    /// <param name="tableName">关系表名称。</param>
    /// <returns>找到时返回映射，否则返回 <c>null</c>。</returns>
    public PropertyGraphEdgeTable? TryGetEdgeTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return _edgesByTable.GetValueOrDefault(tableName);
    }

    /// <summary>创建并校验不依赖具体关系 schema 的映射定义。</summary>
    /// <param name="name">映射图名称。</param>
    /// <param name="vertexTables">顶点表映射。</param>
    /// <param name="edgeTables">边表映射。</param>
    /// <returns>不可变映射定义。</returns>
    public static PropertyGraphDefinition Create(
        string name,
        IReadOnlyList<PropertyGraphVertexTable> vertexTables,
        IReadOnlyList<PropertyGraphEdgeTable> edgeTables)
        => Restore(name, vertexTables, edgeTables, DateTime.UtcNow.Ticks);

    internal static PropertyGraphDefinition Restore(
        string name,
        IReadOnlyList<PropertyGraphVertexTable> vertexTables,
        IReadOnlyList<PropertyGraphEdgeTable> edgeTables,
        long createdAtUtcTicks)
    {
        GraphDefinition.ValidateName(name);
        ArgumentNullException.ThrowIfNull(vertexTables);
        ArgumentNullException.ThrowIfNull(edgeTables);
        if (vertexTables.Count == 0)
            throw new ArgumentException("Property graph 至少需要一个 vertex table。", nameof(vertexTables));
        if (createdAtUtcTicks <= 0 || createdAtUtcTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(nameof(createdAtUtcTicks));

        PropertyGraphVertexTable[] vertices = vertexTables.Select(CloneAndValidate).ToArray();
        PropertyGraphEdgeTable[] edges = edgeTables.Select(CloneAndValidate).ToArray();
        EnsureUniqueTableNames(vertices, edges);
        return new PropertyGraphDefinition(
            name,
            Array.AsReadOnly(vertices),
            Array.AsReadOnly(edges),
            createdAtUtcTicks);
    }

    private static PropertyGraphVertexTable CloneAndValidate(PropertyGraphVertexTable mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ValidateName(mapping.TableName, "vertex table");
        ValidateName(mapping.Label, "vertex label");
        string[] keys = CloneNames(mapping.KeyColumns, "vertex key", requireNonEmpty: true);
        string[] properties = CloneNames(mapping.PropertyColumns, "vertex property", requireNonEmpty: false);
        EnsureNoDuplicates(keys, "vertex key");
        EnsureNoDuplicates(properties, "vertex property");
        return mapping with
        {
            KeyColumns = Array.AsReadOnly(keys),
            PropertyColumns = Array.AsReadOnly(properties),
        };
    }

    private static PropertyGraphEdgeTable CloneAndValidate(PropertyGraphEdgeTable mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ValidateName(mapping.TableName, "edge table");
        ValidateName(mapping.SourceTable, "source table");
        ValidateName(mapping.DestinationTable, "destination table");
        ValidateName(mapping.Label, "edge label");
        string[] keys = CloneNames(mapping.KeyColumns, "edge key", requireNonEmpty: true);
        string[] source = CloneNames(mapping.SourceColumns, "source key", requireNonEmpty: true);
        string[] sourceReferences = CloneNames(mapping.SourceReferenceColumns, "source reference", requireNonEmpty: true);
        string[] destination = CloneNames(mapping.DestinationColumns, "destination key", requireNonEmpty: true);
        string[] destinationReferences = CloneNames(mapping.DestinationReferenceColumns, "destination reference", requireNonEmpty: true);
        string[] properties = CloneNames(mapping.PropertyColumns, "edge property", requireNonEmpty: false);
        if (source.Length != sourceReferences.Length)
            throw new ArgumentException("source key 与 reference 列数量必须一致。", nameof(mapping));
        if (destination.Length != destinationReferences.Length)
            throw new ArgumentException("destination key 与 reference 列数量必须一致。", nameof(mapping));
        EnsureNoDuplicates(keys, "edge key");
        EnsureNoDuplicates(source, "source key");
        EnsureNoDuplicates(destination, "destination key");
        EnsureNoDuplicates(properties, "edge property");
        return mapping with
        {
            KeyColumns = Array.AsReadOnly(keys),
            SourceColumns = Array.AsReadOnly(source),
            SourceReferenceColumns = Array.AsReadOnly(sourceReferences),
            DestinationColumns = Array.AsReadOnly(destination),
            DestinationReferenceColumns = Array.AsReadOnly(destinationReferences),
            PropertyColumns = Array.AsReadOnly(properties),
        };
    }

    private static string[] CloneNames(
        IReadOnlyList<string> names,
        string description,
        bool requireNonEmpty)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (requireNonEmpty && names.Count == 0)
            throw new ArgumentException($"{description} 列不能为空。", nameof(names));
        string[] copy = names.ToArray();
        foreach (string name in copy)
            ValidateName(name, description);
        return copy;
    }

    private static void ValidateName(string name, string description)
    {
        try
        {
            GraphDefinition.ValidateName(name);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"{description} 名称无效。", nameof(name), exception);
        }
    }

    private static void EnsureNoDuplicates(IReadOnlyList<string> names, string description)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
            if (!seen.Add(name))
                throw new ArgumentException($"{description} 列 '{name}' 重复。", nameof(names));
    }

    private static void EnsureUniqueTableNames(
        IReadOnlyList<PropertyGraphVertexTable> vertices,
        IReadOnlyList<PropertyGraphEdgeTable> edges)
    {
        var tableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyGraphVertexTable vertex in vertices)
            if (!tableNames.Add(vertex.TableName))
                throw new ArgumentException($"Property graph 表 '{vertex.TableName}' 重复映射。", nameof(vertices));
        foreach (PropertyGraphEdgeTable edge in edges)
            if (!tableNames.Add(edge.TableName))
                throw new ArgumentException($"Property graph 表 '{edge.TableName}' 重复映射。", nameof(edges));
    }
}
