using SonnetDB.Engine;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// <c>DELETE</c> 语句执行的内部辅助。复用 <see cref="WhereClauseDecomposer"/> 解析 tag 过滤
/// 与时间窗，对所有命中的 series × schema 中所有 Field 列调用 <see cref="Tsdb.Delete(ulong, string, long, long)"/>。
/// 当 WHERE 含字段谓词 / OR 等残差（#219）时，逐点求值残差、按命中时间戳定向删除，而非无差别删整个时间窗。
/// </summary>
internal static class DeleteExecutor
{
    public static DeleteExecutionResult Execute(Tsdb tsdb, DeleteStatement statement)
    {
        var schema = tsdb.Measurements.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"Measurement '{statement.Measurement}' 不存在；请先执行 CREATE MEASUREMENT。");

        var where = WhereClauseDecomposer.Decompose(statement.Where, schema);
        var matchedSeries = tsdb.Catalog.Find(statement.Measurement, where.TagFilter);

        return where.Residual is null
            ? DeleteByTimeRange(tsdb, schema, where, matchedSeries)
            : DeleteByResidualMatch(tsdb, schema, where, matchedSeries);
    }

    /// <summary>
    /// 无残差：DELETE 只受 tag 等值 + time 比较约束，对每个命中 series 的每个 Field 列在
    /// 闭区间 [from, to] 追加墓碑（原有语义）。
    /// </summary>
    private static DeleteExecutionResult DeleteByTimeRange(
        Tsdb tsdb,
        Catalog.MeasurementSchema schema,
        WhereClause where,
        IReadOnlyList<Catalog.SeriesEntry> matchedSeries)
    {
        long from = where.TimeRange.FromInclusive;
        // TimeRange 是闭区间 [FromInclusive, ToInclusive]，与 Tsdb.Delete 的语义一致。
        long to = where.TimeRange.ToInclusive;

        int tombstones = 0;
        foreach (var series in matchedSeries)
        {
            foreach (var col in schema.FieldColumns)
            {
                tsdb.Delete(series.Id, col.Name, from, to);
                tombstones++;
            }
        }

        return new DeleteExecutionResult(schema.Name, matchedSeries.Count, tombstones);
    }

    /// <summary>
    /// 有残差（字段谓词 / OR / IN 等，#219）：逐点求值残差三值 Kleene（复用 #217 语义），
    /// 对每个命中时刻在该 series 的所有 Field 列追加单点闭区间 <c>[ts, ts]</c> 墓碑。
    /// 由于墓碑以 (series, field, 时间窗) 为粒度、无法表达"仅某一列的值命中"，故删除该时刻整行
    /// （所有 field 列），与"一行 = 一个时刻"的时序模型一致。
    /// </summary>
    private static DeleteExecutionResult DeleteByResidualMatch(
        Tsdb tsdb,
        Catalog.MeasurementSchema schema,
        WhereClause where,
        IReadOnlyList<Catalog.SeriesEntry> matchedSeries)
    {
        // 未知列在无候选点时逐点路径不会触发错误，故先做一次静态校验硬报错。
        SelectExecutor.ValidateResidualColumns(where.Residual!, schema);

        var fieldColumns = schema.FieldColumns.ToArray();
        int seriesAffected = 0;
        int tombstones = 0;

        foreach (var series in matchedSeries)
        {
            var timestamps = SelectExecutor.CollectResidualMatchedTimestamps(tsdb, schema, series, where);
            if (timestamps.Count == 0)
                continue;

            seriesAffected++;
            foreach (var ts in timestamps)
            {
                foreach (var col in fieldColumns)
                {
                    tsdb.Delete(series.Id, col.Name, ts, ts);
                    tombstones++;
                }
            }
        }

        return new DeleteExecutionResult(schema.Name, seriesAffected, tombstones);
    }
}
