using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SonnetDB.Data.Remote;

namespace SonnetDB.Core.Tests.Remote;

public sealed class RemoteHttpClientFactoryTests
{
    [Fact]
    public async Task CreateDedicated_AfterSharedRequest_UsesSeparateConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handlers = new ConcurrentBag<Task>();
        var acceptedConnections = 0;
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var acceptLoop = AcceptConnectionsAsync(
            listener,
            handlers,
            () => Interlocked.Increment(ref acceptedConnections),
            cancellation.Token);

        try
        {
            var baseAddress = new Uri($"http://127.0.0.1:{endpoint.Port}/");
            using var shared = RemoteHttpClientFactory.Create(
                baseAddress, username: null, password: null, token: null, TimeSpan.FromSeconds(5));
            using var dedicated = RemoteHttpClientFactory.CreateDedicated(
                baseAddress, username: null, password: null, token: null, TimeSpan.FromSeconds(5));

            using (var response = await shared.GetAsync("shared", cancellation.Token))
            {
                response.EnsureSuccessStatusCode();
            }

            using (var response = await dedicated.GetAsync("dedicated", cancellation.Token))
            {
                response.EnsureSuccessStatusCode();
            }

            Assert.Equal(2, Volatile.Read(ref acceptedConnections));
        }
        finally
        {
            await cancellation.CancelAsync();
            listener.Stop();
            await IgnoreCancellationAsync(acceptLoop);
            await Task.WhenAll(handlers.Select(IgnoreCancellationAsync));
        }
    }

    private static async Task AcceptConnectionsAsync(
        TcpListener listener,
        ConcurrentBag<Task> handlers,
        Action onAccepted,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                onAccepted();
                handlers.Add(ServeConnectionAsync(client, cancellationToken));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private static async Task ServeConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var requestLine = await reader.ReadLineAsync(cancellationToken);
                    if (requestLine is null)
                        return;

                    string? header;
                    do
                    {
                        header = await reader.ReadLineAsync(cancellationToken);
                    }
                    while (!string.IsNullOrEmpty(header));

                    var response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");
                    await stream.WriteAsync(response, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
            }
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }
}
