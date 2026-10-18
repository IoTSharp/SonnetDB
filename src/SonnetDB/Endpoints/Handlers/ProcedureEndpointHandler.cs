using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Exceptions;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Protocol;
using SonnetDB.Routines;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Endpoints;

internal static class ProcedureEndpointHandler
{
    public static async Task HandleRestAsync(HttpContext context, string databaseName,
        TsdbRegistry registry, GrantsStore grants, ServerMetrics metrics)
    {
        try
        {
            if (context.Request.ContentLength > SqlProcedureFrameCodec.MaxJsonRequestBytes)
                throw new ProcedureRequestException(StatusCodes.Status413PayloadTooLarge, "request_too_large", "过程 JSON 请求超过编码字节预算。");
            byte[] encoded = await ReadBoundedBodyAsync(context).ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize(encoded, ServerJsonContext.Default.ProcedureJsonEnvelope);
            if (envelope is null || envelope.Version != 1)
                throw new ProcedureRequestException(400, "unsupported_procedure_version", "不支持的过程 JSON 信封版本。");
            var request = SqlProcedureFrameCodec.DecodeRequest(envelope.Payload);
            if (request.Db != databaseName)
                throw new ProcedureRequestException(400, "database_mismatch", "路径和过程请求数据库不一致。");
            byte[] json = await ExecuteAsync(context, request, registry, grants, metrics, rest: true).ConfigureAwait(false);
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(json, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var (status, code) = MapError(exception);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, new ErrorResponse(code, exception.Message),
                ServerJsonContext.Default.ErrorResponse, context.RequestAborted).ConfigureAwait(false);
        }
    }

