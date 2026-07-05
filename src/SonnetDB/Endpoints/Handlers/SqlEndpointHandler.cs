using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Endpoints;

/// <summary>
/// 提供 <c>POST /v1/db/{db}/sql</c> 与 <c>POST /v1/db/{db}/sql/batch</c> 两个端点的处理逻辑。
/// 结果集以 <c>application/x-ndjson</c> 流式输出。
/// </summary>
internal static class SqlEndpointHandler
{
    /// <summary>慢查询上报时 SQL 文本的最大截断长度。</summary>
    private const int _slowQuerySqlMaxLength = 1024;

    /// <summary>控制面 SQL 作为事件数据库名的占位符。</summary>
    private const string _controlPlaneDatabaseLabel = "__control";

    private static readonly byte[] _newline = "\n"u8.ToArray();

    /// <summary>
    /// 处理单条 SQL 请求。
    /// </summary>
    public static async Task HandleSingleAsync(
        HttpContext context,
        Tsdb tsdb,
        string databaseName,
        SqlRequest request,
        ServerMetrics metrics,
        bool canWrite,
        bool isAdmin,
        IControlPlane? controlPlane)
    {
        await ExecuteAsync(context, tsdb, databaseName, [request], metrics, canWrite, isAdmin, controlPlane).ConfigureAwait(false);
    }

    /// <summary>
    /// 处理批量 SQL 请求。所有语句串行执行。
    /// </summary>
    public static async Task HandleBatchAsync(
        HttpContext context,
        Tsdb tsdb,
        string databaseName,
        SqlBatchRequest request,
        ServerMetrics metrics,
        bool canWrite,
        bool isAdmin,
        IControlPlane? controlPlane)
    {
        await ExecuteAsync(context, tsdb, databaseName, request.Statements, metrics, canWrite, isAdmin, controlPlane).ConfigureAwait(false);
    }

