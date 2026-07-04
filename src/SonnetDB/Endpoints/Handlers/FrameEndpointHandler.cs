using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Protocol;
using SonnetMQ;

namespace SonnetDB.Endpoints;

/// <summary>
/// 通用二进制帧端点处理器（M28 P5b #235）。
/// 请求体 = 1..N 个请求帧，逐帧解析、鉴权、分发到引擎、逐帧写回响应帧（streamId 回显）。
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
        SonnetMqStore mqStore)
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
                    DispatchMqFrame(ctx, registry, grants, mqStore, writer, header, payload);
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

        if (header.Service != (byte)FrameService.Mq)
        {
            errorCode = "unsupported_service";
            return $"service {header.Service} 尚未挂载（当前仅 mq={(byte)FrameService.Mq}）。";
        }

        if (header.Op is < (byte)MqFrameOp.Publish or > (byte)MqFrameOp.Ack)
        {
            errorCode = "unsupported_op";
            return $"mq service 不支持 op {header.Op}。";
        }

        errorCode = string.Empty;
        return null;
    }

    private static void DispatchMqFrame(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        SonnetMqStore mqStore,
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

            ExecuteMqOp(ctx, registry, grants, mqStore, writer, header, payloadMemory);
        }
        catch (FrameFormatException ex)
        {
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, "bad_frame", ex.Message);
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

        string code = access.Status switch
        {
            SonnetDbEndpoints.MqAccessStatus.DbNotFound => "db_not_found",
            SonnetDbEndpoints.MqAccessStatus.Forbidden => "forbidden",
            _ => "bad_request",
        };
        FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, code, access.Message);
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
