using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Mcp;

/// <summary>
/// 为 MCP <c>explain_sql</c> 估算查询将扫描的段数与行数。
/// </summary>
internal sealed class SonnetDbMcpExplainSqlService
{
    /// <summary>
    /// 解释一条只读 SQL。
    /// </summary>
    public McpExplainSqlResult Explain(string databaseName, Tsdb tsdb, SqlStatement statement)
    {
        ArgumentException.ThrowIfNullOrEmpty(databaseName);
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var result = SqlExplainPlanner.Explain(databaseName, tsdb, statement);
        return new McpExplainSqlResult(
            Database: result.Database ?? databaseName,
            StatementType: result.StatementType,
            Measurement: result.Measurement,
            MatchedSeriesCount: result.MatchedSeriesCount,
            EstimatedSegmentCount: result.EstimatedSegmentCount,
            EstimatedBlockCount: result.EstimatedBlockCount,
            EstimatedScannedRows: result.EstimatedScannedRows,
            EstimatedMemTableRows: result.EstimatedMemTableRows,
            EstimatedSegmentRows: result.EstimatedSegmentRows,
            HasTimeFilter: result.HasTimeFilter,
            TagFilterCount: result.TagFilterCount);
    }
}