    /// <summary>
    /// 处理 <c>POST /v1/sql</c> 单条控制面 SQL 请求（无 db 路径）。
    /// 仅支持控制面语句（CREATE USER / GRANT / CREATE DATABASE / SHOW USERS 等）以及 <c>SHOW DATABASES</c>。
    /// 调用方需先确认请求者属于 admin 或动态用户 token，具体语句级权限由本方法继续细分。
    /// </summary>
    public static async Task HandleControlPlaneAsync(
        HttpContext context,
        SqlRequest request,
        ServerMetrics metrics,
        bool isAdmin,
        IControlPlane controlPlane)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);
        var broadcaster = context.RequestServices.GetService<EventBroadcaster>();
        var options = context.RequestServices.GetService<IOptions<ServerOptions>>()?.Value;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        var writerOptions = new JsonWriterOptions { Indented = false, SkipValidation = false };

        metrics.RecordSqlRequest();
        var sw = Stopwatch.StartNew();

        SqlStatement parsed;
        try
        {
            parsed = SqlParser.Parse(request.Sql);
        }
        catch (Exception ex)
        {
            metrics.RecordSqlError();
            MaybePublishSlow(broadcaster, options, _controlPlaneDatabaseLabel, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            await WriteErrorAsync(context, "sql_error", ex.Message).ConfigureAwait(false);
            return;
        }

        if (!IsControlPlaneStatement(parsed) && parsed is not ShowDatabasesStatement)
        {
            metrics.RecordSqlError();
            MaybePublishSlow(broadcaster, options, _controlPlaneDatabaseLabel, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            await WriteErrorAsync(context, "bad_request",
                "/v1/sql 仅支持控制面 SQL（CREATE USER / GRANT / CREATE DATABASE / SHOW USERS / SHOW DATABASES 等），数据面 SQL 请走 /v1/db/{db}/sql。").ConfigureAwait(false);
            return;
        }

        if (!TryAuthorizeControlPlaneStatement(context, parsed, isAdmin, out var authorizationError))
        {
            metrics.RecordSqlError();
            MaybePublishSlow(broadcaster, options, _controlPlaneDatabaseLabel, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            await WriteErrorAsync(context, "forbidden", authorizationError).ConfigureAwait(false);
            return;
        }

        object result;
        try
        {
            result = SqlExecutor.ExecuteControlPlaneStatement(parsed, controlPlane);
        }
        catch (ControlPlaneAccessDeniedException ex)
        {
            metrics.RecordSqlError();
            MaybePublishSlow(broadcaster, options, _controlPlaneDatabaseLabel, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            await WriteErrorAsync(context, "forbidden", ex.Message).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            metrics.RecordSqlError();
            MaybePublishSlow(broadcaster, options, _controlPlaneDatabaseLabel, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            await WriteErrorAsync(context, "sql_error", ex.Message).ConfigureAwait(false);
            return;
        }

        if (result is SelectExecutionResult sel)
        {
            long rowCount = await WriteSelectAsync(context, sel, writerOptions).ConfigureAwait(false);
            metrics.AddReturnedRows(rowCount);
            var elapsed = sw.Elapsed.TotalMilliseconds;
            await WriteEndAsync(context, writerOptions, rowCount, recordsAffected: -1, elapsed).ConfigureAwait(false);
            MaybePublishSlow(broadcaster, options, _controlPlaneDatabaseLabel, request.Sql, elapsed, rowCount, -1, failed: false);
        }
        else
        {
            var elapsed = sw.Elapsed.TotalMilliseconds;
            await WriteEndAsync(context, writerOptions, rowCount: 0, recordsAffected: 0, elapsed).ConfigureAwait(false);
            MaybePublishSlow(broadcaster, options, _controlPlaneDatabaseLabel, request.Sql, elapsed, 0, 0, failed: false);
        }
    }

    private static async Task ExecuteAsync(
        HttpContext context,
        Tsdb tsdb,
        string databaseName,
        IReadOnlyList<SqlRequest> statements,
        ServerMetrics metrics,
        bool canWrite,
        bool isAdmin,
        IControlPlane? controlPlane)
    {
        var broadcaster = context.RequestServices.GetService<EventBroadcaster>();
        var options = context.RequestServices.GetService<IOptions<ServerOptions>>()?.Value;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        var writerOptions = new JsonWriterOptions { Indented = false, SkipValidation = false };
        SqlTransactionContext? transaction = null;

        for (int s = 0; s < statements.Count; s++)
        {
            var stmt = statements[s];
            metrics.RecordSqlRequest();
            var sw = Stopwatch.StartNew();

            SqlStatement parsed;
            try
            {
                parsed = SqlParser.Parse(stmt.Sql);
            }
            catch (Exception ex)
            {
                metrics.RecordSqlError();
                MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
                await WriteErrorAsync(context, "sql_error", ex.Message).ConfigureAwait(false);
                return;
            }

            if (!TryAuthorizeControlPlaneStatement(context, parsed, isAdmin, out var authorizationError))
            {
                metrics.RecordSqlError();
                MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
                await WriteErrorAsync(context, "forbidden", authorizationError).ConfigureAwait(false);
                return;
            }

            if (!IsControlPlaneStatement(parsed) && RequiresWritePermission(parsed) && !canWrite)
            {
                metrics.RecordSqlError();
                MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
                await WriteErrorAsync(context, "forbidden", "当前凭据对该数据库没有写权限。").ConfigureAwait(false);
                return;
            }

            object? result;
            try
            {
                if (parsed is BeginTransactionStatement && transaction is not null && !transaction.IsCompleted)
                    throw new InvalidOperationException("当前已有活动轻事务，不能嵌套 BEGIN。");

                result = SqlExecutor.ExecuteStatement(tsdb, databaseName, parsed, controlPlane, transaction);
                if (result is SqlTransactionContext started)
                    transaction = started;
                else if (parsed is CommitTransactionStatement or RollbackTransactionStatement)
                    transaction = null;
            }
            catch (ControlPlaneAccessDeniedException ex)
            {
                metrics.RecordSqlError();
                MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
                await WriteErrorAsync(context, "forbidden", ex.Message).ConfigureAwait(false);
                return;
            }
            catch (TableConstraintException ex)
            {
                metrics.RecordSqlError();
                MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
                await WriteErrorAsync(context, ex.ErrorCode, ex.Message).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                metrics.RecordSqlError();
                MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
                await WriteErrorAsync(context, "sql_error", ex.Message).ConfigureAwait(false);
                return;
            }

            switch (result)
            {
                case SelectExecutionResult sel:
                    {
                        long rowCount = await WriteSelectAsync(context, sel, writerOptions).ConfigureAwait(false);
                        metrics.AddReturnedRows(rowCount);
                        var elapsed = sw.Elapsed.TotalMilliseconds;
                        await WriteEndAsync(context, writerOptions, rowCount, recordsAffected: -1, elapsed).ConfigureAwait(false);
                        MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, elapsed, rowCount, -1, failed: false);
                        break;
                    }
                case InsertExecutionResult ins:
                    {
                        if (!canWrite)
                        {
                            metrics.RecordSqlError();
                            MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
                            await WriteErrorAsync(context, "forbidden", "INSERT 需要 readwrite 或 admin 角色。").ConfigureAwait(false);
                            return;
                        }
                        metrics.AddInsertedRows(ins.RowsInserted);
                        var elapsed = sw.Elapsed.TotalMilliseconds;
                        await WriteEndAsync(context, writerOptions, rowCount: 0, recordsAffected: ins.RowsInserted, elapsed).ConfigureAwait(false);
                        MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, elapsed, 0, ins.RowsInserted, failed: false);
                        break;
                    }
                case DeleteExecutionResult del:
                    {
                        if (!canWrite)
                        {
                            metrics.RecordSqlError();
                            MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
                            await WriteErrorAsync(context, "forbidden", "DELETE 需要 readwrite 或 admin 角色。").ConfigureAwait(false);
                            return;
                        }
                        var elapsed = sw.Elapsed.TotalMilliseconds;
                        await WriteEndAsync(context, writerOptions, rowCount: 0, recordsAffected: del.TombstonesAdded, elapsed).ConfigureAwait(false);
                        MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, elapsed, 0, del.TombstonesAdded, failed: false);
                        break;
                    }
                case RowsAffectedExecutionResult affected:
                    {
                        if (!canWrite)
                        {
                            metrics.RecordSqlError();
                            MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
                            await WriteErrorAsync(context, "forbidden", "该语句需要 readwrite 或 admin 角色。").ConfigureAwait(false);
                            return;
                        }
                        var elapsed = sw.Elapsed.TotalMilliseconds;
                        await WriteEndAsync(context, writerOptions, rowCount: 0, recordsAffected: affected.RowsAffected, elapsed).ConfigureAwait(false);
                        MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, elapsed, 0, affected.RowsAffected, failed: false);
                        break;
                    }
                default:
                    {
                        // CREATE MEASUREMENT、CREATE USER 等 DDL：返回受影响行数 0。
                        // 控制面语句已在上面按 admin-only / self-service 细分鉴权，这里仅校验需 canWrite 的普通 DDL。
                        if (!IsControlPlaneStatement(parsed) && !canWrite)
                        {
                            metrics.RecordSqlError();
                            MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
                            await WriteErrorAsync(context, "forbidden", "DDL 需要 readwrite 或 admin 角色。").ConfigureAwait(false);
                            return;
                        }
                        var elapsed = sw.Elapsed.TotalMilliseconds;
                        await WriteEndAsync(context, writerOptions, rowCount: 0, recordsAffected: 0, elapsed).ConfigureAwait(false);
                        MaybePublishSlow(broadcaster, options, databaseName, stmt.Sql, elapsed, 0, 0, failed: false);
                        break;
                    }
            }
        }

        if (transaction is not null && !transaction.IsCompleted)
        {
            metrics.RecordSqlError();
            await WriteErrorAsync(context, "sql_error", "SQL batch 结束时仍有未提交的轻事务。").ConfigureAwait(false);
        }
    }

    private static async Task<long> WriteSelectAsync(HttpContext context, SelectExecutionResult result, JsonWriterOptions options)
    {
        var body = context.Response.BodyWriter;

        // 1) meta 行
        var meta = new ResultMeta("meta", result.Columns);
        await using (var metaWriter = new Utf8JsonWriter(body, options))
        {
            JsonSerializer.Serialize(metaWriter, meta, ServerJsonContext.Default.ResultMeta);
        }
        await body.WriteAsync(_newline, context.RequestAborted).ConfigureAwait(false);

        // 2) 行数据：每行一条 ndjson
        long count = 0;
        for (int r = 0; r < result.Rows.Count; r++)
        {
            await using (var rowWriter = new Utf8JsonWriter(body, options))
            {
                NdjsonRowWriter.WriteRow(rowWriter, result.Rows[r]);
            }
            await body.WriteAsync(_newline, context.RequestAborted).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private static async Task WriteEndAsync(HttpContext context, JsonWriterOptions options, long rowCount, int recordsAffected, double elapsedMs)
    {
        var body = context.Response.BodyWriter;
        var end = new ResultEnd("end", rowCount, recordsAffected, elapsedMs);
        await using (var w = new Utf8JsonWriter(body, options))
        {
            JsonSerializer.Serialize(w, end, ServerJsonContext.Default.ResultEnd);
        }
        await body.WriteAsync(_newline, context.RequestAborted).ConfigureAwait(false);
        await body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(HttpContext context, string code, string message)
    {
        // 若响应尚未开始：用 4xx 状态码
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = code switch
            {
                "forbidden" => StatusCodes.Status403Forbidden,
                "db_not_found" => StatusCodes.Status404NotFound,
                "unauthorized" => StatusCodes.Status401Unauthorized,
                _ => StatusCodes.Status400BadRequest,
            };
            context.Response.ContentType = "application/json; charset=utf-8";
            var err = new ErrorResponse(code, message);
            await JsonSerializer.SerializeAsync(context.Response.Body, err, ServerJsonContext.Default.ErrorResponse, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // 已经在 ndjson 流中：附加一条错误行（type=error）
        var body = context.Response.BodyWriter;
        await using (var w = new Utf8JsonWriter(body, new JsonWriterOptions { Indented = false }))
        {
            JsonSerializer.Serialize(w, new ErrorResponse(code, message), ServerJsonContext.Default.ErrorResponse);
        }
        await body.WriteAsync(_newline, context.RequestAborted).ConfigureAwait(false);
        await body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static bool TryAuthorizeControlPlaneStatement(
        HttpContext context,
        SqlStatement statement,
        bool isAdmin,
        out string errorMessage)
    {
        if (IsAdminOnlyControlPlaneStatement(statement) && !isAdmin)
        {
            errorMessage = "控制面 SQL（CREATE USER / GRANT / CREATE DATABASE / SHOW USERS 等）仅 admin 可执行。";
            return false;
        }

        if (IsSelfServiceControlPlaneStatement(statement) && !(isAdmin || HasSelfServiceControlPlaneAccess(context)))
        {
            errorMessage = "SHOW GRANTS / SHOW TOKENS / ISSUE TOKEN / REVOKE TOKEN 仅动态用户本人可执行，admin 除外。";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static bool HasSelfServiceControlPlaneAccess(HttpContext context)
        => BearerAuthMiddleware.GetUser(context) is AuthenticatedUser { IsSuperuser: false };

    /// <summary>判别是否为需要通过服务端控制面执行的 SQL。</summary>
    internal static bool IsControlPlaneStatement(SqlStatement statement)
        => IsAdminOnlyControlPlaneStatement(statement) || IsSelfServiceControlPlaneStatement(statement);

    /// <summary>判别是否为仅 admin 可执行的控制面语句。</summary>
    private static bool IsAdminOnlyControlPlaneStatement(SqlStatement statement) => statement is
        CreateUserStatement or
        AlterUserPasswordStatement or
        DropUserStatement or
        GrantStatement or
        RevokeStatement or
        CreateDatabaseStatement or
        DropDatabaseStatement or
        ShowUsersStatement;

    /// <summary>判别是否为普通动态用户可按“仅自己”执行的控制面语句。</summary>
    private static bool IsSelfServiceControlPlaneStatement(SqlStatement statement) => statement is
        ShowGrantsStatement or
        ShowTokensStatement or
        IssueTokenStatement or
        RevokeTokenStatement;

    /// <summary>
    /// 判别是否为需要数据库写权限的数据面语句。
    /// </summary>
    internal static bool RequiresWritePermission(SqlStatement statement) => statement is not
        (SelectStatement or
        ShowMeasurementsStatement or
        ShowTablesStatement or
        ShowTableIndexesStatement or
        ShowDocumentCollectionsStatement or
        ShowDocumentIndexesStatement or
        ShowFullTextIndexesStatement or
        DescribeMeasurementStatement or
        DescribeTableStatement or
        DescribeDocumentCollectionStatement or
        ExplainStatement or
        ShowDatabasesStatement);

    internal static void MaybePublishSlow(
        EventBroadcaster? broadcaster,
        ServerOptions? options,
        string database,
        string sql,
        double elapsedMs,
        long rowCount,
        int recordsAffected,
        bool failed)
    {
        if (broadcaster is null || options is null)
            return;
        if (!options.SlowQueryEnabled)
            return;
        if (options.SlowQueryThresholdMs < 0)
            return;
        if (broadcaster.SubscriberCount == 0)
            return;
        if (options.SlowQueryThresholdMs > 0 && elapsedMs < options.SlowQueryThresholdMs)
            return;
        var truncated = sql.Length > _slowQuerySqlMaxLength
            ? sql[.._slowQuerySqlMaxLength]
            : sql;
        var severity = GetSlowQuerySeverity(options, elapsedMs);
        var payload = new SlowQueryEvent(database, truncated, elapsedMs, rowCount, recordsAffected, failed, severity);
        broadcaster.Publish(ServerEventFactory.SlowQuery(payload));
    }

    private static string GetSlowQuerySeverity(ServerOptions options, double elapsedMs)
    {
        if (options.SlowQueryCriticalThresholdMs > 0 && elapsedMs >= options.SlowQueryCriticalThresholdMs)
            return SlowQuerySeverity.Critical;
        if (options.SlowQueryWarningThresholdMs > 0 && elapsedMs >= options.SlowQueryWarningThresholdMs)
            return SlowQuerySeverity.Warning;
        return SlowQuerySeverity.Slow;
    }
}
