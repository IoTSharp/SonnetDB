using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SonnetDB.Data.Remote;

internal static class RemoteHttpClientFactory
{
    private static readonly ConcurrentDictionary<string, SocketsHttpHandler> Handlers = new(StringComparer.Ordinal);

    public static HttpClient Create(Uri baseAddress, string? token, TimeSpan timeout)
        => Create(baseAddress, username: null, password: null, token, timeout);

    /// <summary>
    /// 创建指向 <paramref name="baseAddress"/> 的 <see cref="HttpClient"/>。
    /// 认证优先级：<paramref name="username"/> 非空 → HTTP Basic（<c>user:password</c>）；
    /// 否则 <paramref name="token"/> 非空 → Bearer。
    /// </summary>
    public static HttpClient Create(Uri baseAddress, string? username, string? password, string? token, TimeSpan timeout)
    {
        var handler = Handlers.GetOrAdd(BuildHandlerKey(baseAddress), static _ => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxConnectionsPerServer = 64,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });

        var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            Timeout = timeout,
        };
        if (!string.IsNullOrWhiteSpace(username))
        {
            var raw = $"{username}:{password ?? string.Empty}";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }
        else if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static string BuildHandlerKey(Uri baseAddress) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{baseAddress.Scheme}://{baseAddress.IdnHost}:{baseAddress.Port}");
}
