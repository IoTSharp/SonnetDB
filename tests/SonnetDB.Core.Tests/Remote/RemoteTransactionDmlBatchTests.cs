using SonnetDB.Data;
using SonnetDB.Exceptions;
using SonnetDB.Protocol;

namespace SonnetDB.Core.Tests.Remote;

public sealed class RemoteTransactionDmlBatchTests
{
    private const string SessionId = "a63108f36bb549c0b53242b8c33da66d";
    private const string SessionPath = "/v1/db/demo/sql/transactions";
    private const string StatementEnd = """
        {"type":"end","rowCount":0,"recordsAffected":1}
        """ + "\n";

    [Theory]
    [InlineData(200, false, StatementEnd)]
    [InlineData(200, true, StatementEnd)]
    [InlineData(307, false, StatementEnd)]
    [InlineData(400, false, "{\"error\":\"sql_error\"")]
    [InlineData(502, false, "{\"error\":\"gateway_error\",\"message\":\"upstream unavailable\"}")]
    [InlineData(503, false, "{\"error\":\"unavailable\",\"message\":\"upstream unavailable\"}")]
    [InlineData(504, false, "{\"error\":\"gateway_timeout\",\"message\":\"upstream timed out\"}")]
    public async Task UnconfirmedResultsDoNotReplay(int status, bool acknowledgeResultVersion, string body)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var service = new KvLoopbackHttpServer(request => request.Path switch
        {
            SessionPath => BeginResponse(advertise: true),
            SessionPath + "/" + SessionId + "/sql/batch" => new(status, body,
                Location: status == 307 ? "/unexpected-redirect" : null,
                AdditionalHeaders: acknowledgeResultVersion ? $"{SqlFrameCodec.ResultVersionHeader}: 7\r\n" : null),
            SessionPath + "/" + SessionId + "/rollback" => new(200, StatementEnd),
            _ => new(404, "{}"),
        });
        await using var connection = new SndbConnection($"Mode=Remote;Protocol=rest;Data Source={service.Address};Database=demo;Timeout=4");
        await connection.OpenAsync(deadline.Token);
        await using var transaction = await Task.Run(() => connection.BeginTransaction(), deadline.Token);
        Assert.True(transaction.SupportsDmlBatch);
        var failure = await Assert.ThrowsAsync<SndbServerException>(() => transaction.ExecuteDmlBatchAsync(
            [new("UPDATE sample SET v = 2", new SndbParameterCollection(), 1, 3)], deadline.Token));
        Assert.Equal("sql_outcome_unknown", failure.Error);
        await transaction.RollbackAsync(deadline.Token);
        Assert.Equal([SessionPath, $"{SessionPath}/{SessionId}/sql/batch", $"{SessionPath}/{SessionId}/rollback"],
            service.Requests.Select(static request => request.Path));
    }

    [Theory]
    [InlineData(400, "sql_error")]
    [InlineData(503, "transaction_busy")]
    [InlineData(503, TimeSeriesWriteRecoveryRequiredException.ErrorCode)]
    [InlineData(503, TimeSeriesWriteRecoveryRequiredException.PartialErrorCode)]
    public async Task CompleteServerRejectionRetainsTheCodeAndStatus(int status, string code)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var service = new KvLoopbackHttpServer(request => request.Path switch
        {
            SessionPath => BeginResponse(advertise: true),
            SessionPath + "/" + SessionId + "/sql/batch" => new(status,
                $$"""{"error":"{{code}}","message":"database rejected statement"}"""),
            SessionPath + "/" + SessionId + "/rollback" => new(200, StatementEnd),
            _ => new(404, "{}"),
        });
        await using var connection = new SndbConnection($"Mode=Remote;Protocol=rest;Data Source={service.Address};Database=demo;Timeout=4");
        await connection.OpenAsync(deadline.Token);
        await using var transaction = await Task.Run(() => connection.BeginTransaction(), deadline.Token);
        var failure = await Assert.ThrowsAsync<SndbServerException>(() => transaction.ExecuteDmlBatchAsync(
            [new("UPDATE sample SET v = 2", new SndbParameterCollection(), 1, 3)], deadline.Token));
        Assert.Equal(code, failure.Error);
        Assert.Equal(status, (int)failure.StatusCode);
        await transaction.RollbackAsync(deadline.Token);
        Assert.Equal([SessionPath, $"{SessionPath}/{SessionId}/sql/batch", $"{SessionPath}/{SessionId}/rollback"],
            service.Requests.Select(static request => request.Path));
    }

    [Fact]
    public async Task OversizedBatchDoesNotSendOrEmitCommandStart()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var service = new KvLoopbackHttpServer(request => request.Path == SessionPath
            ? BeginResponse(advertise: true) : new(200, StatementEnd));
        await using var connection = new SndbConnection($"Mode=Remote;Protocol=rest;Data Source={service.Address};Database=demo;Timeout=4");
        await connection.OpenAsync(deadline.Token);
        await using var transaction = await Task.Run(() => connection.BeginTransaction(), deadline.Token);
        bool sending = false;
        string largeSql = "UPDATE sample SET v = '" + new string('x', SqlTransactionDmlBatchProtocol.MaxRequestBytes) + "'";
        Assert.Null(await transaction.ExecuteDmlBatchAsync([new(largeSql, new SndbParameterCollection(), 1, 3)],
            deadline.Token, () => { sending = true; return ValueTask.CompletedTask; }));
        Assert.False(sending);
        Assert.Equal(SessionPath, Assert.Single(service.Requests).Path);
        await transaction.RollbackAsync(deadline.Token);
    }

    private static KvLoopbackResponse BeginResponse(bool advertise)
        => new(200, $"{{\"id\":\"{SessionId}\"}}", AdditionalHeaders: advertise
            ? $"{SqlTransactionDmlBatchProtocol.CapabilityHeader}: {SqlTransactionDmlBatchProtocol.Version}\r\n" : null);
}
