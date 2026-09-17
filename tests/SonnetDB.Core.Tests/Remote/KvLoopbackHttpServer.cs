using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SonnetDB.Core.Tests.Remote;

internal sealed class KvLoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
    private readonly ConcurrentQueue<KvLoopbackRequest> _requests = new();
    private readonly Func<KvLoopbackRequest, KvLoopbackResponse> _respond;
    private readonly Task _serving;

    public KvLoopbackHttpServer(Func<KvLoopbackRequest, KvLoopbackResponse> respond)
    {
        _respond = respond;
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Address = new Uri($"http://127.0.0.1:{endpoint.Port}/");
        _serving = ServeAsync();
    }

    public Uri Address { get; }

    public KvLoopbackRequest[] Requests => _requests.ToArray();

    public async ValueTask DisposeAsync()
    {
        await _deadline.CancelAsync();
        _listener.Stop();
        try
        {
            await _serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (_deadline.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_deadline.IsCancellationRequested)
        {
        }
        finally
        {
            _deadline.Dispose();
        }
    }

    private async Task ServeAsync()
    {
        CancellationToken cancellationToken = _deadline.Token;
        // 每连接仅处理一个请求；本 helper 的所有用例最多产生三个请求。
        for (int requestIndex = 0; requestIndex < 8; requestIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            string requestLine = await ReadLineAsync(reader, cancellationToken);
            string[] start = requestLine.Split(' ', 3);
            if (start.Length != 3)
                throw new InvalidDataException("Invalid loopback HTTP request line.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool headersComplete = false;
            for (int headerIndex = 0; headerIndex < 32; headerIndex++)
            {
                string line = await ReadLineAsync(reader, cancellationToken);
                if (line.Length == 0)
                {
                    headersComplete = true;
                    break;
                }
                int separator = line.IndexOf(':');
                if (separator <= 0)
                    throw new InvalidDataException("Invalid loopback HTTP header.");
                headers.Add(line[..separator], line[(separator + 1)..].Trim());
            }
            if (!headersComplete)
                throw new InvalidDataException("Loopback HTTP header count exceeds 32.");
            if (headers.TryGetValue("Transfer-Encoding", out string? transferEncoding))
            {
                if (!string.Equals(transferEncoding, "chunked", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unsupported loopback transfer encoding.");
                await DrainChunksAsync(reader, cancellationToken);
            }
            else if (headers.TryGetValue("Content-Length", out string? length))
            {
                await DrainAsync(reader, int.Parse(length, CultureInfo.InvariantCulture), cancellationToken);
            }

            var request = new KvLoopbackRequest(start[0], start[1], headers.GetValueOrDefault("Authorization"));
            _requests.Enqueue(request);
            KvLoopbackResponse response = _respond(request);
            byte[] body = Encoding.UTF8.GetBytes(response.Json);
            string redirect = response.Location is null ? string.Empty : $"Location: {response.Location}\r\n";
            byte[] responseHeaders = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {response.Status} Test\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n{redirect}Connection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? line = await reader.ReadLineAsync(cancellationToken);
        if (line is null || line.Length > 4096)
            throw new InvalidDataException("Missing or oversized loopback HTTP line.");
        return line;
    }

    private static async Task DrainChunksAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        int totalBytes = 0;
        for (int chunkIndex = 0; chunkIndex < 64; chunkIndex++)
        {
            string line = await ReadLineAsync(reader, cancellationToken);
            int count = int.Parse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            totalBytes = checked(totalBytes + count);
            if (totalBytes > 64 * 1024)
                throw new InvalidDataException("Loopback HTTP body exceeds 64 KiB.");
            if (count == 0)
            {
                if ((await ReadLineAsync(reader, cancellationToken)).Length != 0)
                    throw new InvalidDataException("Loopback HTTP trailers are not supported.");
                return;
            }
            await DrainAsync(reader, count, cancellationToken);
            if ((await ReadLineAsync(reader, cancellationToken)).Length != 0)
                throw new InvalidDataException("Invalid loopback chunk terminator.");
        }
        throw new InvalidDataException("Loopback HTTP chunk count exceeds 64.");
    }

    private static async Task DrainAsync(StreamReader reader, int count, CancellationToken cancellationToken)
    {
        if (count is < 0 or > 64 * 1024)
            throw new InvalidDataException("Invalid loopback HTTP body length.");
        char[] buffer = new char[Math.Min(count, 64 * 1024)];
        int read = await reader.ReadBlockAsync(buffer, cancellationToken);
        if (read != count)
            throw new InvalidDataException("Truncated loopback HTTP request body.");
    }
}

internal sealed record KvLoopbackRequest(string Method, string Path, string? Authorization);

internal sealed record KvLoopbackResponse(int Status, string Json, string? Location = null);