    internal static async Task<byte[]> ExecuteAsync(HttpContext context, SqlProcedureFrameRequest request,
        TsdbRegistry registry, GrantsStore grants, ServerMetrics metrics, bool rest = false)
    {
        metrics.RecordSqlRequest();
        string version = context.Request.Headers[SqlFrameCodec.ResultVersionHeader].ToString();
        bool nativeDateTime2 = version == SqlFrameCodec.NativeDateTime2ResultVersion;
        if (version.Length != 0 && version is not (SqlFrameCodec.NativeDateTimeOffsetResultVersion or SqlFrameCodec.NativeDateTime2ResultVersion))
            throw new ProcedureRequestException(400, "unsupported_result_version", "过程不支持请求的 SQL 结果版本。");
        context.Response.Headers[SqlFrameCodec.ResultVersionHeader] = nativeDateTime2
            ? SqlFrameCodec.NativeDateTime2ResultVersion : SqlFrameCodec.NativeDateTimeOffsetResultVersion;
        if (WorkbenchPreviewSql.IsRequested(context))
            throw new ProcedureRequestException(403, "preview_action_denied", "Workbench Preview 尚未开放过程执行。");
        var access = SonnetDbEndpoints.EvaluateDatabaseAccess(context, registry, grants, request.Db,
            DatabasePermission.Read, out var database);
        if (access.Status != SonnetDbEndpoints.MqAccessStatus.Ok)
            throw new ProcedureRequestException(access.Status == SonnetDbEndpoints.MqAccessStatus.DbNotFound ? 404 : 403,
                access.Status == SonnetDbEndpoints.MqAccessStatus.DbNotFound ? "db_not_found" : "forbidden", access.Message);
        var permission = DatabaseAccessEvaluator.GetEffectivePermission(context, grants, request.Db);
        SqlConnectionSessions.LeaseHandle? connectionLease = null;
        string connectionSessionId = context.Request.Headers[SqlConnectionSessions.HeaderName].ToString();
        if (connectionSessionId.Length != 0)
        {
            if (!Guid.TryParseExact(connectionSessionId, "N", out _))
                throw new ProcedureRequestException(400, "connection_session_invalid", "远程 SQL 连接会话 ID 格式无效。");
            try
            {
                connectionLease = await context.RequestServices.GetRequiredService<SqlConnectionSessions>()
                    .AcquireAsync(connectionSessionId, database, request.Db,
                        context.Request.Headers.Authorization.ToString(), context.RequestAborted).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new ProcedureRequestException(503, "connection_session_busy",
                    "等待远程 SQL 连接会话准入超时。");
            }
            if (connectionLease is null)
                throw new ProcedureRequestException(404, "connection_session_missing", "远程 SQL 连接会话不存在或租约已过期。");
        }
        using var connectionSessionLease = connectionLease;
        using var admission = await context.RequestServices.GetRequiredService<SqlHttpRequestAdmission>()
            .AcquireAsync(request.Db, context.RequestAborted).ConfigureAwait(false);
        if (!admission.IsAcquired)
            throw new ProcedureRequestException(503, "sql_overloaded", "过程执行准入队列已满。");
        var resources = context.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value.SqlExecution;
        var options = new SqlExecutionOptions
        {
            CancellationToken = context.RequestAborted,
            SessionContext = connectionLease?.Context,
            Caller = context.User.Identity?.Name ?? "remote",
            CanWrite = DatabaseAccessEvaluator.HasPermission(permission, DatabasePermission.Write),
            CanAdminister = DatabaseAccessEvaluator.HasPermission(permission, DatabasePermission.Admin),
            MaxRoutineStatements = resources.MaxRoutineStatements,
            MaxRoutineDepth = resources.MaxRoutineDepth,
            MaxRoutineResultRows = resources.MaxRoutineResultRows,
            MaxTriggerTransitionRows = resources.MaxTriggerTransitionRows,
            MaxTriggerTransitionBytes = resources.MaxTriggerTransitionBytes,
            MaxDeferredTriggerInvocations = resources.MaxDeferredTriggerInvocations,
            MaxDeferredTriggerBytes = resources.MaxDeferredTriggerBytes,
            TransactionCommitTimeoutMilliseconds = resources.TransactionCommitTimeoutMilliseconds,
            AllowDateTime2Results = nativeDateTime2,
        };
        if (request.Parameters.Count(static parameter => parameter.Direction == SqlProcedureFrameDirection.ReturnValue) > 1)
            throw new ProcedureRequestException(400, "bad_request", "过程只能有一个返回值声明。");
        var arguments = request.Parameters.Where(static parameter => parameter.Direction != SqlProcedureFrameDirection.ReturnValue)
            .Select(static parameter => new ProcedureCallArgument(
            parameter.Value.Name, parameter.Value.Value, (SqlProcedureParameterDirection)parameter.Direction)).ToArray();
        var sessions = context.RequestServices.GetRequiredService<SqlTransactionSessions>();
        SqlTransactionSessions.Session? session = null;
        SqlTransactionContext? transaction = null;
        bool owned = request.SessionId.Length == 0;
        try
        {
            if (owned) transaction = new SqlTransactionContext();
            else
            {
                try
                {
                    session = await sessions.AcquireAsync(request.SessionId, database, request.Db,
                        context.Request.Headers.Authorization.ToString(), context.RequestAborted,
                        connectionLease?.Id).ConfigureAwait(false);
                }
                catch (SqlTransactionSessionBusyException)
                {
                    throw new ProcedureRequestException(503, "transaction_busy",
                        "等待远程轻事务会话准入超时。");
                }
                if (session is null || session.Terminal is not null)
                    throw new ProcedureRequestException(404, "transaction_missing", "调用方事务不存在、已结束或已过期。");
                transaction = session.Transaction;
            }
            byte[]? payload = null;
            _ = SqlExecutor.ExecuteProcedure(database, request.Name, arguments, transaction, options, request.Db,
                validateCompletion: completed =>
                {
                    foreach (var parameter in request.Parameters)
                    {
                        if (parameter.Direction == SqlProcedureFrameDirection.Input) continue;
                        object? value = parameter.Direction == SqlProcedureFrameDirection.ReturnValue
                            ? completed.ReturnValue : completed.OutputParameters[parameter.Value.Name];
                        _ = SqlProcedureFrameCodec.ConvertDeclaredValue(parameter, value);
                    }
                    payload = EncodeCompleted(completed, context.RequestAborted, nativeDateTime2);
                    if (rest)
                    {
                        payload = JsonSerializer.SerializeToUtf8Bytes(new ProcedureJsonEnvelope { Version = 1, Payload = payload },
                            ServerJsonContext.Default.ProcedureJsonEnvelope);
                        if (payload.Length > SqlProcedureFrameCodec.MaxJsonResponseBytes)
                            throw new InvalidOperationException("过程 JSON 响应超过编码预算。");
                    }
                });
            context.RequestAborted.ThrowIfCancellationRequested();
            if (owned)
                SqlExecutor.ExecuteStatement(database, request.Db, new CommitTransactionStatement(), null, transaction, options);
            return payload ?? throw new InvalidOperationException("过程没有完成响应。");
        }
        catch
        {
            metrics.RecordSqlError();
            if (owned && transaction is { IsCompleted: false })
                SqlExecutor.ExecuteStatement(database, request.Db, new RollbackTransactionStatement(), null,
                    transaction, SqlExecutionOptions.Default);
            throw;
        }
        finally { session?.Gate.Release(); }
    }

