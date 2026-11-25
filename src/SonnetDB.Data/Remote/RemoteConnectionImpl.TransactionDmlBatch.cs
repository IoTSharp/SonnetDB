using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SonnetDB.Data.Internal;
using SonnetDB.Exceptions;
using SonnetDB.Protocol;
using SonnetDB.Sql;

namespace SonnetDB.Data.Remote;

internal sealed partial class RemoteConnectionImpl
{
    internal async Task<IReadOnlyList<SndbDmlBatchResult>?> ExecuteTransactionDmlBatchAsync(
        RemoteTransactionState transaction,
        IReadOnlyList<SndbDmlBatchStatement> statements,
        CancellationToken cancellationToken,
        Func<ValueTask>? onSending)
    {
        cancellationToken.ThrowIfCancellationRequested();
        transaction.ThrowIfCompleted();
        if (_http is null || transaction.SessionId is not { } sessionId)
            throw new InvalidOperationException("远程事务会话未打开。");
        if (statements.Count is < 1 or > SqlTransactionDmlBatchProtocol.MaxStatements)
            throw new ArgumentOutOfRangeException(nameof(statements));

        var requests = new TransactionDmlBatchStatementBody[statements.Count];
        int timeoutSeconds = _builder.Timeout;
        for (int index = 0; index < statements.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var statement = statements[index];
            string sql = statement.Sql;
            byte[]? typedParameters;
            try
            {
                typedParameters = ParameterBinder.PrepareRemoteRequest(ref sql, statement.Parameters);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                or NotSupportedException or SqlParseException or OverflowException or FormatException)
            {
                // 参数在逐条路径对应的执行位置报错，保留显式事务中此前命令的结果。
                return null;
            }
            requests[index] = new(sql, typedParameters, statement.ExpectedRecordsAffected);
            if (statement.CommandTimeout > 0)
                timeoutSeconds = Math.Min(timeoutSeconds, statement.CommandTimeout);
        }

        var body = new TransactionDmlBatchRequestBody(requests);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(body, RemoteJsonContext.Default.TransactionDmlBatchRequestBody);
        if (payload.Length > SqlTransactionDmlBatchProtocol.MaxRequestBytes)
            return null;

        if (onSending is not null)
            await onSending().ConfigureAwait(false);

        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = CreateRequest(HttpMethod.Post, SessionUrl(sessionId) + "/sql/batch", content);
        request.Headers.Remove(SqlFrameCodec.ResultVersionHeader);
        request.Headers.Add(SqlFrameCodec.ResultVersionHeader, SqlFrameCodec.NativeDateTime2ResultVersion);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        // 307/308 会重发 POST 请求体；事务批次必须只发送一次。
        using var client = RemoteHttpClientFactory.Create(new Uri(_baseUrl, UriKind.Absolute),
            _builder.Username, _builder.Password, _builder.Token, TimeSpan.FromSeconds(_builder.Timeout), allowAutoRedirect: false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new InvalidDataException("远程事务 DML 批请求收到重定向。");
            if (!response.IsSuccessStatusCode)
                throw await ReadTransactionDmlBatchHttpErrorAsync(response, deadline.Token).ConfigureAwait(false);
            if (!RemoteExecutionResult.HasNativeDateTime2Result(response))
                throw new InvalidDataException("远程 DML 批响应未确认 SQL v7。");

            return await ReadTransactionDmlBatchAsync(response, statements, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("远程事务 DML 批请求已取消；已执行的前缀未知，未自动重发。",
                exception, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException($"远程事务 DML 批请求超过 {timeoutSeconds} 秒；已执行的前缀未知，未自动重发。",
                exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException
            or JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            throw new SndbServerException("sql_outcome_unknown",
                "远程事务 DML 批响应未完整验证；已执行的前缀未知，未自动重发。", HttpStatusCode.OK, exception);
        }
    }

    private static async Task<SndbServerException> ReadTransactionDmlBatchHttpErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        // 客户端拒绝和已知的服务端准入拒绝返回完整 JSON 错误。
        // 网关和通用服务端故障可能发生在执行后，无法确认事务前缀。
        if (((int)status is < 400 or >= 500 && status != HttpStatusCode.ServiceUnavailable)
            || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"远程事务 DML 批响应的 HTTP 状态 {status} 无法确认执行结果。");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("error", out var codeElement) || codeElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(codeElement.GetString())
            || !root.TryGetProperty("message", out var messageElement) || messageElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("远程事务 DML 批响应缺少完整的服务端错误。");

        string code = codeElement.GetString()!;
        if (status == HttpStatusCode.ServiceUnavailable
            && code is not ("sql_overloaded" or "connection_session_busy" or "transaction_busy"
                or TimeSeriesWriteRecoveryRequiredException.ErrorCode or TimeSeriesWriteRecoveryRequiredException.PartialErrorCode))
            throw new InvalidDataException("远程事务 DML 批响应无法确认服务端拒绝原因。");

        return new SndbServerException(code, messageElement.GetString()!, status);
    }

    private static async Task<IReadOnlyList<SndbDmlBatchResult>> ReadTransactionDmlBatchAsync(
        HttpResponseMessage response,
        IReadOnlyList<SndbDmlBatchStatement> statements,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var results = new List<SndbDmlBatchResult>(statements.Count);
        bool mismatch = false;
        for (int index = 0; index < statements.Count && !mismatch; index++)
        {
            string firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("远程 DML 批响应缺少语句结果。");
            IExecutionResult result;
            try
            {
                result = await RemoteExecutionResult.ReadBatchStatementAsync(response, stream, reader, firstLine,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SndbServerException exception)
            {
                results.Add(new(null, exception));
                return results;
            }

            results.Add(new(new SndbDataReader(result, CommandBehavior.Default, connection: null), null));
            mismatch = statements[index].ExpectedRecordsAffected is { } expected && result.RecordsAffected != expected;
        }

        var end = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("远程 DML 批响应缺少批次结束记录。");
        using var document = JsonDocument.Parse(end);
        var root = document.RootElement;
        string expectedStopReason = mismatch ? SqlTransactionDmlBatchProtocol.RecordsAffectedMismatch
            : SqlTransactionDmlBatchProtocol.Completed;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || type.GetString() != SqlTransactionDmlBatchProtocol.EndRecordType
            || !root.TryGetProperty("statementsExecuted", out var count) || count.ValueKind != JsonValueKind.Number
            || !count.TryGetInt32(out int executed) || executed != results.Count
            || !root.TryGetProperty("stopReason", out var reason) || reason.ValueKind != JsonValueKind.String
            || reason.GetString() != expectedStopReason
            || await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidDataException("远程 DML 批响应的批次结束记录无效。");

        return results;
    }
}

internal sealed record TransactionDmlBatchRequestBody(TransactionDmlBatchStatementBody[] Statements);

internal sealed record TransactionDmlBatchStatementBody(
    string Sql, byte[]? TypedParameters, int? ExpectedRecordsAffected);
