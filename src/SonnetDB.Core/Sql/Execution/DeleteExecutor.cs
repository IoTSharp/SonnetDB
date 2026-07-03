using SonnetDB.Engine;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// <c>DELETE</c> 语句执行的内部辅助。复用 <see cref="WhereClauseDecomposer"/> 解析 tag 过滤
/// 与时间窗，对所有命中的 series × schema 中所有 Field 列调用 <see cref="Tsdb.Delete(ulong, string, long, long)"/>。
/// </summary>
internal static class DeleteExecutor
{
    public static DeleteExecutionResult Execute(Tsdb tsdb, DeleteStatement statement)
    {
        var schema = tsdb.Measurements.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"Measurement '{statement.Measurement}' 不存在；请先执行 CREATE MEASUREMENT。");

        var where = WhereClauseDecomposer.Decompose(statement.Where, schema);

        // DELETE 通过 (series, field, 时间窗) 墓碑删除，无法表达"仅删残差字段谓词命中的点"；
        // 残差（字段谓词 / OR / 未知列比较等）在此显式拒绝（按点定向删除见 ROADMAP #219）。
        if (where.Residual is not null)
            throw new InvalidOperationException(
                "DELETE 的 WHERE 仅支持 tag 等值与 time 比较（AND 连接）；字段谓词 / OR / 非等值 tag 比较暂不支持。");

        var matchedSeries = tsdb.Catalog.Find(statement.Measurement, where.TagFilter);

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
}
