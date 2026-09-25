using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Kv;
using SonnetDB.Mcp;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetMQ;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapSqlEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var users = app.Services.GetRequiredService<UserStore>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var metrics = app.Services.GetRequiredService<ServerMetrics>();
        var admission = app.Services.GetRequiredService<SqlHttpRequestAdmission>();

        // ---- SQL ----
        var controlPlane = app.Services.GetRequiredService<SonnetDB.Sql.Execution.IControlPlane>();
        app.MapPost("/v1/db/{db}/sql", async (HttpContext ctx, string db) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var admissionResult = await TryAcquireSqlHttpAdmissionAsync(ctx, admission, db).ConfigureAwait(false);
            using var admissionLease = admissionResult.Lease;
            if (admissionLease is null)
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.SqlRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }
            var scopedControlPlane = CreateScopedControlPlane(ctx, controlPlane, users, grants, registry);
            bool canWrite = DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Write);
            bool canAdministerDatabase = DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Admin);
            bool isServerAdmin = DatabaseAccessEvaluator.IsServerAdmin(ctx);
            await SqlEndpointHandler.HandleSingleAsync(ctx, tsdb, db, req, metrics,
                canWrite,
                canAdministerDatabase,
                isServerAdmin,
                scopedControlPlane,
                admissionResult.QueueWaitMs).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/sql/batch", async (HttpContext ctx, string db) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var admissionResult = await TryAcquireSqlHttpAdmissionAsync(ctx, admission, db).ConfigureAwait(false);
            using var admissionLease = admissionResult.Lease;
            if (admissionLease is null)
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.SqlBatchRequest).ConfigureAwait(false);
            if (req is null || req.Statements.Count == 0)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体或 statements 不可为空。").ConfigureAwait(false);
                return;
            }
            var scopedControlPlane = CreateScopedControlPlane(ctx, controlPlane, users, grants, registry);
            bool canWrite = DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Write);
            bool canAdministerDatabase = DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Admin);
            bool isServerAdmin = DatabaseAccessEvaluator.IsServerAdmin(ctx);
            await SqlEndpointHandler.HandleBatchAsync(ctx, tsdb, db, req, metrics,
                canWrite,
                canAdministerDatabase,
                isServerAdmin,
                scopedControlPlane,
                admissionResult.QueueWaitMs).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/sql/transactions", async (HttpContext ctx, string db) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            var permission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, permission, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var sessions = ctx.RequestServices.GetRequiredService<SqlTransactionSessions>();
            if (!sessions.TryCreate(tsdb, db, ctx.Request.Headers.Authorization.ToString(), out var session))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable,
                    "transaction_limit", "活动远程轻事务数已达上限。").ConfigureAwait(false);
                return;
            }
            ctx.Response.ContentType = "application/json";
            await using (var writer = new Utf8JsonWriter(ctx.Response.BodyWriter))
            {
                writer.WriteStartObject();
                writer.WriteString("id", session!.Id);
                writer.WriteEndObject();
            }
            await ctx.Response.BodyWriter.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/sql/transactions/{id}/sql",
            (HttpContext ctx, string db, string id) => HandleTransactionRequestAsync(ctx, db, id, "sql"));
        app.MapPost("/v1/db/{db}/sql/transactions/{id}/commit",
            (HttpContext ctx, string db, string id) => HandleTransactionRequestAsync(ctx, db, id, "commit"));
        app.MapPost("/v1/db/{db}/sql/transactions/{id}/rollback",
            (HttpContext ctx, string db, string id) => HandleTransactionRequestAsync(ctx, db, id, "rollback"));
        app.MapGet("/v1/db/{db}/sql/transactions/{id}",
            (HttpContext ctx, string db, string id) => HandleTransactionRequestAsync(ctx, db, id, "status"));
    }

    private static async Task HandleTransactionRequestAsync(HttpContext ctx, string db, string id, string action)
    {
        var services = ctx.RequestServices;
        var registry = services.GetRequiredService<TsdbRegistry>();
        if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
            return;
        var grants = services.GetRequiredService<GrantsStore>();
        var permission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
        if (!await TryRequireDatabasePermissionAsync(ctx, db, permission, DatabasePermission.Read).ConfigureAwait(false))
            return;
        var admission = await TryAcquireSqlHttpAdmissionAsync(
            ctx, services.GetRequiredService<SqlHttpRequestAdmission>(), db).ConfigureAwait(false);
        using var admissionLease = admission.Lease;
        if (admissionLease is null)
            return;

        var sessions = services.GetRequiredService<SqlTransactionSessions>();
        var session = await sessions.AcquireAsync(
            id, tsdb, db, ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted).ConfigureAwait(false);
        if (session is null)
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound,
                "transaction_missing", "远程轻事务不存在或租约已过期。").ConfigureAwait(false);
            return;
        }

        try
        {
            if (action == "status")
            {
                ctx.Response.ContentType = "application/json";
                await using (var writer = new Utf8JsonWriter(ctx.Response.BodyWriter))
                {
                    writer.WriteStartObject();
                    writer.WriteString("state", session.Terminal ?? "active");
                    writer.WriteEndObject();
                }
                await ctx.Response.BodyWriter.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                return;
            }

            if (session.Terminal is not null)
            {
                if (action == session.Terminal)
                    ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                else
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict,
                        "transaction_completed", "远程轻事务已经结束。").ConfigureAwait(false);
                return;
            }

            SqlRequest request;
            if (action == "sql")
            {
                var submitted = await ReadJsonAsync(ctx, ServerJsonContext.Default.SqlRequest).ConfigureAwait(false);
                if (submitted is null)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest,
                        "bad_request", "SQL 请求体不可为空。").ConfigureAwait(false);
                    return;
                }
                SqlStatement parsed;
                try { parsed = SqlParser.Parse(submitted.Sql); }
                catch (Exception ex)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest,
                        "sql_error", ex.Message).ConfigureAwait(false);
                    return;
                }
                if (parsed is BeginTransactionStatement or CommitTransactionStatement or RollbackTransactionStatement
                    || SqlEndpointHandler.IsControlPlaneStatement(parsed))
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest,
                        "bad_request", "事务会话只接受数据面 SQL，提交和回滚使用专用端点。").ConfigureAwait(false);
                    return;
                }
                request = submitted;
            }
            else
            {
                request = new SqlRequest(action == "commit" ? "COMMIT" : "ROLLBACK");
            }

            var controlPlane = services.GetRequiredService<SonnetDB.Sql.Execution.IControlPlane>();
            await SqlEndpointHandler.HandleSessionStatementAsync(
                ctx, tsdb, db, request, services.GetRequiredService<ServerMetrics>(),
                DatabaseAccessEvaluator.HasPermission(permission, DatabasePermission.Write),
                DatabaseAccessEvaluator.HasPermission(permission, DatabasePermission.Admin),
                DatabaseAccessEvaluator.IsServerAdmin(ctx),
                CreateScopedControlPlane(ctx, controlPlane,
                    services.GetRequiredService<UserStore>(), grants, registry),
                admission.QueueWaitMs, session.Transaction).ConfigureAwait(false);
            if (action != "sql" && session.Transaction.IsCompleted)
                sessions.Complete(session, session.Transaction.WasCommitted ? "commit" : "rollback");
        }
        finally
        {
            if (action != "sql" && session.Transaction.IsCompleted && session.Terminal is null)
                sessions.Complete(session, session.Transaction.WasCommitted ? "commit" : "rollback");
            session.Gate.Release();
        }
    }

    private static async Task<SqlAdmissionResult> TryAcquireSqlHttpAdmissionAsync(
        HttpContext ctx,
        SqlHttpRequestAdmission admission,
        string database)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        RateLimitLease lease;
        try
        {
            lease = await admission.AcquireAsync(database, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return new SqlAdmissionResult(null, Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
        }

        if (lease.IsAcquired)
        {
            return new SqlAdmissionResult(
                lease,
                Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
        }

        lease.Dispose();
        ctx.Response.Headers["Retry-After"] = SqlHttpRequestAdmission.RetryAfterSeconds
            .ToString(CultureInfo.InvariantCulture);
        await WriteSimpleErrorAsync(
            ctx,
            StatusCodes.Status503ServiceUnavailable,
            "sql_overloaded",
            $"数据库 '{database}' 的 SQL 请求已达到并发与等待队列上限，请稍后重试。").ConfigureAwait(false);
        return new SqlAdmissionResult(null, Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
    }

    private readonly record struct SqlAdmissionResult(RateLimitLease? Lease, double QueueWaitMs);
}
