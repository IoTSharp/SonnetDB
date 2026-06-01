using System.Collections.Frozen;
using System.Collections.ObjectModel;
using SonnetDB.Storage.Format;

namespace SonnetDB.Catalog;

/// <summary>
/// 一个 measurement 的 schema 定义：包含列定义（按声明顺序）以及按名查找索引。
/// 不可变值对象；通过 <see cref="Create"/> 校验后构造。
/// </summary>
public sealed class MeasurementSchema
{
    private readonly FrozenDictionary<string, MeasurementColumn> _byName;

    /// <summary>Measurement 名称（区分大小写，非空且不含保留字符）。</summary>
    public string Name { get; }

    /// <summary>列定义列表（按 CREATE 语句中的声明顺序）。</summary>
    public IReadOnlyList<MeasurementColumn> Columns { get; }

    /// <summary>Schema 创建时间（UTC Ticks）。</summary>
    public long CreatedAtUtcTicks { get; }

    private MeasurementSchema(string name, IReadOnlyList<MeasurementColumn> columns, long createdAtUtcTicks)
    {
        Name = name;
        Columns = columns;
        CreatedAtUtcTicks = createdAtUtcTicks;
        var byName = new Dictionary<string, MeasurementColumn>(columns.Count, StringComparer.Ordinal);
        foreach (var col in columns)
            byName[col.Name] = col;
        _byName = byName.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// 创建并校验一个新的 <see cref="MeasurementSchema"/>。
    /// </summary>
    /// <param name="name">measurement 名称。</param>
    /// <param name="columns">列定义，至少包含一列且至少一个 <see cref="MeasurementColumnRole.Field"/>。</param>
    /// <param name="createdAtUtcTicks">创建时间（UTC Ticks）；省略时使用当前时间。</param>
    /// <returns>校验通过的 <see cref="MeasurementSchema"/>。</returns>
    /// <exception cref="ArgumentException">校验失败时抛出。</exception>
    public static MeasurementSchema Create(
        string name,
        IReadOnlyList<MeasurementColumn> columns,
        long? createdAtUtcTicks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
            throw new ArgumentException("Measurement schema 至少需要一列。", nameof(columns));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fieldCount = 0;
        var copy = new List<MeasurementColumn>(columns.Count);

        foreach (var col in columns)
        {
            ArgumentNullException.ThrowIfNull(col);
            if (string.IsNullOrWhiteSpace(col.Name))
                throw new ArgumentException("列名不能为空。", nameof(columns));
            if (!seen.Add(col.Name))
                throw new ArgumentException($"重复的列名 '{col.Name}'。", nameof(columns));
            if (col.Role == MeasurementColumnRole.Tag && col.DataType != FieldType.String)
                throw new ArgumentException(
                    $"Tag 列 '{col.Name}' 必须是 STRING 类型，但声明为 {col.DataType}。", nameof(columns));
            if (col.DataType == FieldType.Unknown)
                throw new ArgumentException($"列 '{col.Name}' 的数据类型不能为 Unknown。", nameof(columns));
            if (col.DataType == FieldType.Vector)
            {
                if (col.Role != MeasurementColumnRole.Field)
                    throw new ArgumentException(
                        $"VECTOR 列 '{col.Name}' 必须是 FIELD 角色。", nameof(columns));
                if (col.VectorDimension is not int dim || dim <= 0)
                    throw new ArgumentException(
                        $"VECTOR 列 '{col.Name}' 必须声明正的维度（VectorDimension > 0），实际为 {(col.VectorDimension?.ToString() ?? "null")}。",
                        nameof(columns));
                if (col.VectorIndex is not null)
                    ValidateVectorIndex(col.Name, col.VectorIndex, nameof(columns));
            }
            else if (col.VectorDimension is not null || col.VectorIndex is not null)
            {
                throw new ArgumentException(
                    $"列 '{col.Name}' 的类型为 {col.DataType}，不应声明向量维度或向量索引。", nameof(columns));
            }
            if (col.Role == MeasurementColumnRole.Field)
                fieldCount++;
            copy.Add(col);
        }

        if (fieldCount == 0)
            throw new ArgumentException(
                "Measurement schema 至少需要一个 FIELD 列。", nameof(columns));

        return new MeasurementSchema(
            name,
            new ReadOnlyCollection<MeasurementColumn>(copy),
            createdAtUtcTicks ?? DateTime.UtcNow.Ticks);
    }

    /// <summary>按名查找列；未命中返回 null。</summary>
    /// <param name="columnName">列名（区分大小写）。</param>
    public MeasurementColumn? TryGetColumn(string columnName)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        return _byName.GetValueOrDefault(columnName);
    }

    /// <summary>枚举所有 Tag 列（按声明顺序）。</summary>
    public IEnumerable<MeasurementColumn> TagColumns
        => Columns.Where(c => c.Role == MeasurementColumnRole.Tag);

