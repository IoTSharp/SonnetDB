using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Protocol;
using SonnetMQ;

namespace SonnetDB.Endpoints;

/// <summary>
/// MQ 推送订阅双工流端点（M28 P5b #236）：<c>POST /v1/frame/stream</c>，仅 HTTP/2。
/// 请求体是长生命周期的帧流；订阅后新消息到达即以带 <see cref="FrameFlags.Push"/> 位、
/// 同 streamId 的帧推送。控制帧（publish/publish-batch/pull/ack）与一元端点语义一致，
/// 响应交错回写。用 <see cref="Channel"/> 做 producer/consumer 解耦与背压，HTTP/2 流控经
/// PipeWriter.FlushAsync 反压到 channel。一条连接多订阅按 streamId 交错。
/// </summary>
internal static class FrameStreamEndpointHandler
{
    internal const string ContentType = FrameEndpointHandler.ContentType;

    /// <summary>单连接最大并发订阅数（防御）。</summary>
    private const int MaxSubscriptionsPerConnection = 64;

    /// <summary>outbound channel 容量：满时 pump/reader 阻塞于 WriteAsync，形成背压。</summary>
    private const int OutboundCapacity = 8;

    private const int DefaultBatchMax = 100;
    private const int MaxBatchMax = 1000;

    public static async Task HandleAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        SonnetMqStore mqStore)
    {
        if (!IsFrameContentType(ctx.Request.ContentType))
        {
            await WriteJsonErrorAsync(ctx, StatusCodes.Status415UnsupportedMediaType, "bad_request",
                $"帧流端点要求 Content-Type '{ContentType}'。").ConfigureAwait(false);
            return;
        }

        // 双工推送需要 HTTP/2 长流；HTTP/1.1 无多路复用，长连接期间无法交错，拒绝。
        if (!string.Equals(ctx.Request.Protocol, "HTTP/2", StringComparison.Ordinal))
        {
            await WriteJsonErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                "帧流端点要求 HTTP/2（h2c 端点或 TLS ALPN 协商）。").ConfigureAwait(false);
            return;
        }

        // 清除 Kestrel 默认 MinRequestBodyDataRate（240B/5s），否则空闲订阅流的请求体会被判定过慢而中止。
        var minRateFeature = ctx.Features.Get<IHttpMinRequestBodyDataRateFeature>();
        if (minRateFeature is not null)
            minRateFeature.MinDataRate = null;

        // 先冲响应头，客户端才能在请求体仍打开时增量读取推送帧。
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = ContentType;
        await ctx.Response.StartAsync(ctx.RequestAborted).ConfigureAwait(false);

        var identity = CapturedIdentity.From(ctx);
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        // channel 传 OutboundFrame（帧描述 + 结果），由 writer task 直接编码进 PipeWriter——
        // 避免 producer 侧的 ArrayBufferWriter→ToArray→channel→PipeWriter.Write 三重缓冲。
        var channel = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        var subscriptions = new Dictionary<uint, Subscription>();
        // writer 绑定 RequestAborted（非 connectionCts）：正常 EOF teardown 时 connectionCts 取消只停 producer，
        // writer 仍排空 channel 里已入队的推送帧；仅客户端真正中止（RequestAborted）才让 writer 立即退出。
        Task writerTask = RunWriterAsync(ctx, channel.Reader, ctx.RequestAborted);

        try
        {
            await RunReaderAsync(ctx, registry, grants, mqStore, identity, channel.Writer, subscriptions, connectionCts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            // teardown 确定性顺序：先取消（唤醒所有 pump 的 WaitForMessagesAsync/WriteAsync）→ 等 pump 退出
            // → 完成 channel → 等 writer 排空。此顺序下没有 producer 在 channel 完成后仍尝试写入。
            connectionCts.Cancel();
            Subscription[] remaining;
            lock (subscriptions)
            {
                remaining = new Subscription[subscriptions.Count];
                subscriptions.Values.CopyTo(remaining, 0);
            }

            try { await Task.WhenAll(Array.ConvertAll(remaining, s => s.PumpTask)).ConfigureAwait(false); }
            catch { /* pump 取消/故障已在各自帧内处理；此处仅确保退出 */ }
            foreach (Subscription sub in remaining)
                sub.Cancellation.Dispose();

            channel.Writer.TryComplete();
            try { await writerTask.ConfigureAwait(false); }
            catch { /* 客户端中止时 writer 的 FlushAsync 抛 OCE，忽略 */ }
        }
    }

    // ────────────────────────────── writer task（独占 PipeWriter）──────────────────────────────

    private static async Task RunWriterAsync(HttpContext ctx, ChannelReader<OutboundFrame> reader, CancellationToken ct)
    {
        PipeWriter writer = ctx.Response.BodyWriter;
        try
        {
            await foreach (OutboundFrame frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // 直接编码进 PipeWriter：租借的写缓冲即最终发送缓冲，无中间 byte[]。
                frame.Encode(writer);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 连接关闭，静默结束。
        }
    }

    // ────────────────────────────── reader loop（解析控制帧）──────────────────────────────

    private static async Task RunReaderAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        SonnetMqStore mqStore,
        CapturedIdentity identity,
        ChannelWriter<OutboundFrame> outbound,
        Dictionary<uint, Subscription> subscriptions,
        CancellationToken ct)
    {
        PipeReader reader = ctx.Request.BodyReader;
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
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
                        // 帧边界不可恢复：回一个连接级错误帧后终止读取。
                        await EnqueueAsync(outbound, OutboundFrame.Error(0, 0, 0, "bad_frame", ex.Message), ct).ConfigureAwait(false);
                        reader.AdvanceTo(buffer.End);
                        return;
                    }

                    await HandleControlFrameAsync(ctx, registry, grants, mqStore, identity, outbound, subscriptions, header, payload, ct)
                        .ConfigureAwait(false);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // 连接关闭。
        }
    }

    private static async Task HandleControlFrameAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        SonnetMqStore mqStore,
        CapturedIdentity identity,
        ChannelWriter<OutboundFrame> outbound,
        Dictionary<uint, Subscription> subscriptions,
        FrameHeader header,
        ReadOnlySequence<byte> payload,
        CancellationToken ct)
    {
        string? envelopeError = ValidateEnvelope(in header, out string envelopeErrorCode);
        if (envelopeError is not null)
        {
            await EnqueueAsync(outbound, OutboundFrame.Error(header.Service, header.Op, header.StreamId, envelopeErrorCode, envelopeError), ct).ConfigureAwait(false);
            return;
        }

        switch ((MqFrameOp)header.Op)
        {
            case MqFrameOp.Publish:
            case MqFrameOp.PublishBatch:
            case MqFrameOp.Pull:
            case MqFrameOp.Ack:
            {
                // payload 是输入缓冲的零拷贝视图，仅在 AdvanceTo 前有效——同步消费（解码 + 引擎调用）
                // 产出 owned 结果帧后再异步入队，无需整段 ToArray。
                OutboundFrame response = ExecuteUnaryOp(ctx, registry, grants, mqStore, in header, payload);
                await EnqueueAsync(outbound, response, ct).ConfigureAwait(false);
                return;
            }

            case MqFrameOp.Subscribe:
            {
                // subscribe 帧体小且 DecodeSubscribeRequest 会 materialize 出 string 字段，同步解码后 payload 即可释放。
                OutboundFrame? decodeError = TryDecodeSubscribe(payload, in header, out MqSubscribeFrameRequest request);
                if (decodeError is { } error)
                {
                    await EnqueueAsync(outbound, error, ct).ConfigureAwait(false);
                    return;
                }

                await HandleSubscribeAsync(ctx, registry, grants, mqStore, identity, outbound, subscriptions, header, request, ct).ConfigureAwait(false);
                return;
            }

            case MqFrameOp.Unsubscribe:
                await HandleUnsubscribeAsync(outbound, subscriptions, header, ct).ConfigureAwait(false);
                return;
        }
    }

    // ────────────────────────────── 控制帧：一元 op（parity 一元端点）──────────────────────────────

    /// <summary>
    /// 同步执行一元 op：零拷贝消费 <paramref name="payload"/>（单段直接切片、多段 ArrayPool 租借），
    /// 解码 + 鉴权 + 引擎调用后返回 owned 结果帧。payload 视图在本方法返回后即失效，故不得跨出。
    /// </summary>
    private static OutboundFrame ExecuteUnaryOp(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        SonnetMqStore mqStore,
        in FrameHeader header,
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

            switch ((MqFrameOp)header.Op)
            {
                case MqFrameOp.Publish:
                {
                    MqPublishFrameRequest request = MqFrameCodec.DecodePublishRequest(payloadMemory);
                    if (Authorize(ctx, registry, grants, in header, request.Db, request.Topic, DatabasePermission.Write) is { } denied)
                        return denied;
                    long offset = mqStore.Publish(
                        SonnetDbEndpoints.QualifyMqTopic(request.Db, request.Topic),
                        request.Payload.Span,
                        request.Headers.Count == 0 ? null : new SonnetMqPublishOptions(request.Headers));
                    return OutboundFrame.PublishResponse(header.StreamId, offset);
                }

                case MqFrameOp.PublishBatch:
                {
                    MqPublishBatchFrameRequest request = MqFrameCodec.DecodePublishBatchRequest(payloadMemory);
                    if (Authorize(ctx, registry, grants, in header, request.Db, request.Topic, DatabasePermission.Write) is { } denied)
                        return denied;
                    IReadOnlyList<long> offsets = mqStore.PublishMany(
                        SonnetDbEndpoints.QualifyMqTopic(request.Db, request.Topic), request.Entries);
                    return OutboundFrame.PublishBatchResponse(header.StreamId, offsets);
                }

                case MqFrameOp.Pull:
                {
                    MqPullFrameRequest request = MqFrameCodec.DecodePullRequest(payloadMemory);
                    if (Authorize(ctx, registry, grants, in header, request.Db, request.Topic, DatabasePermission.Read) is { } denied)
                        return denied;
                    if (string.IsNullOrWhiteSpace(request.ConsumerGroup))
                        return OutboundFrame.Error(header.Service, header.Op, header.StreamId, "bad_request", "pull 需包含 consumerGroup。");

                    int maxCount = request.MaxCount <= 0 ? DefaultBatchMax : Math.Min(request.MaxCount, MaxBatchMax);
                    IReadOnlyList<SonnetMqMessage> messages = mqStore.Pull(
                        SonnetDbEndpoints.QualifyMqTopic(request.Db, request.Topic), request.ConsumerGroup, maxCount);
                    return OutboundFrame.PullResponse(header.StreamId, messages);
                }

                case MqFrameOp.Ack:
                {
                    MqAckFrameRequest request = MqFrameCodec.DecodeAckRequest(payloadMemory);
                    if (Authorize(ctx, registry, grants, in header, request.Db, request.Topic, DatabasePermission.Write) is { } denied)
                        return denied;
                    if (string.IsNullOrWhiteSpace(request.ConsumerGroup))
                        return OutboundFrame.Error(header.Service, header.Op, header.StreamId, "bad_request", "ack 需包含 consumerGroup。");

                    long nextOffset = mqStore.Ack(
                        SonnetDbEndpoints.QualifyMqTopic(request.Db, request.Topic), request.ConsumerGroup, request.Offset);
                    return OutboundFrame.AckResponse(header.StreamId, nextOffset);
                }

                default:
                    // envelope 校验已限定为上述四个一元 op，此处不可达。
                    return OutboundFrame.Error(header.Service, header.Op, header.StreamId, "unsupported_op", $"mq service 不支持 op {header.Op}。");
            }
        }
        catch (Exception ex) when (TryMapException(ex, out string code, out string message))
        {
            return OutboundFrame.Error(header.Service, header.Op, header.StreamId, code, message);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // ────────────────────────────── 控制帧：subscribe / unsubscribe ──────────────────────────────

    /// <summary>
    /// 同步零拷贝解码 subscribe 帧体：成功返回 null 并输出 <paramref name="request"/>，失败返回错误帧。
    /// </summary>
    private static OutboundFrame? TryDecodeSubscribe(ReadOnlySequence<byte> payload, in FrameHeader header, out MqSubscribeFrameRequest request)
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

            request = MqFrameCodec.DecodeSubscribeRequest(payloadMemory);
            return null;
        }
        catch (Exception ex) when (TryMapException(ex, out string code, out string message))
        {
            request = default!;
            return OutboundFrame.Error(header.Service, header.Op, header.StreamId, code, message);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task HandleSubscribeAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        SonnetMqStore mqStore,
        CapturedIdentity identity,
        ChannelWriter<OutboundFrame> outbound,
        Dictionary<uint, Subscription> subscriptions,
        FrameHeader header,
        MqSubscribeFrameRequest request,
        CancellationToken ct)
    {
        if (Authorize(ctx, registry, grants, in header, request.Db, request.Topic, DatabasePermission.Read) is { } denied)
        {
            await EnqueueAsync(outbound, denied, ct).ConfigureAwait(false);
            return;
        }

        bool needsGroup = request.StartMode == MqSubscribeStartMode.ConsumerGroup;
        if (needsGroup && string.IsNullOrWhiteSpace(request.ConsumerGroup))
        {
            await EnqueueAsync(outbound, OutboundFrame.Error(header.Service, header.Op, header.StreamId, "bad_request", "consumerGroup 起始模式需提供 consumerGroup。"), ct).ConfigureAwait(false);
            return;
        }

        // 查重 / 容量校验（reader loop 单线程调用本方法，锁仅防御 teardown 的快照读取）。锁内不 await。
        Subscription? subscription = null;
        long startOffset = 0;
        OutboundFrame? rejectFrame = null;
        lock (subscriptions)
        {
            if (subscriptions.ContainsKey(header.StreamId))
                rejectFrame = OutboundFrame.Error(header.Service, header.Op, header.StreamId, "bad_request", $"streamId {header.StreamId} 已存在活跃订阅。");
            else if (subscriptions.Count >= MaxSubscriptionsPerConnection)
                rejectFrame = OutboundFrame.Error(header.Service, header.Op, header.StreamId, "too_many_subscriptions", $"单连接订阅数超过上限 {MaxSubscriptionsPerConnection}。");
            else
            {
                string qualified = SonnetDbEndpoints.QualifyMqTopic(request.Db, request.Topic);
                startOffset = ResolveStartOffset(mqStore, qualified, in request);
                var subCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // 先登记订阅（PumpTask 暂为占位），确认帧入队后再真正启动 pump——保证确认帧先于任何数据帧。
                subscription = new Subscription(header.StreamId, request.Db, qualified, subCts);
                subscriptions[header.StreamId] = subscription;
            }
        }

        if (rejectFrame is { } reject)
        {
            await EnqueueAsync(outbound, reject, ct).ConfigureAwait(false);
            return;
        }

        await EnqueueAsync(outbound, OutboundFrame.SubscribeResponse(header.StreamId, startOffset), ct).ConfigureAwait(false);
        subscription!.PumpTask = RunPumpAsync(mqStore, grants, identity, outbound, subscription, startOffset,
            request.BatchMax <= 0 ? DefaultBatchMax : Math.Min(request.BatchMax, MaxBatchMax), subscription.Cancellation.Token);
    }

    private static async Task HandleUnsubscribeAsync(
        ChannelWriter<OutboundFrame> outbound,
        Dictionary<uint, Subscription> subscriptions,
        FrameHeader header,
        CancellationToken ct)
    {
        Subscription? sub;
        lock (subscriptions)
        {
            subscriptions.Remove(header.StreamId, out sub);
        }

        if (sub is null)
        {
            await EnqueueAsync(outbound, OutboundFrame.Error(header.Service, header.Op, header.StreamId, "bad_request", $"streamId {header.StreamId} 无活跃订阅。"), ct).ConfigureAwait(false);
            return;
        }

        sub.Cancellation.Cancel();
        try { await sub.PumpTask.ConfigureAwait(false); }
        catch { /* pump 取消 */ }
        sub.Cancellation.Dispose();

        await EnqueueAsync(outbound, OutboundFrame.UnsubscribeResponse(header.StreamId), ct).ConfigureAwait(false);
    }

    // ────────────────────────────── pump loop（推送数据帧）──────────────────────────────

    private static async Task RunPumpAsync(
        SonnetMqStore mqStore,
        GrantsStore grants,
        CapturedIdentity identity,
        ChannelWriter<OutboundFrame> outbound,
        Subscription subscription,
        long startOffset,
        int batchMax,
        CancellationToken ct)
    {
        long cursor = startOffset;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                cursor = await mqStore.WaitForMessagesAsync(subscription.Topic, cursor, ct).ConfigureAwait(false);

                // 动态用户逐批复查授权（SSE parity）——权限被撤销即杀该订阅，连接存活。
                if (!identity.HasReadPermission(grants, subscription.Db))
                {
                    await EnqueueAsync(outbound, OutboundFrame.Error((byte)FrameService.Mq, (byte)MqFrameOp.Subscribe, subscription.StreamId, "forbidden", $"当前凭据对数据库 '{subscription.Db}' 失去 read 权限。"), ct).ConfigureAwait(false);
                    return;
                }

                IReadOnlyList<SonnetMqMessage> batch = mqStore.Pull(subscription.Topic, cursor, batchMax);
                if (batch.Count == 0)
                    continue;

                await EnqueueAsync(outbound, OutboundFrame.Push(subscription.StreamId, batch), ct).ConfigureAwait(false);
                cursor = batch[^1].Offset + 1;
            }
        }
        catch (OperationCanceledException)
        {
            // 退订 / 连接关闭。
        }
        catch (ObjectDisposedException)
        {
            // store 释放：连接即将随之关闭。
        }
        catch (Exception ex)
        {
            string code = ex is IOException ? "mq_io_error" : "mq_error";
            try { await EnqueueAsync(outbound, OutboundFrame.Error((byte)FrameService.Mq, (byte)MqFrameOp.Subscribe, subscription.StreamId, code, ex.Message), ct).ConfigureAwait(false); }
            catch { /* 连接关闭 */ }
        }
    }

    private static long ResolveStartOffset(SonnetMqStore mqStore, string qualifiedTopic, in MqSubscribeFrameRequest request)
    {
        SonnetMqTopicStats stats = mqStore.GetStats(qualifiedTopic);
        return request.StartMode switch
        {
            MqSubscribeStartMode.ConsumerGroup =>
                stats.ConsumerOffsets.TryGetValue(request.ConsumerGroup, out long committed) ? committed : stats.NextOffset - stats.MessageCount,
            MqSubscribeStartMode.ExplicitOffset => request.StartOffset,
            MqSubscribeStartMode.Earliest => stats.NextOffset - stats.MessageCount,
            MqSubscribeStartMode.Latest => stats.NextOffset,
            _ => stats.NextOffset,
        };
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private static ValueTask EnqueueAsync(ChannelWriter<OutboundFrame> outbound, OutboundFrame frame, CancellationToken ct)
        => outbound.WriteAsync(frame, ct);

    private static bool IsFrameContentType(string? contentType)
        => contentType is not null &&
           contentType.AsSpan().TrimStart().StartsWith(ContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 流端点信封校验：接受 op 1~6，拒绝 Response/Error/Push 位与保留位。
    /// </summary>
    private static string? ValidateEnvelope(in FrameHeader header, out string errorCode)
    {
        if (header.Version != FrameHeader.CurrentVersion)
        {
            errorCode = "unsupported_version";
            return $"不支持的帧协议版本 {header.Version}（当前 {FrameHeader.CurrentVersion}）。";
        }

        if (header.Flags != (byte)FrameFlags.None)
        {
            errorCode = "bad_frame";
            return "请求帧 Flags 必须为 0（不得设置 Response/Error/Push 或保留位）。";
        }

        if (header.Service != (byte)FrameService.Mq)
        {
            errorCode = "unsupported_service";
            return $"service {header.Service} 尚未挂载（当前仅 mq={(byte)FrameService.Mq}）。";
        }

        if (header.Op is < (byte)MqFrameOp.Publish or > (byte)MqFrameOp.Unsubscribe)
        {
            errorCode = "unsupported_op";
            return $"mq service 不支持 op {header.Op}。";
        }

        errorCode = string.Empty;
        return null;
    }

    private static OutboundFrame? Authorize(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        in FrameHeader header,
        string db,
        string topic,
        DatabasePermission required)
    {
        SonnetDbEndpoints.MqAccessResult access = SonnetDbEndpoints.EvaluateMqAccess(ctx, registry, grants, db, topic, required);
        if (access.Status == SonnetDbEndpoints.MqAccessStatus.Ok)
            return null;

        string code = access.Status switch
        {
            SonnetDbEndpoints.MqAccessStatus.DbNotFound => "db_not_found",
            SonnetDbEndpoints.MqAccessStatus.Forbidden => "forbidden",
            _ => "bad_request",
        };
        return OutboundFrame.Error(header.Service, header.Op, header.StreamId, code, access.Message);
    }

    private static bool TryMapException(Exception ex, out string code, out string message)
    {
        switch (ex)
        {
            case FrameFormatException:
            case InvalidOperationException:
                code = "bad_frame"; message = ex.Message; return true;
            case ArgumentException:
                code = "bad_request"; message = ex.Message; return true;
            case IOException:
                code = "mq_io_error"; message = ex.Message; return true;
            case InvalidDataException:
                code = "mq_error"; message = ex.Message; return true;
            default:
                code = string.Empty; message = string.Empty; return false;
        }
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

    /// <summary>
    /// 订阅时捕获的调用方身份（HttpContext 非线程安全，pump 不得触碰它）。
    /// 动态用户按用户名逐批复查 grants；静态 token（角色）在订阅时已通过、保持不变。
    /// </summary>
    private readonly struct CapturedIdentity
    {
        private readonly string? _dynamicUser;
        private readonly bool _isSuperuser;

        private CapturedIdentity(string? dynamicUser, bool isSuperuser)
        {
            _dynamicUser = dynamicUser;
            _isSuperuser = isSuperuser;
        }

        public static CapturedIdentity From(HttpContext ctx)
        {
            if (BearerAuthMiddleware.GetUser(ctx) is AuthenticatedUser user)
                return new CapturedIdentity(user.IsSuperuser ? null : user.UserName, user.IsSuperuser);
            // 静态 token（角色凭据）：订阅时的 EvaluateMqAccess 已判定权限，pump 无需再查。
            return new CapturedIdentity(null, isSuperuser: true);
        }

        public bool HasReadPermission(GrantsStore grants, string db)
        {
            if (_isSuperuser || _dynamicUser is null)
                return true;
            return grants.GetPermission(_dynamicUser, db) >= DatabasePermission.Read;
        }
    }

    /// <summary>
    /// 出站帧的延迟编码描述：携带帧类型与 owned 结果（offset/messages/错误信息），
    /// 由 writer task 在独占 PipeWriter 上调用 <see cref="Encode"/> 直接写出，消除中间缓冲。
    /// </summary>
    private readonly struct OutboundFrame
    {
        private readonly OutboundKind _kind;
        private readonly uint _streamId;
        private readonly long _scalar;
        private readonly IReadOnlyList<SonnetMqMessage>? _messages;
        private readonly IReadOnlyList<long>? _offsets;
        private readonly byte _service;
        private readonly byte _op;
        private readonly string? _code;
        private readonly string? _message;

        private OutboundFrame(
            OutboundKind kind, uint streamId, long scalar,
            IReadOnlyList<SonnetMqMessage>? messages, IReadOnlyList<long>? offsets,
            byte service, byte op, string? code, string? message)
        {
            _kind = kind;
            _streamId = streamId;
            _scalar = scalar;
            _messages = messages;
            _offsets = offsets;
            _service = service;
            _op = op;
            _code = code;
            _message = message;
        }

        public static OutboundFrame Error(byte service, byte op, uint streamId, string code, string message)
            => new(OutboundKind.Error, streamId, 0, null, null, service, op, code, message);

        public static OutboundFrame PublishResponse(uint streamId, long offset)
            => new(OutboundKind.PublishResponse, streamId, offset, null, null, 0, 0, null, null);

        public static OutboundFrame PublishBatchResponse(uint streamId, IReadOnlyList<long> offsets)
            => new(OutboundKind.PublishBatchResponse, streamId, 0, null, offsets, 0, 0, null, null);

        public static OutboundFrame PullResponse(uint streamId, IReadOnlyList<SonnetMqMessage> messages)
            => new(OutboundKind.PullResponse, streamId, 0, messages, null, 0, 0, null, null);

        public static OutboundFrame AckResponse(uint streamId, long nextOffset)
            => new(OutboundKind.AckResponse, streamId, nextOffset, null, null, 0, 0, null, null);

        public static OutboundFrame SubscribeResponse(uint streamId, long effectiveOffset)
            => new(OutboundKind.SubscribeResponse, streamId, effectiveOffset, null, null, 0, 0, null, null);

        public static OutboundFrame UnsubscribeResponse(uint streamId)
            => new(OutboundKind.UnsubscribeResponse, streamId, 0, null, null, 0, 0, null, null);

        public static OutboundFrame Push(uint streamId, IReadOnlyList<SonnetMqMessage> messages)
            => new(OutboundKind.Push, streamId, 0, messages, null, 0, 0, null, null);

        public void Encode(IBufferWriter<byte> writer)
        {
            switch (_kind)
            {
                case OutboundKind.Error:
                    FrameCodec.WriteErrorFrame(writer, _service, _op, _streamId, _code!, _message!);
                    break;
                case OutboundKind.PublishResponse:
                    MqFrameCodec.EncodePublishResponse(writer, _streamId, _scalar);
                    break;
                case OutboundKind.PublishBatchResponse:
                    MqFrameCodec.EncodePublishBatchResponse(writer, _streamId, _offsets!);
                    break;
                case OutboundKind.PullResponse:
                    MqFrameCodec.EncodePullResponse(writer, _streamId, _messages!);
                    break;
                case OutboundKind.AckResponse:
                    MqFrameCodec.EncodeAckResponse(writer, _streamId, _scalar);
                    break;
                case OutboundKind.SubscribeResponse:
                    MqFrameCodec.EncodeSubscribeResponse(writer, _streamId, _scalar);
                    break;
                case OutboundKind.UnsubscribeResponse:
                    MqFrameCodec.EncodeUnsubscribeResponse(writer, _streamId);
                    break;
                case OutboundKind.Push:
                    MqFrameCodec.EncodePushFrame(writer, _streamId, _messages!);
                    break;
            }
        }
    }

    private enum OutboundKind : byte
    {
        Error,
        PublishResponse,
        PublishBatchResponse,
        PullResponse,
        AckResponse,
        SubscribeResponse,
        UnsubscribeResponse,
        Push,
    }

    private sealed class Subscription(uint streamId, string db, string topic, CancellationTokenSource cancellation)
    {
        public uint StreamId { get; } = streamId;
        public string Db { get; } = db;
        public string Topic { get; } = topic;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task PumpTask { get; set; } = Task.CompletedTask;
    }
}
