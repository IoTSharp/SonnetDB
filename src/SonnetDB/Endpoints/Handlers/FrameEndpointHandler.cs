using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Ingest;
using SonnetDB.Json;
using SonnetDB.Kv;
using SonnetDB.ObjectStorage;
using SonnetDB.Protocol;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetMQ;

namespace SonnetDB.Endpoints;

/// <summary>
/// 通用二进制帧端点处理器（M28 P5b #235；#237 挂载 tsdb service；#238 挂载 sql service；
/// #239 挂载 vector service；#240 挂载 kv / object / doc service——七个 service 全部就位）。
/// 请求体 = 1..N 个请求帧，逐帧解析、鉴权、分发到引擎、逐帧写回响应帧（streamId 回显）。
/// sql 查询与 vector 检索响应为同 streamId 的流式帧序列（meta → rows × N → end），
/// object get 响应为 meta → data × N → end，均逐块 flush。
/// 错误模型：未成帧（错 Content-Type / 首帧畸形 / 空体）走 HTTP 状态码；
/// 成帧后一切按帧回错误帧（HTTP 200），批内单帧失败不影响其余帧。
/// </summary>
internal static class FrameEndpointHandler
{
    internal const string ContentType = "application/x-sonnetdb-frame";

    public static async Task HandleAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        SonnetMqStore mqStore,
        ServerMetrics metrics)
    {
        if (!IsFrameContentType(ctx.Request.ContentType))
        {
            await WriteJsonErrorAsync(ctx, StatusCodes.Status415UnsupportedMediaType, "bad_request",
                $"帧端点要求 Content-Type '{ContentType}'。").ConfigureAwait(false);
            return;
        }

        PipeReader reader = ctx.Request.BodyReader;
        PipeWriter writer = ctx.Response.BodyWriter;
        int frameCount = 0;

        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(ctx.RequestAborted).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (true)
                {
                    FrameHeader header;
                    ReadOnlySequence<byte> payload;
                    try
                    {
                        if (!FrameCodec.TryReadFrame(ref buffer, out header, out payload))
                            break;
                    }
                    catch (FrameFormatException ex)
                    {
                        // 帧边界不可恢复（声明长度超限），无法继续解析后续字节
                        await RespondFramingErrorAsync(ctx, writer, frameCount, "bad_frame", ex.Message).ConfigureAwait(false);
                        reader.AdvanceTo(buffer.End);
                        return;
                    }

                    frameCount++;
                    string? envelopeError = ValidateEnvelope(in header, out string envelopeErrorCode);
                    if (envelopeError is not null)
                    {
                        if (frameCount == 1 && !ctx.Response.HasStarted)
                        {
                            await WriteJsonErrorAsync(ctx, StatusCodes.Status400BadRequest, envelopeErrorCode, envelopeError).ConfigureAwait(false);
                            reader.AdvanceTo(buffer.End);
                            return;
                        }

                        EnsureFrameResponseStarted(ctx);
                        FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, envelopeErrorCode, envelopeError);
                        await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                        continue;
                    }

                    EnsureFrameResponseStarted(ctx);
                    if (header.Service == (byte)FrameService.Sql)
                        await ExecuteSqlQueryAsync(ctx, registry, grants, metrics, writer, header, payload).ConfigureAwait(false);
                    else if (header.Service == (byte)FrameService.Vector)
                        await ExecuteVectorSearchAsync(ctx, registry, grants, metrics, writer, header, payload).ConfigureAwait(false);
                    else if (header.Service == (byte)FrameService.Object)
                        await ExecuteObjectOpAsync(ctx, registry, grants, writer, header, payload).ConfigureAwait(false);
                    else
                        DispatchFrame(ctx, registry, grants, mqStore, metrics, writer, header, payload);
                    await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                }

                if (result.IsCompleted)
                {
                    if (buffer.Length > 0)
                    {
                        // 尾部残帧：能解析出帧头就回显其 streamId，否则用 0
                        uint streamId = 0;
                        byte service = 0, op = 0;
                        if (buffer.Length >= FrameHeader.Size)
                        {
                            Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
                            buffer.Slice(0, FrameHeader.Size).CopyTo(headerBytes);
                            if (FrameHeader.TryRead(headerBytes, out FrameHeader partial))
                            {
                                streamId = partial.StreamId;
                                service = partial.Service;
                                op = partial.Op;
                            }
                        }

                        if (frameCount == 0 && !ctx.Response.HasStarted)
                        {
                            await WriteJsonErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_frame",
                                "请求体包含不完整的帧。").ConfigureAwait(false);
                        }
                        else
                        {
                            EnsureFrameResponseStarted(ctx);
                            FrameCodec.WriteErrorFrame(writer, service, op, streamId, "bad_frame", "请求体尾部包含不完整的帧。");
                            await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                        }
                    }
                    else if (frameCount == 0)
                    {
                        await WriteJsonErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                            "帧端点请求体为空。").ConfigureAwait(false);
                    }

                    reader.AdvanceTo(buffer.End);
                    return;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // 客户端断开，静默结束（同 SseEndpointHandler）
        }
    }

    private static bool IsFrameContentType(string? contentType)
        => contentType is not null &&
           contentType.AsSpan().TrimStart().StartsWith(ContentType, StringComparison.OrdinalIgnoreCase);

    private static void EnsureFrameResponseStarted(HttpContext ctx)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = ContentType;
        }
    }

    /// <summary>
    /// 帧信封语义校验；合法返回 null，否则返回错误消息并输出错误码。
    /// </summary>
    private static string? ValidateEnvelope(in FrameHeader header, out string errorCode)
    {
        if (header.Version != FrameHeader.CurrentVersion)
        {
            errorCode = "unsupported_version";
            return $"不支持的帧协议版本 {header.Version}（当前 {FrameHeader.CurrentVersion}）。";
        }

        if ((header.Flags & ~(byte)(FrameFlags.Response | FrameFlags.Error)) != 0)
        {
            errorCode = "bad_frame";
            return $"帧 Flags 0x{header.Flags:X2} 含 v1 保留位。";
        }

        if (header.IsResponse || header.IsError)
        {
            errorCode = "bad_frame";
            return "请求帧不得设置 Response/Error 标志。";
        }

        if (header.Service == (byte)FrameService.Mq)
        {
            if (header.Op is < (byte)MqFrameOp.Publish or > (byte)MqFrameOp.Ack)
            {
                errorCode = "unsupported_op";
                return $"mq service 不支持 op {header.Op}。";
            }
        }
        else if (header.Service == (byte)FrameService.Tsdb)
        {
            if (header.Op != (byte)TsdbFrameOp.WriteColumnar)
            {
                errorCode = "unsupported_op";
                return $"tsdb service 不支持 op {header.Op}。";
            }
        }
        else if (header.Service == (byte)FrameService.Sql)
        {
            if (header.Op != (byte)SqlFrameOp.Query)
            {
                errorCode = "unsupported_op";
                return $"sql service 不支持 op {header.Op}。";
            }
        }
        else if (header.Service == (byte)FrameService.Vector)
        {
            if (header.Op != (byte)VectorFrameOp.Search)
            {
                errorCode = "unsupported_op";
                return $"vector service 不支持 op {header.Op}。";
            }
        }
        else if (header.Service == (byte)FrameService.Kv)
        {
            if (header.Op is < (byte)KvFrameOp.Get or > (byte)KvFrameOp.Scan)
            {
                errorCode = "unsupported_op";
                return $"kv service 不支持 op {header.Op}。";
            }
        }
        else if (header.Service == (byte)FrameService.Object)
        {
            if (header.Op is < (byte)ObjectFrameOp.Get or > (byte)ObjectFrameOp.Put)
            {
                errorCode = "unsupported_op";
                return $"object service 不支持 op {header.Op}。";
            }
        }
        else if (header.Service == (byte)FrameService.Doc)
        {
            if (header.Op is < (byte)DocFrameOp.Find or > (byte)DocFrameOp.Insert)
            {
                errorCode = "unsupported_op";
                return $"doc service 不支持 op {header.Op}。";
            }
        }
        else
        {
            errorCode = "unsupported_service";
            return $"service {header.Service} 未定义（mq=1、tsdb=2、sql=3、vector=4、kv=5、object=6、doc=7）。";
        }

        errorCode = string.Empty;
        return null;
    }

    private static void DispatchFrame(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        SonnetMqStore mqStore,
        ServerMetrics metrics,
        PipeWriter writer,
        FrameHeader header,
        ReadOnlySequence<byte> payload)
    {
        byte[]? rented = null;
        try
        {
            ReadOnlyMemory<byte> payloadMemory;
            if (payload.IsSingleSegment)
            {
                payloadMemory = payload.First;
            }
            else
            {
                rented = ArrayPool<byte>.Shared.Rent((int)payload.Length);
                payload.CopyTo(rented);
                payloadMemory = rented.AsMemory(0, (int)payload.Length);
            }

            if (header.Service == (byte)FrameService.Tsdb)
                ExecuteTsdbOp(ctx, registry, grants, metrics, writer, header, payloadMemory);
            else if (header.Service == (byte)FrameService.Kv)
                ExecuteKvOp(ctx, registry, grants, writer, header, payloadMemory);
            else if (header.Service == (byte)FrameService.Doc)
                ExecuteDocOp(ctx, registry, grants, writer, header, payloadMemory);
            else
                ExecuteMqOp(ctx, registry, grants, mqStore, writer, header, payloadMemory);
        }
        catch (FrameFormatException ex)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_frame", ex.Message);
        }
        catch (BulkIngestException ex)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bulk_ingest_error", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // SpanReader underflow 等结构性解码失败
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_frame", ex.Message);
        }
        catch (ArgumentException ex)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_request", ex.Message);
        }
        catch (IOException ex)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "mq_io_error", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "mq_error", ex.Message);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// 执行 sql query 帧（#238）：解码 → 鉴权 → 参数绑定 → 只读校验 → 执行 →
    /// meta / rows / end 逐帧流式回写（rows 帧按 <see cref="SqlFrameCodec.SelectChunkRowCount"/>
    /// 切块并逐块 flush，响应缓冲内存上界 = 单块）。指标与慢查询事件与 REST NDJSON 端点对齐。
    /// 帧通道只承载数据面只读语句（SELECT / SHOW / DESCRIBE / EXPLAIN）——写语句与控制面 SQL 回
    /// bad_request 引导走 REST。
    /// </summary>
    private static async Task ExecuteSqlQueryAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        ServerMetrics metrics,
        PipeWriter writer,
        FrameHeader header,
        ReadOnlySequence<byte> payload)
    {
        metrics.RecordSqlRequest();
        var sw = Stopwatch.StartNew();
        var broadcaster = ctx.RequestServices.GetService<EventBroadcaster>();
        var options = ctx.RequestServices.GetService<IOptions<ServerOptions>>()?.Value;

        SqlQueryFrameRequest request;
        try
        {
            // 解码（payload 是输入缓冲的零拷贝视图，鉴权/执行前同步消费完毕）
            byte[]? rented = null;
            try
            {
                ReadOnlyMemory<byte> payloadMemory;
                if (payload.IsSingleSegment)
                {
                    payloadMemory = payload.First;
                }
                else
                {
                    rented = ArrayPool<byte>.Shared.Rent((int)payload.Length);
                    payload.CopyTo(rented);
                    payloadMemory = rented.AsMemory(0, (int)payload.Length);
                }

                request = SqlFrameCodec.DecodeQueryRequest(payloadMemory.Span);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception ex) when (ex is FrameFormatException or InvalidOperationException)
        {
            metrics.RecordSqlError();
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_frame", ex.Message);
            return;
        }

        try
        {
            SonnetDbEndpoints.MqAccessResult access = SonnetDbEndpoints.EvaluateDatabaseAccess(
                ctx, registry, grants, request.Db, DatabasePermission.Read, out Tsdb tsdb);
            if (access.Status != SonnetDbEndpoints.MqAccessStatus.Ok)
            {
                metrics.RecordSqlError();
                string code = access.Status switch
                {
                    SonnetDbEndpoints.MqAccessStatus.DbNotFound => "db_not_found",
                    SonnetDbEndpoints.MqAccessStatus.Forbidden => "forbidden",
                    _ => "bad_request",
                };
                FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, code, access.Message);
                return;
            }

            SqlStatement parsed = SqlParser.Parse(request.Sql);
            parsed = SqlParameterBinder.Bind(parsed, request.Parameters);

            if (SqlEndpointHandler.IsControlPlaneStatement(parsed) || parsed is ShowDatabasesStatement)
            {
                metrics.RecordSqlError();
                FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_request",
                    "帧通道不承载控制面 SQL；请走 REST /v1/sql 或 /v1/db/{db}/sql。");
                return;
            }

            if (SqlEndpointHandler.RequiresWritePermission(parsed))
            {
                metrics.RecordSqlError();
                FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_request",
                    "sql query 帧仅支持只读语句（SELECT / SHOW / DESCRIBE / EXPLAIN）；写语句请走 REST SQL 端点或 tsdb 列式写帧。");
                return;
            }

            object? result = SqlExecutor.ExecuteStatement(tsdb, request.Db, parsed, controlPlane: null);
            if (result is not SelectExecutionResult select)
            {
                metrics.RecordSqlError();
                FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "sql_error",
                    "语句未产生结果集。");
                return;
            }

            // 流式回写：meta → rows × N（逐块 flush）→ end。执行本身是同步物化（引擎契约），
            // 分块编码把峰值响应缓冲压到单块，行数大时客户端可增量消费。
            SqlFrameCodec.EncodeQueryMetaFrame(writer, header.StreamId, select.Columns);
            await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);

            int position = 0;
            while (position < select.Rows.Count)
            {
                int chunkRows = SqlFrameCodec.SelectChunkRowCount(select.Rows, position);
                SqlFrameCodec.EncodeQueryRowsFrame(writer, header.StreamId, select.Rows, position, chunkRows, select.Columns.Count);
                position += chunkRows;
                await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }

            double elapsed = sw.Elapsed.TotalMilliseconds;
            SqlFrameCodec.EncodeQueryEndFrame(writer, header.StreamId, select.Rows.Count, elapsed);
            metrics.AddReturnedRows(select.Rows.Count);
            SqlEndpointHandler.MaybePublishSlow(broadcaster, options, request.Db, request.Sql, elapsed, select.Rows.Count, -1, failed: false);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordSqlError();
            SqlEndpointHandler.MaybePublishSlow(broadcaster, options, request.Db, request.Sql, sw.Elapsed.TotalMilliseconds, 0, 0, failed: true);
            // meta/rows 帧可能已写出：错误帧同 streamId 追加，客户端按「end 前收到错误帧」终止该查询
            string code = ex is ArgumentException ? "bad_request" : "sql_error";
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, code, ex.Message);
        }
    }

    /// <summary>
    /// 执行 vector search 帧（#239）：解码（查询向量 f32 二进制）→ 鉴权（Read）→
    /// 复用 SQL knn TVF 的同一检索内核（<see cref="TableValuedFunctionExecutor.ExecuteKnnSearch"/>）→
    /// meta / rows / end 逐帧流式回写（块布局与 sql service 一致，帧头 service/op 为 vector/search，
    /// 复用 #238 的切块与逐块 flush——响应缓冲内存上界 = 单块）。
    /// </summary>
    private static async Task ExecuteVectorSearchAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        ServerMetrics metrics,
        PipeWriter writer,
        FrameHeader header,
        ReadOnlySequence<byte> payload)
    {
        var sw = Stopwatch.StartNew();

        VectorSearchFrameRequest request;
        try
        {
            byte[]? rented = null;
            try
            {
                ReadOnlyMemory<byte> payloadMemory;
                if (payload.IsSingleSegment)
                {
                    payloadMemory = payload.First;
                }
                else
                {
                    rented = ArrayPool<byte>.Shared.Rent((int)payload.Length);
                    payload.CopyTo(rented);
                    payloadMemory = rented.AsMemory(0, (int)payload.Length);
                }

                request = VectorFrameCodec.DecodeSearchRequest(payloadMemory.Span);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception ex) when (ex is FrameFormatException or InvalidOperationException)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_frame", ex.Message);
            return;
        }

        try
        {
            SonnetDbEndpoints.MqAccessResult access = SonnetDbEndpoints.EvaluateDatabaseAccess(
                ctx, registry, grants, request.Db, DatabasePermission.Read, out Tsdb tsdb);
            if (access.Status != SonnetDbEndpoints.MqAccessStatus.Ok)
            {
                string code = access.Status switch
                {
                    SonnetDbEndpoints.MqAccessStatus.DbNotFound => "db_not_found",
                    SonnetDbEndpoints.MqAccessStatus.Forbidden => "forbidden",
                    _ => "bad_request",
                };
                FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, code, access.Message);
                return;
            }

            SelectExecutionResult select = TableValuedFunctionExecutor.ExecuteKnnSearch(
                tsdb, request.Measurement, request.Column, request.QueryVector,
                request.K, request.Metric, request.TagFilter, request.TimeRange);

            VectorFrameCodec.EncodeSearchMetaFrame(writer, header.StreamId, select.Columns);
            await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);

            int position = 0;
            while (position < select.Rows.Count)
            {
                int chunkRows = SqlFrameCodec.SelectChunkRowCount(select.Rows, position);
                VectorFrameCodec.EncodeSearchRowsFrame(writer, header.StreamId, select.Rows, position, chunkRows, select.Columns.Count);
                position += chunkRows;
                await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }

            VectorFrameCodec.EncodeSearchEndFrame(writer, header.StreamId, select.Rows.Count, sw.Elapsed.TotalMilliseconds);
            metrics.AddReturnedRows(select.Rows.Count);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // meta/rows 帧可能已写出：错误帧同 streamId 追加，客户端按「end 前收到错误帧」终止该查询
            string code = ex is ArgumentException ? "bad_request" : "vector_search_error";
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, code, ex.Message);
        }
    }

    private static void ExecuteTsdbOp(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        ServerMetrics metrics,
        PipeWriter writer,
        FrameHeader header,
        ReadOnlyMemory<byte> payload)
    {
        TsdbWriteColumnarFrameRequest request = TsdbFrameCodec.DecodeWriteColumnarRequest(payload);
        SonnetDbEndpoints.MqAccessResult access = SonnetDbEndpoints.EvaluateDatabaseAccess(
            ctx, registry, grants, request.Db, DatabasePermission.Write, out Tsdb tsdb);
        if (access.Status != SonnetDbEndpoints.MqAccessStatus.Ok)
        {
            string code = access.Status switch
            {
                SonnetDbEndpoints.MqAccessStatus.DbNotFound => "db_not_found",
                SonnetDbEndpoints.MqAccessStatus.Forbidden => "forbidden",
                _ => "bad_request",
            };
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, code, access.Message);
            return;
        }

        var reader = new TsdbColumnarPointReader(request);
        BulkIngestResult result = BulkIngestor.Ingest(tsdb, reader, BulkErrorPolicy.FailFast, request.FlushMode);
        metrics.AddInsertedRows(result.Written);
        TsdbFrameCodec.EncodeWriteColumnarResponse(writer, header.StreamId, result.Written);
    }

    private static void ExecuteMqOp(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        SonnetMqStore mqStore,
        PipeWriter writer,
        FrameHeader header,
        ReadOnlyMemory<byte> payload)
    {
        switch ((MqFrameOp)header.Op)
        {
            case MqFrameOp.Publish:
            {
                MqPublishFrameRequest request = MqFrameCodec.DecodePublishRequest(payload);
                if (!TryAuthorize(ctx, registry, grants, writer, header, request.Db, request.Topic, DatabasePermission.Write))
                    return;
                long offset = mqStore.Publish(
                    SonnetDbEndpoints.QualifyMqTopic(request.Db, request.Topic),
                    request.Payload.Span,
                    request.Headers.Count == 0 ? null : new SonnetMqPublishOptions(request.Headers));
                MqFrameCodec.EncodePublishResponse(writer, header.StreamId, offset);
                return;
            }

            case MqFrameOp.PublishBatch:
            {
                MqPublishBatchFrameRequest request = MqFrameCodec.DecodePublishBatchRequest(payload);
                if (!TryAuthorize(ctx, registry, grants, writer, header, request.Db, request.Topic, DatabasePermission.Write))
                    return;
                IReadOnlyList<long> offsets = mqStore.PublishMany(
                    SonnetDbEndpoints.QualifyMqTopic(request.Db, request.Topic), request.Entries);
                MqFrameCodec.EncodePublishBatchResponse(writer, header.StreamId, offsets);
                return;
            }

            case MqFrameOp.Pull:
            {
                MqPullFrameRequest request = MqFrameCodec.DecodePullRequest(payload);
                if (!TryAuthorize(ctx, registry, grants, writer, header, request.Db, request.Topic, DatabasePermission.Read))
                    return;
                if (string.IsNullOrWhiteSpace(request.ConsumerGroup))
                {
                    FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_request", "pull 需包含 consumerGroup。");
                    return;
                }

                int maxCount = request.MaxCount <= 0 ? 100 : Math.Min(request.MaxCount, 1000);
                IReadOnlyList<SonnetMqMessage> messages = mqStore.Pull(
                    SonnetDbEndpoints.QualifyMqTopic(request.Db, request.Topic), request.ConsumerGroup, maxCount);
                MqFrameCodec.EncodePullResponse(writer, header.StreamId, messages);
                return;
            }

            case MqFrameOp.Ack:
            {
                MqAckFrameRequest request = MqFrameCodec.DecodeAckRequest(payload);
                if (!TryAuthorize(ctx, registry, grants, writer, header, request.Db, request.Topic, DatabasePermission.Write))
                    return;
                if (string.IsNullOrWhiteSpace(request.ConsumerGroup))
                {
                    FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_request", "ack 需包含 consumerGroup。");
                    return;
                }

                long nextOffset = mqStore.Ack(
                    SonnetDbEndpoints.QualifyMqTopic(request.Db, request.Topic), request.ConsumerGroup, request.Offset);
                MqFrameCodec.EncodeAckResponse(writer, header.StreamId, nextOffset);
                return;
            }

            default:
                FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "unsupported_op", $"mq service 不支持 op {header.Op}。");
                return;
        }
    }

    /// <summary>
    /// 执行 kv 帧 op（#240）：get / put / scan。key / value 原始字节直传（零 Base64），
    /// 与 REST KV 端点同一引擎入口（<see cref="KvKeyspace"/>）与同一鉴权语义
    /// （get / scan 需 Read，put 需 Write；keyspace 名合法性同 REST）。
    /// </summary>
    private static void ExecuteKvOp(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        PipeWriter writer,
        FrameHeader header,
        ReadOnlyMemory<byte> payload)
    {
        switch ((KvFrameOp)header.Op)
        {
            case KvFrameOp.Get:
            {
                KvGetFrameRequest request = KvFrameCodec.DecodeGetRequest(payload);
                if (!TryAuthorizeNamedResource(ctx, registry, grants, writer, header,
                        request.Db, request.Keyspace, "keyspace 名", DatabasePermission.Read, out Tsdb tsdb))
                    return;
                KvEntry? entry = tsdb.Keyspaces.Open(request.Keyspace).GetEntry(request.Key.Span);
                KvFrameCodec.EncodeGetResponse(writer, header.StreamId, entry);
                return;
            }

            case KvFrameOp.Put:
            {
                KvPutFrameRequest request = KvFrameCodec.DecodePutRequest(payload);
                if (!TryAuthorizeNamedResource(ctx, registry, grants, writer, header,
                        request.Db, request.Keyspace, "keyspace 名", DatabasePermission.Write, out Tsdb tsdb))
                    return;
                long version = tsdb.Keyspaces.Open(request.Keyspace)
                    .Put(request.Key.Span, request.Value.Span, request.ExpiresAtUtc);
                KvFrameCodec.EncodePutResponse(writer, header.StreamId, version);
                return;
            }

            case KvFrameOp.Scan:
            {
                KvScanFrameRequest request = KvFrameCodec.DecodeScanRequest(payload);
                if (!TryAuthorizeNamedResource(ctx, registry, grants, writer, header,
                        request.Db, request.Keyspace, "keyspace 名", DatabasePermission.Read, out Tsdb tsdb))
                    return;
                int? limit = request.Limit <= 0 ? null : Math.Min(request.Limit, 10_000);
                IReadOnlyList<KvEntry> entries = tsdb.Keyspaces.Open(request.Keyspace)
                    .ScanPrefixAfter(request.Prefix.Span, request.AfterKey.Span, limit);
                KvFrameCodec.EncodeScanResponse(writer, header.StreamId, entries);
                return;
            }

            default:
                FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "unsupported_op", $"kv service 不支持 op {header.Op}。");
                return;
        }
    }

    /// <summary>
    /// 执行 doc 帧 op（#240）：find（ID 点查 / ID 列表 / 扫描分页）与 insert（批量插入）。
    /// JSON 文本原始 UTF-8 直传（零 JSON 信封转义），与 REST 文档端点同一引擎入口
    /// （<see cref="DocumentCollectionStore"/>）；复杂查询（filter / projection / sort / aggregate）
    /// 不进帧，走 REST 或 SQL。集合必须已存在（同 REST mustExist 语义），insert 需 Write、find 需 Read。
    /// </summary>
    private static void ExecuteDocOp(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        PipeWriter writer,
        FrameHeader header,
        ReadOnlyMemory<byte> payload)
    {
        switch ((DocFrameOp)header.Op)
        {
            case DocFrameOp.Find:
            {
                DocFindFrameRequest request = DocFrameCodec.DecodeFindRequest(payload.Span);
                if (!TryAuthorizeNamedResource(ctx, registry, grants, writer, header,
                        request.Db, request.Collection, "document collection 名", DatabasePermission.Read, out Tsdb tsdb))
                    return;
                if (!TryOpenCollection(tsdb, request.Collection, writer, header, out DocumentCollectionStore store))
                    return;

                IReadOnlyList<DocumentRow> rows;
                if (request.Ids.Count > 0)
                {
                    rows = store.GetMany(request.Ids);
                }
                else
                {
                    int limit = request.Limit <= 0 ? 100 : Math.Min(request.Limit, DocFrameCodec.MaxDocumentCount);
                    rows = request.AfterId is null
                        ? store.Scan(limit)
                        : store.ScanAfter(request.AfterId, limit);
                }

                DocFrameCodec.EncodeFindResponse(writer, header.StreamId, rows);
                return;
            }

            case DocFrameOp.Insert:
            {
                DocInsertFrameRequest request = DocFrameCodec.DecodeInsertRequest(payload.Span);
                if (!TryAuthorizeNamedResource(ctx, registry, grants, writer, header,
                        request.Db, request.Collection, "document collection 名", DatabasePermission.Write, out Tsdb tsdb))
                    return;
                if (!TryOpenCollection(tsdb, request.Collection, writer, header, out DocumentCollectionStore store))
                    return;

                DocumentWriteResult result = store.InsertMany(request.Documents, request.Ordered);
                DocFrameCodec.EncodeInsertResponse(writer, header.StreamId, result);
                return;
            }

            default:
                FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "unsupported_op", $"doc service 不支持 op {header.Op}。");
                return;
        }
    }

    private static bool TryOpenCollection(
        Tsdb tsdb,
        string collection,
        PipeWriter writer,
        FrameHeader header,
        out DocumentCollectionStore store)
    {
        store = null!;
        if (tsdb.Documents.Catalog.TryGet(collection) is null)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "collection_not_found",
                $"document collection '{collection}' 不存在。");
            return false;
        }

        store = tsdb.Documents.Open(collection);
        return true;
    }

    /// <summary>
    /// 执行 object 帧 op（#240）：get（meta → data × N → end 流式分块回传，
    /// 大 blob 增量到达客户端、响应缓冲内存上界 = 单块）与 put（内容原始字节直传、零 Base64）。
    /// 与 REST S3 兼容端点同一引擎入口（<see cref="SndbObjectStore"/>）与错误码词汇；
    /// bucket 管理 / 版本 / multipart / 生命周期不进帧。get 需 Read、put 需 Write。
    /// </summary>
    private static async Task ExecuteObjectOpAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        PipeWriter writer,
        FrameHeader header,
        ReadOnlySequence<byte> payload)
    {
        byte[]? rented = null;
        try
        {
            ReadOnlyMemory<byte> payloadMemory;
            if (payload.IsSingleSegment)
            {
                payloadMemory = payload.First;
            }
            else
            {
                rented = ArrayPool<byte>.Shared.Rent((int)payload.Length);
                payload.CopyTo(rented);
                payloadMemory = rented.AsMemory(0, (int)payload.Length);
            }

            if (header.Op == (byte)ObjectFrameOp.Get)
                await ExecuteObjectGetAsync(ctx, registry, grants, writer, header, payloadMemory).ConfigureAwait(false);
            else
                await ExecuteObjectPutAsync(ctx, registry, grants, writer, header, payloadMemory).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (FrameFormatException ex)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_frame", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_frame", ex.Message);
        }
        catch (SndbObjectStorageException ex)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, ex.Code, ex.Message);
        }
        catch (ArgumentException ex)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_request", ex.Message);
        }
        catch (IOException ex)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "object_storage_io_error", ex.Message);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task ExecuteObjectGetAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        PipeWriter writer,
        FrameHeader header,
        ReadOnlyMemory<byte> payload)
    {
        ObjectGetFrameRequest request = ObjectFrameCodec.DecodeGetRequest(payload.Span);
        SonnetDbEndpoints.MqAccessResult access = SonnetDbEndpoints.EvaluateDatabaseAccess(
            ctx, registry, grants, request.Db, DatabasePermission.Read, out Tsdb tsdb);
        if (access.Status != SonnetDbEndpoints.MqAccessStatus.Ok)
        {
            WriteAccessErrorFrame(writer, header, access);
            return;
        }

        var store = new SndbObjectStore(tsdb);
        SndbObjectReadResult? result = store.OpenRead(request.Bucket, request.Key, range: null, request.VersionId);
        if (result is null)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "object_not_found",
                $"Object '{request.Bucket}/{request.Key}' was not found.");
            return;
        }

        await using (result.Content)
        {
            ObjectFrameCodec.EncodeGetMetaFrame(writer, header.StreamId, result.Info);
            await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);

            long total = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ObjectFrameCodec.DefaultDataChunkBytes);
            try
            {
                while (true)
                {
                    int read = await result.Content.ReadAsync(
                        buffer.AsMemory(0, ObjectFrameCodec.DefaultDataChunkBytes), ctx.RequestAborted).ConfigureAwait(false);
                    if (read <= 0)
                        break;

                    ObjectFrameCodec.EncodeGetDataFrame(writer, header.StreamId, buffer.AsSpan(0, read));
                    total += read;
                    await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            ObjectFrameCodec.EncodeGetEndFrame(writer, header.StreamId, total);
        }
    }

    private static async Task ExecuteObjectPutAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        PipeWriter writer,
        FrameHeader header,
        ReadOnlyMemory<byte> payload)
    {
        ObjectPutFrameRequest request = ObjectFrameCodec.DecodePutRequest(payload);
        SonnetDbEndpoints.MqAccessResult access = SonnetDbEndpoints.EvaluateDatabaseAccess(
            ctx, registry, grants, request.Db, DatabasePermission.Write, out Tsdb tsdb);
        if (access.Status != SonnetDbEndpoints.MqAccessStatus.Ok)
        {
            WriteAccessErrorFrame(writer, header, access);
            return;
        }

        var store = new SndbObjectStore(tsdb);
        ReadOnlyMemoryStream content = new(request.Content);
        SndbObjectInfo info = await store.PutObjectAsync(
            request.Bucket,
            request.Key,
            content,
            request.ContentType,
            request.Metadata.Count == 0 ? null : request.Metadata,
            request.Tags.Count == 0 ? null : request.Tags,
            ctx.RequestAborted).ConfigureAwait(false);
        ObjectFrameCodec.EncodePutResponse(writer, header.StreamId, info);
    }

    private static bool TryAuthorizeNamedResource(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        PipeWriter writer,
        FrameHeader header,
        string db,
        string resourceName,
        string resourceLabel,
        DatabasePermission required,
        out Tsdb tsdb)
    {
        SonnetDbEndpoints.MqAccessResult access = SonnetDbEndpoints.EvaluateNamedResourceAccess(
            ctx, registry, grants, db, resourceName, resourceLabel, required, out tsdb);
        if (access.Status == SonnetDbEndpoints.MqAccessStatus.Ok)
            return true;

        WriteAccessErrorFrame(writer, header, access);
        return false;
    }

    private static void WriteAccessErrorFrame(PipeWriter writer, in FrameHeader header, in SonnetDbEndpoints.MqAccessResult access)
    {
        string code = access.Status switch
        {
            SonnetDbEndpoints.MqAccessStatus.DbNotFound => "db_not_found",
            SonnetDbEndpoints.MqAccessStatus.Forbidden => "forbidden",
            _ => "bad_request",
        };
        FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, code, access.Message);
    }

    /// <summary>
    /// 把 <see cref="ReadOnlyMemory{T}"/> 包装为只读流，供 <see cref="SndbObjectStore.PutObjectAsync"/>
    /// 直接消费帧体内容视图（帧在 PipeReader AdvanceTo 之前同步-完成处理，视图存活期覆盖整个写入）。
    /// </summary>
    private sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> memory) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => memory.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int remaining = memory.Length - _position;
            int count = Math.Min(remaining, buffer.Length);
            if (count <= 0)
                return 0;
            memory.Span.Slice(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static bool TryAuthorize(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        PipeWriter writer,
        FrameHeader header,
        string db,
        string topic,
        DatabasePermission required)
    {
        SonnetDbEndpoints.MqAccessResult access = SonnetDbEndpoints.EvaluateMqAccess(ctx, registry, grants, db, topic, required);
        if (access.Status == SonnetDbEndpoints.MqAccessStatus.Ok)
            return true;

        WriteAccessErrorFrame(writer, header, access);
        return false;
    }

    private static async Task RespondFramingErrorAsync(HttpContext ctx, PipeWriter writer, int frameCount, string code, string message)
    {
        if (frameCount == 0 && !ctx.Response.HasStarted)
        {
            await WriteJsonErrorAsync(ctx, StatusCodes.Status400BadRequest, code, message).ConfigureAwait(false);
            return;
        }

        EnsureFrameResponseStarted(ctx);
        FrameCodec.WriteErrorFrame(writer, 0, 0, 0, code, message);
        await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteJsonErrorAsync(HttpContext ctx, int statusCode, string code, string message)
    {
        if (ctx.Response.HasStarted)
            return;
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse, ctx.RequestAborted).ConfigureAwait(false);
    }
}
