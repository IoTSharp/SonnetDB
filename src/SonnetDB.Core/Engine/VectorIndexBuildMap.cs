using SonnetDB.Catalog;
using SonnetDB.Memory;
using SonnetDB.Model;

namespace SonnetDB.Engine;

/// <summary>
/// 为 Flush / Compaction 计算需要构建向量索引的字段集合。
/// </summary>
internal static class VectorIndexBuildMap
{
    /// <summary>
    /// Segment 写出前解析向量索引集合，并强制 I11 不变式：待写桶若含 VECTOR 字段，则必须提供
    /// catalog 来解析其索引声明；否则该段的向量块会被无索引持久化，静默退化为暴力扫。
    /// <para>
    /// 两个 catalog 均非 null 时等价于 <see cref="Build"/>；若缺失 catalog 但桶中存在 VECTOR 字段，
    /// 抛 <see cref="InvalidOperationException"/>（而非返回 null 让调用方静默丢索引）。
    /// 无向量字段时返回 null（无需构建）。
    /// </para>
    /// </summary>
    /// <param name="seriesList">待写出的桶列表。</param>
    /// <param name="catalog">Series 目录；可为 null（仅当桶中无向量字段时）。</param>
    /// <param name="measurements">Measurement schema 目录；可为 null（仅当桶中无向量字段时）。</param>
    /// <returns>向量索引定义映射；无向量字段时为 null。</returns>
    /// <exception cref="InvalidOperationException">
    /// 桶中存在 VECTOR 字段但 <paramref name="catalog"/> 或 <paramref name="measurements"/> 为 null 时抛出（I11）。
    /// </exception>
    public static IReadOnlyDictionary<SeriesFieldKey, VectorIndexDefinition>? BuildForSegment(
        IReadOnlyList<MemTableSeries> seriesList,
        SeriesCatalog? catalog,
        MeasurementCatalog? measurements)
    {
        ArgumentNullException.ThrowIfNull(seriesList);

        if (catalog is not null && measurements is not null)
            return Build(seriesList, catalog, measurements);

        // 缺失 catalog：只要有任一 VECTOR 桶，就是调用方漏传 catalog —— 该段的向量块将无索引落盘，
        // 查询时静默退化为暴力扫（I11）。转为显式失败，避免"慢但正确"掩盖调用契约违反。
        foreach (var series in seriesList)
        {
            if (series.FieldType == Storage.Format.FieldType.Vector)
            {
                throw new InvalidOperationException(
                    $"Cannot write a segment containing VECTOR field '{series.Key.FieldName}' " +
                    "without a series/measurement catalog: the vector block would be persisted " +
                    "without an index and silently degrade to brute-force scan (I11). " +
                    "Provide both seriesCatalog and measurementCatalog.");
            }
        }

        return null;
    }

    /// <summary>
    /// 根据当前待写出的 MemTable 桶，解析出需要构建向量索引的字段集合。
    /// </summary>
    /// <param name="seriesList">待写出的桶列表。</param>
    /// <param name="catalog">Series 目录。</param>
    /// <param name="measurements">Measurement schema 目录。</param>
    /// <returns>按 <see cref="SeriesFieldKey"/> 索引的向量索引定义。</returns>
    public static IReadOnlyDictionary<SeriesFieldKey, VectorIndexDefinition> Build(
        IReadOnlyList<MemTableSeries> seriesList,
        SeriesCatalog catalog,
        MeasurementCatalog measurements)
    {
        ArgumentNullException.ThrowIfNull(seriesList);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(measurements);

        var result = new Dictionary<SeriesFieldKey, VectorIndexDefinition>();
        foreach (var series in seriesList)
        {
            if (series.FieldType != Storage.Format.FieldType.Vector)
                continue;

            var vectorIndex = Resolve(series.Key.SeriesId, series.Key.FieldName, catalog, measurements);
            if (vectorIndex is not null)
                result[series.Key] = vectorIndex;
        }

        return result;
    }

    /// <summary>
    /// 解析指定 (SeriesId, FieldName) 是否声明了向量索引。
    /// </summary>
    /// <param name="seriesId">目标序列 ID。</param>
    /// <param name="fieldName">目标字段名。</param>
    /// <param name="catalog">Series 目录。</param>
    /// <param name="measurements">Measurement schema 目录。</param>
    /// <returns>若该字段声明了向量索引则返回定义，否则返回 null。</returns>
    public static VectorIndexDefinition? Resolve(
        ulong seriesId,
        string fieldName,
        SeriesCatalog catalog,
        MeasurementCatalog measurements)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(measurements);

        var series = catalog.TryGet(seriesId);
        if (series is null)
            return null;

        var schema = measurements.TryGet(series.Measurement);
        var column = schema?.TryGetColumn(fieldName);
        return column?.VectorIndex;
    }
}
