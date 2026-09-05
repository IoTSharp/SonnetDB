using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Modbus;
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
        bool canAdministerDatabase,
        bool isServerAdmin,
        IControlPlane? controlPlane,
        double queueWaitMs)
    {
        await ExecuteAsync(
            context,
            tsdb,
            databaseName,
            [request],
            metrics,
            canWrite,
            canAdministerDatabase,
            isServerAdmin,
            controlPlane,
            queueWaitMs).ConfigureAwait(false);
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
        bool canAdministerDatabase,
        bool isServerAdmin,
        IControlPlane? controlPlane,
        double queueWaitMs)
    {
        await ExecuteAsync(
            context,
            tsdb,
            databaseName,
            request.Statements,
            metrics,
            canWrite,
            canAdministerDatabase,
            isServerAdmin,
            controlPlane,
            queueWaitMs).ConfigureAwait(false);
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
        var diagnostics = context.RequestServices.GetService<SlowQueryDiagnostics>();
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
            RecordSlow(diagnostics, _controlPlaneDatabaseLabel, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            await WriteErrorAsync(context, "sql_error", ex.Message).ConfigureAwait(false);
            return;
        }

        if (!IsControlPlaneStatement(parsed) && parsed is not ShowDatabasesStatement)
        {
            metrics.RecordSqlError();
            RecordSlow(diagnostics, _controlPlaneDatabaseLabel, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            await WriteErrorAsync(context, "bad_request",
                "/v1/sql 仅支持控制面 SQL（CREATE USER / GRANT / CREATE DATABASE / SHOW USERS / SHOW DATABASES 等），数据面 SQL 请走 /v1/db/{db}/sql。").ConfigureAwait(false);
            return;
        }

        if (!TryAuthorizeStatement(
            context,
            parsed,
            canAdministerDatabase: false,
            isServerAdmin: isAdmin,
            out var authorizationError))
        {
            metrics.RecordSqlError();
            RecordSlow(diagnostics, _controlPlaneDatabaseLabel, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            await WriteErrorAsync(context, "forbidden", authorizationError).ConfigureAwait(false);
            return;
        }

        SqlStatement executable;
        try
        {
            executable = BindParameters(parsed, request);
        }
        catch (Exception ex)
        {
            metrics.RecordSqlError();
            RecordSlow(diagnostics, _controlPlaneDatabaseLabel, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            await WriteErrorAsync(context, "sql_error", ex.Message).ConfigureAwait(false);
            return;
        }

        object result;
        try
        {
            result = SqlExecutor.ExecuteControlPlaneStatement(executable, controlPlane);
        }
        catch (ControlPlaneAccessDeniedException ex)
        {
            metrics.RecordSqlError();
            RecordSlow(diagnostics, _controlPlaneDatabaseLabel, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            await WriteErrorAsync(context, "forbidden", ex.Message).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            metrics.RecordSqlError();
            RecordSlow(diagnostics, _controlPlaneDatabaseLabel, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            await WriteErrorAsync(context, "sql_error", ex.Message).ConfigureAwait(false);
            return;
        }

        if (result is SelectExecutionResult sel)
        {
            long rowCount = await WriteSelectAsync(context, sel, writerOptions).ConfigureAwait(false);
            metrics.AddReturnedRows(rowCount);
            var elapsed = sw.Elapsed.TotalMilliseconds;
            await WriteEndAsync(context, writerOptions, rowCount, recordsAffected: -1, elapsed).ConfigureAwait(false);
            RecordSlow(diagnostics, _controlPlaneDatabaseLabel, request.Sql, elapsed, rowCount, -1, failed: false);
        }
        else
        {
            var elapsed = sw.Elapsed.TotalMilliseconds;
            await WriteEndAsync(context, writerOptions, rowCount: 0, recordsAffected: 0, elapsed).ConfigureAwait(false);
            RecordSlow(diagnostics, _controlPlaneDatabaseLabel, request.Sql, elapsed, 0, 0, failed: false);
        }
    }

    private static async Task ExecuteAsync(
        HttpContext context,
        Tsdb tsdb,
        string databaseName,
        IReadOnlyList<SqlRequest> statements,
        ServerMetrics metrics,
        bool canWrite,
        bool canAdministerDatabase,
        bool isServerAdmin,
        IControlPlane? controlPlane,
        double queueWaitMs)
    {
        var diagnostics = context.RequestServices.GetService<SlowQueryDiagnostics>();
        ModbusWriteService? modbusWriteService = null;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        var writerOptions = new JsonWriterOptions { Indented = false, SkipValidation = false };
        SqlTransactionContext? transaction = null;
        var routineOptions = context.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value.SqlExecution;

        try
        {
        for (int s = 0; s < statements.Count; s++)
        {
            var stmt = statements[s];
            string diagnosticsSql = RedactSqlForDiagnostics(stmt.Sql);
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
                RecordSlow(diagnostics, databaseName, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                    queueWaitMs: queueWaitMs);
                await WriteErrorAsync(context, "sql_error", ex.Message).ConfigureAwait(false);
                return;
            }

            var diagnosticsDatabase = IsControlPlaneStatement(parsed) || parsed is ShowDatabasesStatement
                ? _controlPlaneDatabaseLabel
                : databaseName;

            if (!TryAuthorizeStatement(
                context,
                parsed,
                canAdministerDatabase,
                isServerAdmin,
                out var authorizationError))
            {
                metrics.RecordSqlError();
                RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                    queueWaitMs: queueWaitMs);
                await WriteErrorAsync(context, "forbidden", authorizationError).ConfigureAwait(false);
                return;
            }

            if (!IsControlPlaneStatement(parsed) && RequiresWritePermission(parsed) && !canWrite)
            {
                metrics.RecordSqlError();
                RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                    queueWaitMs: queueWaitMs);
                await WriteErrorAsync(context, "forbidden", "当前凭据对该数据库没有写权限。").ConfigureAwait(false);
                return;
            }

            SqlStatement executable;
            var executionMetrics = diagnostics is not null
                && diagnostics.Options.Enabled
                && diagnostics.Options.ThresholdMs >= 0
                    ? new SqlExecutionMetrics()
                    : null;
            SqlExecutionMetricsSnapshot? executionSnapshot = null;
            try
            {
                executable = BindParameters(parsed, stmt);

                if (executable is BeginTransactionStatement && transaction is not null && !transaction.IsCompleted)
                    throw new InvalidOperationException("当前已有活动轻事务，不能嵌套 BEGIN。");
                if (executable is WriteModbusStatement && transaction is not null && !transaction.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "WRITE MODBUS 不能在活动轻事务内执行；远端设备写入不属于本地关系表事务。");
                }

                string caller = ResolveRoutineCaller(context, isServerAdmin);
                object? result = executable switch
                {
                    WriteModbusStatement writeModbus => await GetModbusWriteService(
                        context,
                        ref modbusWriteService).ExecuteAsync(
                        tsdb,
                        databaseName,
                        writeModbus,
                        ResolveModbusWritePrincipal(context, caller),
                        canWrite,
                        canAdministerDatabase,
                        context.RequestAborted).ConfigureAwait(false),
                    ShowModbusWriteAuditStatement => GetModbusWriteService(
                        context,
                        ref modbusWriteService).ShowAudit(databaseName),
                    _ => SqlExecutor.ExecuteStatement(
                        tsdb,
                        databaseName,
                        executable,
                        controlPlane,
                        transaction,
                        new SqlExecutionOptions
                        {
                            CancellationToken = context.RequestAborted,
                            Caller = caller,
                            CanWrite = canWrite,
                            CanAdminister = canAdministerDatabase,
                            MaxRoutineStatements = routineOptions.MaxRoutineStatements,
                            MaxRoutineDepth = routineOptions.MaxRoutineDepth,
                            MaxRoutineResultRows = routineOptions.MaxRoutineResultRows,
                            Metrics = executionMetrics,
                        }),
                };
                executionSnapshot = executionMetrics?.Complete();
                if (result is SqlTransactionContext started)
                    transaction = started;
                else if (executable is CommitTransactionStatement or RollbackTransactionStatement)
                    transaction = null;

                switch (result)
                {
                    case SelectExecutionResult sel:
                        {
                            long rowCount = await WriteSelectAsync(context, sel, writerOptions).ConfigureAwait(false);
                            metrics.AddReturnedRows(rowCount);
                            var elapsed = sw.Elapsed.TotalMilliseconds;
                            await WriteEndAsync(context, writerOptions, rowCount, recordsAffected: -1, elapsed).ConfigureAwait(false);
                            RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, elapsed, rowCount, -1, failed: false,
                                executionSnapshot, queueWaitMs);
                            break;
                        }
                    case InsertExecutionResult ins:
                        {
                            if (!canWrite)
                            {
                                metrics.RecordSqlError();
                                RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                                    executionSnapshot, queueWaitMs);
                                await WriteErrorAsync(context, "forbidden", "INSERT 需要 readwrite 或 admin 角色。").ConfigureAwait(false);
                                return;
                            }
                            metrics.AddInsertedRows(ins.RowsInserted);
                            long rowCount = 0;
                            if (ins.Returning is { } returning)
                            {
                                rowCount = await WriteSelectAsync(context, returning, writerOptions).ConfigureAwait(false);
                                metrics.AddReturnedRows(rowCount);
                            }
                            var elapsed = sw.Elapsed.TotalMilliseconds;
                            await WriteEndAsync(context, writerOptions, rowCount, recordsAffected: ins.RowsInserted, elapsed).ConfigureAwait(false);
                            RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, elapsed, rowCount, ins.RowsInserted, failed: false,
                                executionSnapshot, queueWaitMs);
                            break;
                        }
                    case DeleteExecutionResult del:
                        {
                            if (!canWrite)
                            {
                                metrics.RecordSqlError();
                                RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                                    executionSnapshot, queueWaitMs);
                                await WriteErrorAsync(context, "forbidden", "DELETE 需要 readwrite 或 admin 角色。").ConfigureAwait(false);
                                return;
                            }
                            var elapsed = sw.Elapsed.TotalMilliseconds;
                            await WriteEndAsync(context, writerOptions, rowCount: 0, recordsAffected: del.TombstonesAdded, elapsed).ConfigureAwait(false);
                            RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, elapsed, 0, del.TombstonesAdded, failed: false,
                                executionSnapshot, queueWaitMs);
                            break;
                        }
                    case RowsAffectedExecutionResult affected:
                        {
                            if (!canWrite)
                            {
                                metrics.RecordSqlError();
                                RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                                    executionSnapshot, queueWaitMs);
                                await WriteErrorAsync(context, "forbidden", "该语句需要 readwrite 或 admin 角色。").ConfigureAwait(false);
                                return;
                            }
                            var elapsed = sw.Elapsed.TotalMilliseconds;
                            await WriteEndAsync(context, writerOptions, rowCount: 0, recordsAffected: affected.RowsAffected, elapsed).ConfigureAwait(false);
                            RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, elapsed, 0, affected.RowsAffected, failed: false,
                                executionSnapshot, queueWaitMs);
                            break;
                        }
                    default:
                        {
                            // CREATE MEASUREMENT、CREATE USER 等 DDL：返回受影响行数 0。
                            // 控制面语句已在上面按 admin-only / self-service 细分鉴权，这里仅校验需 canWrite 的普通 DDL。
                            if (!IsControlPlaneStatement(executable) && !canWrite)
                            {
                                metrics.RecordSqlError();
                                RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                                    executionSnapshot, queueWaitMs);
                                await WriteErrorAsync(context, "forbidden", "DDL 需要 readwrite 或 admin 角色。").ConfigureAwait(false);
                                return;
                            }
                            var elapsed = sw.Elapsed.TotalMilliseconds;
                            await WriteEndAsync(context, writerOptions, rowCount: 0, recordsAffected: 0, elapsed).ConfigureAwait(false);
                            RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, elapsed, 0, 0, failed: false,
                                executionSnapshot, queueWaitMs);
                            break;
                        }
                }
            }
            catch (ControlPlaneAccessDeniedException ex)
            {
                metrics.RecordSqlError();
                RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                    executionSnapshot ?? executionMetrics?.Complete(), queueWaitMs);
                await WriteErrorAsync(context, "forbidden", ex.Message).ConfigureAwait(false);
                return;
            }
            catch (TableConstraintException ex)
            {
                metrics.RecordSqlError();
                RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                    executionSnapshot ?? executionMetrics?.Complete(), queueWaitMs);
                await WriteErrorAsync(context, ex.ErrorCode, ex.Message).ConfigureAwait(false);
                return;
            }
            catch (RoutineExecutionException ex)
            {
                metrics.RecordSqlError();
                RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                    executionSnapshot ?? executionMetrics?.Complete(), queueWaitMs);
                await WriteErrorAsync(context, ex.Code, ex.Message).ConfigureAwait(false);
                return;
            }
            catch (ModbusWriteException ex)
            {
                metrics.RecordSqlError();
                RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                    executionSnapshot ?? executionMetrics?.Complete(), queueWaitMs);
                await WriteErrorAsync(context, ex.Code, ex.Message).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                metrics.RecordSqlError();
                RecordSlow(diagnostics, diagnosticsDatabase, diagnosticsSql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true,
                    executionSnapshot ?? executionMetrics?.Complete(), queueWaitMs);
                await WriteErrorAsync(context, "sql_error", ex.Message).ConfigureAwait(false);
                return;
            }
        }

        if (transaction is not null && !transaction.IsCompleted)
        {
            metrics.RecordSqlError();
            await WriteErrorAsync(context, "sql_error", "SQL batch 结束时仍有未提交的轻事务。").ConfigureAwait(false);
        }
        }
        finally
        {
            if (transaction is { IsCompleted: false })
                SqlExecutor.ExecuteStatement(tsdb, databaseName, new RollbackTransactionStatement(), null, transaction);
        }
    }

    private static SqlStatement BindParameters(SqlStatement statement, SqlRequest request)
    {
        if (request.Parameters is null || request.Parameters.Count == 0)
            return statement;

        var parameters = new SqlParameters();
        foreach (var pair in request.Parameters)
        {
            parameters.AddNamed(pair.Key, ToSqlParameterValue(pair.Key, pair.Value));
        }

        return SqlParameterBinder.Bind(statement, parameters);
    }

    private static object? ToSqlParameterValue(string name, JsonElementValue value)
        => value.Kind switch
        {
            ScalarKind.Null => null,
            ScalarKind.String => value.StringValue
                ?? throw new InvalidOperationException($"参数 '{name}' 缺少 stringValue。"),
            ScalarKind.Integer => value.IntegerValue
                ?? throw new InvalidOperationException($"参数 '{name}' 缺少 integerValue。"),
            ScalarKind.Double => value.DoubleValue
                ?? throw new InvalidOperationException($"参数 '{name}' 缺少 doubleValue。"),
            ScalarKind.Boolean => value.BooleanValue
                ?? throw new InvalidOperationException($"参数 '{name}' 缺少 booleanValue。"),
            _ => throw new InvalidOperationException($"参数 '{name}' 的类型 {value.Kind} 不受支持。"),
        };

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
                "forbidden" or RoutineErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
                "db_not_found" => StatusCodes.Status404NotFound,
                "unauthorized" => StatusCodes.Status401Unauthorized,
                "modbus_write_audit_unavailable" => StatusCodes.Status503ServiceUnavailable,
                "modbus_write_timeout" => StatusCodes.Status504GatewayTimeout,
                "modbus_write_connection_error" => StatusCodes.Status502BadGateway,
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

    /// <summary>
    /// 按当前数据库的 Admin 权限与服务端管理权限分别校验 Modbus DDL 和控制面 SQL。
    /// </summary>
    private static bool TryAuthorizeStatement(
        HttpContext context,
        SqlStatement statement,
        bool canAdministerDatabase,
        bool isServerAdmin,
        out string errorMessage)
    {
        if (IsAdminOnlyModbusStatement(statement) && !canAdministerDatabase)
        {
            errorMessage = "Modbus 基础设施定义、远端控制写和写审计需要当前数据库的 Admin 权限。";
            return false;
        }

        if (IsAdminOnlyControlPlaneStatement(statement) && !isServerAdmin)
        {
            errorMessage = "控制面 SQL（CREATE USER / GRANT / CREATE DATABASE / SHOW USERS 等）仅 admin 可执行。";
            return false;
        }

        if (IsSelfServiceControlPlaneStatement(statement) && !(isServerAdmin || HasSelfServiceControlPlaneAccess(context)))
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

    /// <summary>判别是否为仅 admin 可执行的 Modbus 数据面 DDL。</summary>
    private static bool IsAdminOnlyModbusStatement(SqlStatement statement) => statement is
        CreateModbusSourceStatement or
        CreateModbusEndpointStatement or
        CreateTableStatement { ModbusBinding: not null } or
        WriteModbusStatement or
        ShowModbusWriteAuditStatement;

    /// <summary>
    /// 判别是否为需要数据库写权限的数据面语句。
    /// </summary>
    internal static bool RequiresWritePermission(SqlStatement statement) => statement is not
        (SelectStatement or
        CallProcedureStatement or
        ShowMeasurementsStatement or
        ShowTablesStatement or
        ShowViewsStatement or
        ShowMaterializedViewsStatement or
        ShowProceduresStatement or
        ShowTriggersStatement or
        ShowTableIndexesStatement or
        ShowDocumentCollectionsStatement or
        ShowDocumentIndexesStatement or
        ShowFullTextIndexesStatement or
        ShowGraphsStatement or
        ShowPropertyGraphsStatement or
        ShowModbusSourcesStatement or
        ShowModbusEndpointsStatement or
        ShowModbusWriteAuditStatement or
        DescribeMeasurementStatement or
        DescribeTableStatement or
        DescribeViewStatement or
        DescribeMaterializedViewStatement or
        DescribeProcedureStatement or
        DescribeTriggerStatement or
        ExplainRoutineStatement or
        ShowRoutineDiagnosticsStatement or
        DescribeDocumentCollectionStatement or
        DescribeGraphStatement or
        DescribePropertyGraphStatement or
        DescribeModbusSourceStatement or
        DescribeModbusEndpointStatement or
        DescribeModbusTableStatement or
        ExplainStatement or
        ShowDatabasesStatement);

    private static string ResolveRoutineCaller(HttpContext context, bool isAdmin)
    {
        if (BearerAuthMiddleware.GetUser(context) is { } user)
            return user.UserName;
        return isAdmin ? "admin" : "remote";
    }

    private static string ResolveModbusWritePrincipal(HttpContext context, string caller)
    {
        if (BearerAuthMiddleware.GetUser(context) is not null)
            return caller;

        string authorization = context.Request.Headers.Authorization.ToString();
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(authorization));
        return "credential:" + Convert.ToHexString(digest.AsSpan(0, 16));
    }

    internal static string RedactSqlForDiagnostics(string sql)
    {
        int writeIndex = sql.IndexOf("WRITE", StringComparison.OrdinalIgnoreCase);
        if (writeIndex >= 0
            && sql.AsSpan(writeIndex + "WRITE".Length)
                .IndexOf("MODBUS", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "WRITE MODBUS <redacted>";
        }

        return sql;
    }

    private static ModbusWriteService GetModbusWriteService(
        HttpContext context,
        ref ModbusWriteService? service)
    {
        if (service is not null)
            return service;

        try
        {
            service = context.RequestServices.GetRequiredService<ModbusWriteService>();
            return service;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ModbusWriteException(
                "modbus_write_audit_unavailable",
                "Modbus 写审计无法加载；操作已失败关闭。",
                exception);
        }
    }

    internal static void RecordSlow(
        SlowQueryDiagnostics? diagnostics,
        string database,
        string sql,
        double elapsedMs,
        long rowCount,
        int recordsAffected,
        bool failed,
        SqlExecutionMetricsSnapshot? executionMetrics = null,
        double queueWaitMs = 0)
    {
        diagnostics?.Record(
            database,
            sql,
            elapsedMs,
            rowCount,
            recordsAffected,
            failed,
            executionMetrics,
            queueWaitMs);
    }
}
