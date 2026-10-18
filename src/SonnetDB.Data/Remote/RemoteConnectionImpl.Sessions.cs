using System.Net;
using System.Text.Json;
using SonnetDB.Data.Internal;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Data.Remote;

internal sealed partial class RemoteConnectionImpl
{
    private const string ConnectionSessionHeader = "X-SonnetDB-Connection-Session";
    private const int MaximumCreateResponseBytes = 16 * 1024;
    private static readonly TimeSpan ConnectionSessionGateWait = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _connectionSessionGate = new(1, 1);
    private string? _connectionSessionId;

    private async Task EnsureConnectionSessionForSqlAsync(string sql, CancellationToken cancellationToken)
    {
        if (_connectionSessionId is not null || !RequiresConnectionSession(sql))
            return;
        await EnsureConnectionSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureConnectionSessionAsync(CancellationToken cancellationToken)
    {
        if (_connectionSessionId is not null)
            return;
        if (!await _connectionSessionGate.WaitAsync(ConnectionSessionGateWait, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("等待远程 SQL 连接会话创建准入超时。");
        try
        {
            if (_connectionSessionId is not null)
                return;
            if (_http is null || _state != System.Data.ConnectionState.Open)
                throw new InvalidOperationException("连接未打开。");
            using var request = CreateRequest(HttpMethod.Post,
                $"v1/db/{Uri.EscapeDataString(_database)}/sql/sessions");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                throw new SndbServerException(
                    "connection_session_not_supported",
                    "服务端不支持连接级临时关系表；TEMP SQL 未发送，请升级服务端后重试。",
                    response.StatusCode);
            }
            if (!response.IsSuccessStatusCode)
                throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaximumCreateResponseBytes)
                throw new InvalidDataException("远程 SQL 连接会话创建响应超过字节预算。");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] body = await BoundedContentReader.ReadAsync(
                stream, MaximumCreateResponseBytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("id", out JsonElement id)
                || id.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(id.GetString(), "N", out _))
            {
                throw new InvalidDataException("远程 SQL 连接会话创建响应缺少有效 ID。");
            }
            _connectionSessionId = id.GetString();
        }
        finally
        {
            _connectionSessionGate.Release();
        }
    }

    private async Task CloseConnectionSessionAsync(CancellationToken cancellationToken)
    {
        string? id = Interlocked.Exchange(ref _connectionSessionId, null);
        if (id is null || _http is null)
            return;
        using var request = CreateRequest(HttpMethod.Delete,
            $"v1/db/{Uri.EscapeDataString(_database)}/sql/sessions/{id}");
        request.Headers.Remove(ConnectionSessionHeader);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private void CloseConnectionSessionBestEffort()
    {
        if (_connectionSessionId is null || _http is null)
            return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            CloseConnectionSessionAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or OperationCanceledException or SndbServerException)
        {
            // 服务端租约会回收失联连接；Close 不能因清理回执丢失而保留本地连接资源。
        }
    }

    private void ResetConnectionSessionForDatabaseSwitch()
        => CloseConnectionSessionBestEffort();

    private void ApplyConnectionSession(HttpRequestMessage request)
    {
        string? id = _connectionSessionId;
        if (id is not null)
            request.Headers.TryAddWithoutValidation(ConnectionSessionHeader, id);
    }

    private static bool RequiresConnectionSession(string sql)
    {
        try
        {
            return SqlParser.Parse(sql) is CreateTableStatement { IsTemporary: true }
                or CreateTableAsStatement { IsTemporary: true };
        }
        catch (SqlParseException)
        {
            return false;
        }
    }
}
