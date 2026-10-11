using Microsoft.AspNetCore.Http;
using SonnetDB.Engine;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Endpoints;

/// <summary>限定 Web Preview 的 SQL 动作；不替代现有身份与数据库授权。</summary>
internal static class WorkbenchPreviewSql
{
    internal const int MaximumInputBytes = 64 * 1024;
    internal const int MaximumResultBytes = 4 * 1024 * 1024;
    internal static bool IsRequested(HttpContext context)
        => context.Request.Headers.ContainsKey("X-SonnetDB-Workbench-Profile");

    internal static bool Allows(HttpContext context, Tsdb database, SqlStatement statement, int statementCount, CancellationToken cancellationToken)
    {
        if (!IsRequested(context))
            return true;
        if (context.Request.Headers["X-SonnetDB-Workbench-Profile"] != "preview-1" || statementCount != 1)
            return false;

        if (statement is ShowTablesStatement or ShowMeasurementsStatement or DescribeTableStatement or DescribeMeasurementStatement)
        {
            try
            {
                _ = WorkbenchPreviewMetadata.Read(database, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return context.Request.Headers["X-SonnetDB-Workbench-Action"].ToString() switch
        {
            "sql.read" => IsRead(statement),
            "relation.insert.one" => statement is InsertStatement insert
                && insert.Query is null && !insert.IsDefaultValues && insert.Rows.Count == 1
                && insert.Rows[0].Count == insert.Columns.Count && insert.Columns.Count > 0
                && insert.Rows[0].All(static value => value is ParameterExpression)
                && database.Tables.Catalog.Resolve(insert.Measurement, insert.MeasurementIsQuoted) is not null,
            _ => false,
        };
    }

    private static bool IsRead(SqlStatement statement) => statement switch
    {
        SelectStatement or ShowTablesStatement or ShowMeasurementsStatement
            or DescribeTableStatement or DescribeMeasurementStatement => true,
        ExplainStatement explain => explain.Statement is SelectStatement,
        _ => false,
    };
}