    private static byte[] EncodeCompleted(ProcedureExecutionResult completed, CancellationToken cancellationToken, bool nativeDateTime2)
    {
        var results = new List<byte[]>();
        int remaining = SqlProcedureFrameCodec.MaxResponseBytes;
        foreach (var result in completed.Results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = new BoundedWriter(remaining);
            var declarations = result.ColumnInfo ?? Enumerable.Repeat(new SelectColumnInfo(null) { IsDeclared = false }, result.Columns.Count).ToArray();
            SqlFrameCodec.EncodeQueryMetaFrame(buffer, 1, result.Columns, declarations,
                fullDeclarations: true, nativeDateTime: true, nativeDuration: true, nativeDateTimeOffset: true, nativeDateTime2: nativeDateTime2);
            int start = 0;
            for (int chunk = 0; start < result.Rows.Count && chunk < 100_000; chunk++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = SqlFrameCodec.SelectChunkRowCount(result.Rows, start);
                SqlFrameCodec.EncodeQueryRowsFrame(buffer, 1, result.Rows, start, count, result.Columns.Count,
                    exactDecimal: true, nativeDateTime: true, nativeDuration: true, nativeDateTimeOffset: true);
                start += count;
            }
            if (start != result.Rows.Count) throw new InvalidOperationException("过程结果分帧超过次数上限。");
            SqlFrameCodec.EncodeQueryEndFrame(buffer, 1, result.Rows.Count, 0);
            byte[] bytes = buffer.Bytes;
            remaining -= bytes.Length;
            results.Add(bytes);
        }
        var outputs = completed.OutputParameters.Select(static value => SqlExecuteFrameCodec.FromClr(value.Key, value.Value)).ToArray();
        return SqlProcedureFrameCodec.EncodeResponsePayload(new SqlProcedureFrameResponse(results, completed.RecordsAffected,
            completed.ReturnValue, outputs));
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(HttpContext context)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var buffer = new MemoryStream();
        byte[] block = new byte[8192];
        for (int count = 0; count <= SqlProcedureFrameCodec.MaxJsonRequestBytes; count++)
        {
            int read = await context.Request.Body.ReadAsync(block, deadline.Token).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > SqlProcedureFrameCodec.MaxJsonRequestBytes)
                throw new ProcedureRequestException(413, "request_too_large", "过程 JSON 请求超过编码字节预算。");
            buffer.Write(block, 0, read);
        }
        throw new ProcedureRequestException(413, "request_too_large", "过程请求读取次数超过上限。");
    }

    internal static (int Status, string Code) MapError(Exception exception) => exception switch
    {
        ProcedureRequestException request => (request.Status, request.Code),
        RoutineExecutionException routine => (routine.Code == RoutineErrorCodes.Forbidden ? 403 : 400, routine.Code),
        FrameFormatException or JsonException or ArgumentException => (400, "bad_request"),
        _ => (400, "procedure_error"),
    };

    private sealed class ProcedureRequestException(int status, string code, string message) : Exception(message)
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
    }

    private sealed class BoundedWriter(int maximum) : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        public byte[] Bytes => _buffer.WrittenSpan.ToArray();
        public void Advance(int count)
        {
            if (count < 0 || count > maximum - _buffer.WrittenCount) throw new InvalidOperationException("过程结果超过字节预算。");
            _buffer.Advance(count);
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Check(sizeHint);
            return _buffer.GetMemory(sizeHint);
        }
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Check(sizeHint);
            return _buffer.GetSpan(sizeHint);
        }
        private void Check(int sizeHint)
        {
            if (sizeHint < 0 || Math.Max(1, sizeHint) > maximum - _buffer.WrittenCount)
                throw new InvalidOperationException("过程结果超过字节预算；未提交自有事务。");
        }
    }
}
