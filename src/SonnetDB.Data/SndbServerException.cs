using System.Net;

namespace SonnetDB.Data;

/// <summary>
/// 服务端返回非 2xx 响应时抛出的异常。
/// </summary>
public sealed class SndbServerException : Exception
{
    /// <summary>构造一条服务端错误。</summary>
    public SndbServerException(string error, string message, HttpStatusCode statusCode)
        : base($"[{(int)statusCode} {statusCode}] {error}: {message}")
    {
        Error = error;
        ServerMessage = message;
        StatusCode = statusCode;
    }

    /// <summary>服务端给出的错误标识，例如 <c>unauthorized</c> / <c>forbidden</c> / <c>db_not_found</c> / <c>sql_error</c>。</summary>
    public string Error { get; }

    /// <summary>服务端给出的人类可读消息。</summary>
    public string ServerMessage { get; }

    /// <summary>HTTP 状态码。</summary>
    public HttpStatusCode StatusCode { get; }
}
