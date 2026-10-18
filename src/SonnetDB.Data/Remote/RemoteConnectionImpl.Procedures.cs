using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using SonnetDB.Data.Internal;
using SonnetDB.Protocol;

namespace SonnetDB.Data.Remote;

internal sealed partial class RemoteConnectionImpl
{
    public async Task<IExecutionResult> ExecuteProcedureAsync(ProcedureCallSnapshot call, object? transactionState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_http is null || _state != ConnectionState.Open) throw new InvalidOperationException("连接未打开。");
        var transaction = GetTransactionState(transactionState);
        string sessionId = transaction?.SessionId ?? string.Empty;
        if (transaction is not null && sessionId.Length == 0)
            throw new NotSupportedException("过程不能通过旧事务重放预览执行；需要真实远程事务会话。");
        byte[] request = SqlProcedureFrameCodec.EncodeRequestPayload(_database, call.Name, sessionId, call.Arguments);
        byte[] completed;
        bool dispatched = false;
        try
        {
            if (_builder.ResolveProtocol() == SndbTransportProtocol.Rest)
            {
                string json = JsonSerializer.Serialize(new ProcedureJsonEnvelope { Version = 1, Payload = request }, RemoteJsonContext.Default.ProcedureJsonEnvelope);
                if (Encoding.UTF8.GetByteCount(json) > SqlProcedureFrameCodec.MaxJsonRequestBytes)
                    throw new ArgumentException("REST 过程请求超过 JSON 编码预算。");
                using var message = CreateRequest(HttpMethod.Post,
                    $"v1/db/{Uri.EscapeDataString(_database)}/sql/procedure",
                    new StringContent(json, Encoding.UTF8, "application/json"));
                dispatched = true;
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await using var errorStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    byte[] body = await BoundedContentReader.ReadAsync(errorStream, 64 * 1024, cancellationToken).ConfigureAwait(false);
                    ServerErrorBody? error = null;
                    try { error = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.ServerErrorBody); }
                    catch (JsonException) { }
                    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnsupportedMediaType && error is null)
                        throw new SndbServerException("procedure_not_supported", "服务端不支持类型化过程端点；未回落文本 CALL。", response.StatusCode);
                    throw new SndbServerException(error?.Error ?? "procedure_http_error",
                        error?.Message ?? "过程 HTTP 请求失败，提交状态可能未知；未自动重试。", response.StatusCode);
                }
                if (response.Content.Headers.ContentLength > SqlProcedureFrameCodec.MaxJsonResponseBytes)
                    throw new InvalidDataException("REST 过程响应超过 JSON 编码预算；提交状态未知。");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                byte[] bytes = await BoundedContentReader.ReadAsync(stream, SqlProcedureFrameCodec.MaxJsonResponseBytes, cancellationToken).ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize(bytes, RemoteJsonContext.Default.ProcedureJsonEnvelope);
                if (envelope is null || envelope.Version != 1) throw new InvalidDataException("过程响应版本不支持；提交状态未知。");
                completed = envelope.Payload;
            }
            else
            {
                if (_frames is null) throw new InvalidOperationException("Frame 通道未打开。");
                if (!_frames.ShouldTryFrames()) throw new NotSupportedException("当前连接已确认不支持 Frame；过程调用未回落 REST。");
                dispatched = true;
                var frames = await _frames.TrySendAsync(SqlProcedureFrameCodec.WrapFrame(request, response: false), cancellationToken,
                    allowFallback: false, maximumResponseBytes: SqlProcedureFrameCodec.MaxResponseBytes + FrameHeader.Size,
                    requireHttp2: true).ConfigureAwait(false);
                if (frames is null || frames.Count != 1) throw new InvalidDataException("过程帧响应不完整；提交状态未知，未回落 REST。");
                var frame = frames[0];
                if (!frame.Header.IsResponse || frame.Header.Version != FrameHeader.CurrentVersion
                    || (frame.Header.Flags & ~(byte)(FrameFlags.Response | FrameFlags.Error)) != 0 || frame.Header.Service != (byte)FrameService.Sql
                    || frame.Header.Op != (byte)SqlFrameOp.Procedure || frame.Header.StreamId != 1)
                    throw new InvalidDataException("过程帧响应身份不符；提交状态未知。");
                FrameChannel.ThrowIfError(frame.Header, frame.Payload);
                completed = frame.Payload;
            }
            var result = SqlProcedureFrameCodec.DecodeResponse(completed);
            var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in result.Outputs)
                if (!outputs.TryAdd(output.Name, output.Value)) throw new InvalidDataException("过程输出名称重复。");
            var selected = new List<SonnetDB.Sql.Execution.SelectExecutionResult>(result.ResultFrames.Count);
            long remainingCells = 2_000_000;
            foreach (byte[] frame in result.ResultFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = ProcedureResultDecoder.DecodeResult(frame, remainingCells);
                remainingCells -= (long)query.Rows.Count * query.Columns.Count;
                selected.Add(query);
            }
            try { call.ValidateCompletion(outputs, result.ReturnValue); }
            catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
            { throw new InvalidDataException("过程输出与冻结声明不匹配；提交状态未知。", exception); }
            return new ProcedureExecutionResultAdapter(selected, outputs, result.ReturnValue, result.RecordsAffected, call);
        }
        catch (OperationCanceledException exception) when (dispatched)
        {
            throw new OperationCanceledException("过程请求已取消；发送后的提交状态可能未知，未自动重试。", exception, cancellationToken);
        }
        catch (Exception exception) when (dispatched && (exception is HttpRequestException or InvalidDataException or FrameFormatException or JsonException
            || exception is SndbServerException { Error: "frame_transport_error" }))
        {
            throw new SndbServerException("procedure_outcome_unknown", "过程响应未完整验证；提交状态未知，未自动重试或降级。", HttpStatusCode.OK, exception);
        }
    }
}
