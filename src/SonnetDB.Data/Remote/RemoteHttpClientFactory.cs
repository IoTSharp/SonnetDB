using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace SonnetDB.Data.Remote;

internal static class RemoteHttpClientFactory
{
    private static readonly ConcurrentDictionary<string, SocketsHttpHandler> Handlers = new(StringComparer.Ordinal);

    public static HttpClient Create(Uri baseAddress, string? token, TimeSpan timeout)
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
        if (!string.IsNullOrWhiteSpace(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    private static string BuildHandlerKey(Uri baseAddress) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{baseAddress.Scheme}://{baseAddress.IdnHost}:{baseAddress.Port}");
}