    /// <summary>枚举所有 Field 列（按声明顺序）。</summary>
    public IEnumerable<MeasurementColumn> FieldColumns
        => Columns.Where(c => c.Role == MeasurementColumnRole.Field);

    private static void ValidateVectorIndex(string columnName, VectorIndexDefinition vectorIndex, string paramName)
    {
        ArgumentNullException.ThrowIfNull(vectorIndex);

        switch (vectorIndex.Kind)
        {
            case VectorIndexKind.Hnsw:
                var hnsw = vectorIndex.Hnsw ?? throw new ArgumentException(
                    $"VECTOR 列 '{columnName}' 的 HNSW 参数缺失。", paramName);
                if (hnsw.M < 2)
                    throw new ArgumentException(
                        $"VECTOR 列 '{columnName}' 的 HNSW 参数 m 必须 >= 2，实际为 {hnsw.M}。",
                        paramName);
                if (hnsw.Ef <= 0)
                    throw new ArgumentException(
                        $"VECTOR 列 '{columnName}' 的 HNSW 参数 ef 必须 > 0，实际为 {hnsw.Ef}。",
                        paramName);
                if (hnsw.Ef < hnsw.M)
                    throw new ArgumentException(
                        $"VECTOR 列 '{columnName}' 的 HNSW 参数 ef({hnsw.Ef}) 必须 >= m({hnsw.M})。",
                        paramName);
                break;

            case VectorIndexKind.IvfFlat:
                var ivf = vectorIndex.Ivf ?? throw new ArgumentException(
                    $"VECTOR 列 '{columnName}' 的 IVF 参数缺失。", paramName);
                ValidateIvf(columnName, ivf.NList, ivf.NProbe, ivf.MaxIterations, paramName);
                break;

            case VectorIndexKind.IvfPq:
                var ivfPq = vectorIndex.IvfPq ?? throw new ArgumentException(
                    $"VECTOR 列 '{columnName}' 的 IVF-PQ 参数缺失。", paramName);
                ValidateIvf(columnName, ivfPq.NList, ivfPq.NProbe, ivfPq.MaxIterations, paramName);
                if (ivfPq.M <= 0)
                    throw new ArgumentException(
                        $"VECTOR 列 '{columnName}' 的 IVF-PQ 参数 m 必须 > 0，实际为 {ivfPq.M}。",
                        paramName);
                if (ivfPq.NBits != 8)
                    throw new ArgumentException(
                        $"VECTOR 列 '{columnName}' 的 IVF-PQ 参数 nbits 当前仅支持 8，实际为 {ivfPq.NBits}。",
                        paramName);
                break;

            case VectorIndexKind.Vamana:
                var vamana = vectorIndex.Vamana ?? throw new ArgumentException(
                    $"VECTOR 列 '{columnName}' 的 Vamana 参数缺失。", paramName);
                if (vamana.MaxDegree <= 0)
                    throw new ArgumentException(
                        $"VECTOR 列 '{columnName}' 的 Vamana 参数 max_degree 必须 > 0，实际为 {vamana.MaxDegree}。",
                        paramName);
                if (vamana.SearchListSize < vamana.MaxDegree)
                    throw new ArgumentException(
                        $"VECTOR 列 '{columnName}' 的 Vamana 参数 search_list_size({vamana.SearchListSize}) 必须 >= max_degree({vamana.MaxDegree})。",
                        paramName);
                if (vamana.Alpha < 1.0f)
                    throw new ArgumentException(
                        $"VECTOR 列 '{columnName}' 的 Vamana 参数 alpha 必须 >= 1.0，实际为 {vamana.Alpha}。",
                        paramName);
                if (vamana.BeamWidth <= 0)
                    throw new ArgumentException(
                        $"VECTOR 列 '{columnName}' 的 Vamana 参数 beam_width 必须 > 0，实际为 {vamana.BeamWidth}。",
                        paramName);
                break;

            default:
                throw new ArgumentException(
                    $"VECTOR 列 '{columnName}' 使用了未知索引类型 {vectorIndex.Kind}。", paramName);
        }
    }

    private static void ValidateIvf(string columnName, int nList, int nProbe, int maxIterations, string paramName)
    {
        if (nList <= 0)
            throw new ArgumentException(
                $"VECTOR 列 '{columnName}' 的 IVF 参数 nlist 必须 > 0，实际为 {nList}。",
                paramName);
        if (nProbe <= 0)
            throw new ArgumentException(
                $"VECTOR 列 '{columnName}' 的 IVF 参数 nprobe 必须 > 0，实际为 {nProbe}。",
                paramName);
        if (nProbe > nList)
            throw new ArgumentException(
                $"VECTOR 列 '{columnName}' 的 IVF 参数 nprobe({nProbe}) 不能超过 nlist({nList})。",
                paramName);
        if (maxIterations <= 0)
            throw new ArgumentException(
                $"VECTOR 列 '{columnName}' 的 IVF 参数 max_iterations 必须 > 0，实际为 {maxIterations}。",
                paramName);
    }
}
