using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

public sealed class SqlHttpRequestAdmissionTests
{
    [Fact]
    public async Task AcquireAsync_EnforcesHardPermitAndQueueLimitsPerDatabase()
    {
        using var admission = CreateAdmission(permitLimit: 2, queueLimit: 2);
        RateLimitLease? first = await admission.AcquireAsync("orders", CancellationToken.None);
        RateLimitLease? second = await admission.AcquireAsync("ORDERS", CancellationToken.None);
        RateLimitLease? third = null;
        RateLimitLease? fourth = null;

        try
        {
            Assert.True(first.IsAcquired);
            Assert.True(second.IsAcquired);

            Task<RateLimitLease> thirdTask = admission.AcquireAsync("orders", CancellationToken.None).AsTask();
            Task<RateLimitLease> fourthTask = admission.AcquireAsync("orders", CancellationToken.None).AsTask();
            Assert.False(thirdTask.IsCompleted);
            Assert.False(fourthTask.IsCompleted);

            using RateLimitLease rejected = await admission.AcquireAsync("orders", CancellationToken.None);
            Assert.False(rejected.IsAcquired);

            using RateLimitLease otherDatabase = await admission.AcquireAsync("inventory", CancellationToken.None);
            Assert.True(otherDatabase.IsAcquired);

            first.Dispose();
            first = null;
            third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(third.IsAcquired);
            Assert.False(fourthTask.IsCompleted);

            second.Dispose();
            second = null;
            fourth = await fourthTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fourth.IsAcquired);
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
            third?.Dispose();
            fourth?.Dispose();
        }
    }

    [Fact]
    public async Task AcquireAsync_CanceledWaitLeavesQueueReusableAndCompletionReleasesPermit()
    {
        using var admission = CreateAdmission(permitLimit: 1, queueLimit: 1);
        RateLimitLease? active = await admission.AcquireAsync("orders", CancellationToken.None);
        RateLimitLease? replacement = null;

        try
        {
            using var cancellation = new CancellationTokenSource();
            Task<RateLimitLease> canceledWait = admission.AcquireAsync("orders", cancellation.Token).AsTask();
            Assert.False(canceledWait.IsCompleted);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledWait);

            Task<RateLimitLease> replacementTask = admission.AcquireAsync("orders", CancellationToken.None).AsTask();
            Assert.False(replacementTask.IsCompleted);

            active.Dispose();
            active = null;
            replacement = await replacementTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(replacement.IsAcquired);
        }
        finally
        {
            active?.Dispose();
            replacement?.Dispose();
        }
    }

    [Theory]
    [InlineData("/v1/db/admission/sql", "{\"sql\":\"SHOW MEASUREMENTS\"}")]
    [InlineData("/v1/db/admission/sql/batch", "{\"statements\":[{\"sql\":\"SHOW MEASUREMENTS\"}]}")]
    public async Task Overload_IsRejectedBeforeRequestBodyIsRead(string path, string payload)
    {
        await using var server = await AdmissionTestServer.StartAsync(permitLimit: 1, queueLimit: 1);
        RateLimitLease? activeLease = await server.Admission.AcquireAsync("admission", CancellationToken.None);
        Task<RateLimitLease> queuedLeaseTask = server.Admission
            .AcquireAsync("admission", CancellationToken.None)
            .AsTask();
        RateLimitLease? queuedLease = null;

        try
        {
            Assert.True(activeLease.IsAcquired);
            Assert.False(queuedLeaseTask.IsCompleted);

            string responseHeaders = await SendHeadersWithoutBodyAsync(
                server.Client.BaseAddress!,
                path,
                Encoding.UTF8.GetByteCount(payload));

            Assert.StartsWith("HTTP/1.1 503", responseHeaders, StringComparison.Ordinal);
            Assert.Contains("Retry-After: 1\r\n", responseHeaders, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            activeLease.Dispose();
            activeLease = null;
            queuedLease = await queuedLeaseTask.WaitAsync(TimeSpan.FromSeconds(5));
            queuedLease.Dispose();
        }
    }

    [Fact]
    public async Task CompletedActiveRequest_ReleasesPermitForQueuedRequest()
    {
        const string path = "/v1/db/admission/sql";
        const string payload = "{\"sql\":\"SHOW MEASUREMENTS\"}";
        await using var server = await AdmissionTestServer.StartAsync(permitLimit: 1, queueLimit: 1);
        var activeBody = new GatedJsonContent(payload);
        var queuedBody = new TrackingJsonContent(payload);
        using var activeRequest = CreateRequest(path, activeBody);
        using var queuedRequest = CreateRequest(path, queuedBody);

        Task<HttpResponseMessage> activeTask = server.Client.SendAsync(
            activeRequest,
            HttpCompletionOption.ResponseHeadersRead);
        Task<HttpResponseMessage>? queuedTask = null;

        try
        {
            await activeBody.SerializationStarted.WaitAsync(TimeSpan.FromSeconds(10));
            queuedTask = server.Client.SendAsync(queuedRequest, HttpCompletionOption.ResponseHeadersRead);
            await Task.Delay(100);
            Assert.False(queuedBody.SerializeCalled);

            activeBody.Release();
            using HttpResponseMessage activeResponse = await activeTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, activeResponse.StatusCode);

            await queuedBody.SerializationStarted.WaitAsync(TimeSpan.FromSeconds(10));
            using HttpResponseMessage queuedResponse = await queuedTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, queuedResponse.StatusCode);
        }
        finally
        {
            activeBody.Release();
            await ObserveCompletionAsync(activeTask);
            if (queuedTask is not null)
                await ObserveCompletionAsync(queuedTask);
        }
    }

    [Fact]
    public async Task CanceledActiveRequest_ReleasesPermitForQueuedRequest()
    {
        const string path = "/v1/db/admission/sql";
        const string payload = "{\"sql\":\"SHOW MEASUREMENTS\"}";
        await using var server = await AdmissionTestServer.StartAsync(permitLimit: 1, queueLimit: 1);
        var activeBody = new GatedJsonContent(payload);
        var queuedBody = new TrackingJsonContent(payload);
        using var activeRequest = CreateRequest(path, activeBody);
        using var queuedRequest = CreateRequest(path, queuedBody);
        using var activeCancellation = new CancellationTokenSource();

        Task<HttpResponseMessage> activeTask = server.Client.SendAsync(
            activeRequest,
            HttpCompletionOption.ResponseHeadersRead,
            activeCancellation.Token);
        Task<HttpResponseMessage>? queuedTask = null;

        try
        {
            await activeBody.SerializationStarted.WaitAsync(TimeSpan.FromSeconds(10));
            queuedTask = server.Client.SendAsync(queuedRequest, HttpCompletionOption.ResponseHeadersRead);
            await Task.Delay(100);
            Assert.False(queuedBody.SerializeCalled);

            activeCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await activeTask.WaitAsync(TimeSpan.FromSeconds(10)));

            await queuedBody.SerializationStarted.WaitAsync(TimeSpan.FromSeconds(10));
            using HttpResponseMessage queuedResponse = await queuedTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, queuedResponse.StatusCode);
        }
        finally
        {
            activeBody.Release();
            await ObserveCompletionAsync(activeTask);
            if (queuedTask is not null)
                await ObserveCompletionAsync(queuedTask);
        }
    }

    [Fact]
    public async Task RestPermitAndQueue_AreSharedWithFrameSql_AndOverloadSkipsParsing()
    {
        const string restPath = "/v1/db/admission/sql";
        const string restPayload = "{\"sql\":\"SHOW MEASUREMENTS\"}";
        await using var server = await AdmissionTestServer.StartAsync(permitLimit: 1, queueLimit: 1);
        var activeBody = new GatedJsonContent(restPayload);
        var queuedBody = new TrackingJsonContent(restPayload);
        using var activeRequest = CreateRequest(restPath, activeBody);
        using var queuedRequest = CreateRequest(restPath, queuedBody);
        Task<HttpResponseMessage> activeTask = server.Client.SendAsync(
            activeRequest,
            HttpCompletionOption.ResponseHeadersRead);
        Task<HttpResponseMessage>? queuedTask = null;

        try
        {
            await activeBody.SerializationStarted.WaitAsync(TimeSpan.FromSeconds(10));
            queuedTask = server.Client.SendAsync(queuedRequest, HttpCompletionOption.ResponseHeadersRead);
            await WaitForQueueCountAsync(server.Admission, "admission", expectedCount: 1);

            const uint streamId = 73;
            using HttpResponseMessage frameResponse = await PostSqlFrameAsync(
                server.FrameClient,
                streamId,
                "admission",
                "THIS IS DELIBERATELY NOT VALID SQL");

            Assert.Equal(HttpStatusCode.OK, frameResponse.StatusCode);
            Assert.Equal(HttpVersion.Version20, frameResponse.Version);
            Assert.True(frameResponse.Headers.TryGetValues("Retry-After", out var retryAfterValues));
            Assert.Equal("1", Assert.Single(retryAfterValues));

            byte[] responseBody = await frameResponse.Content.ReadAsByteArrayAsync();
            var buffer = new ReadOnlySequence<byte>(responseBody);
            Assert.True(FrameCodec.TryReadFrame(ref buffer, out FrameHeader header, out ReadOnlySequence<byte> payload));
            Assert.Equal(0, buffer.Length);
            Assert.Equal((byte)FrameService.Sql, header.Service);
            Assert.Equal((byte)SqlFrameOp.Query, header.Op);
            Assert.Equal(streamId, header.StreamId);
            Assert.True(header.IsResponse);
            Assert.True(header.IsError);
            (string code, _) = FrameCodec.ReadErrorPayload(payload.ToArray());
            Assert.Equal("sql_overloaded", code);

            activeBody.Release();
            using HttpResponseMessage activeResponse = await activeTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, activeResponse.StatusCode);
            using HttpResponseMessage queuedResponse = await queuedTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, queuedResponse.StatusCode);
        }
        finally
        {
            activeBody.Release();
            await ObserveCompletionAsync(activeTask);
            if (queuedTask is not null)
                await ObserveCompletionAsync(queuedTask);
        }
    }

    [Fact]
    public async Task CanceledQueuedFrameSql_RemovesItsSharedQueueEntry()
    {
        await using var server = await AdmissionTestServer.StartAsync(permitLimit: 1, queueLimit: 1);
        RateLimitLease? activeLease = await server.Admission.AcquireAsync("admission", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<HttpResponseMessage> frameTask = PostSqlFrameAsync(
            server.FrameClient,
            streamId: 74,
            database: "admission",
            sql: "SHOW MEASUREMENTS",
            cancellation.Token);

        try
        {
            Assert.True(activeLease.IsAcquired);
            await WaitForQueueCountAsync(server.Admission, "admission", expectedCount: 1);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await frameTask.WaitAsync(TimeSpan.FromSeconds(10)));
            await WaitForQueueCountAsync(server.Admission, "admission", expectedCount: 0);

            activeLease.Dispose();
            activeLease = null;
            using RateLimitLease replacement = await server.Admission.AcquireAsync(
                "admission",
                CancellationToken.None);
            Assert.True(replacement.IsAcquired);
        }
        finally
        {
            cancellation.Cancel();
            activeLease?.Dispose();
            await ObserveCompletionAsync(frameTask);
        }
    }

    private static SqlHttpRequestAdmission CreateAdmission(int permitLimit, int queueLimit)
        => new(Options.Create(new ServerOptions
        {
            SqlHttpAdmission = new SqlHttpAdmissionOptions
            {
                PermitLimit = permitLimit,
                QueueLimit = queueLimit,
            },
        }));

    private static HttpRequestMessage CreateRequest(string path, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.ExpectContinue = true;
        return request;
    }

    private static async Task<HttpResponseMessage> PostSqlFrameAsync(
        HttpClient client,
        uint streamId,
        string database,
        string sql,
        CancellationToken cancellationToken = default)
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(writer, streamId, database, sql, parameters: null);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/frame")
        {
            Content = new ByteArrayContent(writer.WrittenMemory.ToArray()),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(FrameEndpointHandler.ContentType);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task WaitForQueueCountAsync(
        SqlHttpRequestAdmission admission,
        string database,
        long expectedCount)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (admission.GetStatistics(database)?.CurrentQueuedCount == expectedCount)
                return;
            await Task.Delay(10);
        }

        long? actual = admission.GetStatistics(database)?.CurrentQueuedCount;
        throw new TimeoutException($"等待 SQL 准入队列长度 {expectedCount} 超时，实际 {actual?.ToString() ?? "<null>"}。");
    }

    private static async Task<string> SendHeadersWithoutBodyAsync(Uri baseAddress, string path, int contentLength)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(baseAddress.Host, baseAddress.Port).WaitAsync(TimeSpan.FromSeconds(5));
        await using NetworkStream stream = client.GetStream();
        string requestHeaders =
            $"POST {path} HTTP/1.1\r\n" +
            $"Host: {baseAddress.Host}:{baseAddress.Port}\r\n" +
            $"Authorization: Bearer {AdmissionTestServer.AdminToken}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {contentLength}\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(requestHeaders));
        await stream.FlushAsync();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = new StringBuilder();
        byte[] buffer = new byte[1024];
        while (!response.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(buffer, cancellation.Token);
            if (read == 0)
                break;
            response.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return response.ToString();
    }

    private static async Task ObserveCompletionAsync(Task<HttpResponseMessage> responseTask)
    {
        try
        {
            using HttpResponseMessage response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or TimeoutException)
        {
            // Cleanup path: the assertions above already cover the expected result.
        }
    }

    private sealed class AdmissionTestServer : IAsyncDisposable
    {
        internal const string AdminToken = "sql-admission-admin";
        private readonly WebApplication _app;
        private readonly string _dataRoot;

        private AdmissionTestServer(
            WebApplication app,
            string dataRoot,
            HttpClient client,
            HttpClient frameClient,
            SqlHttpRequestAdmission admission)
        {
            _app = app;
            _dataRoot = dataRoot;
            Client = client;
            FrameClient = frameClient;
            Admission = admission;
        }

        public HttpClient Client { get; }

        public HttpClient FrameClient { get; }

        public SqlHttpRequestAdmission Admission { get; }

        public static async Task<AdmissionTestServer> StartAsync(int permitLimit, int queueLimit)
        {
            string dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-sql-admission-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            var options = new ServerOptions
            {
                DataRoot = dataRoot,
                AutoLoadExistingDatabases = false,
                Tokens = new Dictionary<string, string>
                {
                    [AdminToken] = ServerRoles.Admin,
                },
                SqlHttpAdmission = new SqlHttpAdmissionOptions
                {
                    PermitLimit = permitLimit,
                    QueueLimit = queueLimit,
                },
            };

            WebApplication app = TestServerHost.Build(options, extraArgs:
            [
                "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
                "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
            ]);
            Assert.True(app.Services.GetRequiredService<TsdbRegistry>().TryCreate("admission", out _));
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            string? http11Address = null;
            string? frameHttp2Address = null;
            foreach (string address in addresses.Addresses)
            {
                if (await ProbeIsHttp11Async(address))
                    http11Address = address;
                else
                    frameHttp2Address = address;
            }
            if (http11Address is null || frameHttp2Address is null)
                throw new InvalidOperationException("未能区分 SQL admission 测试的 HTTP/1.1 与 h2c 端口。");

            var handler = new SocketsHttpHandler
            {
                Expect100ContinueTimeout = TimeSpan.FromSeconds(30),
            };
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(http11Address),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
            var frameClient = new HttpClient
            {
                BaseAddress = new Uri(frameHttp2Address),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            frameClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
            return new AdmissionTestServer(
                app,
                dataRoot,
                client,
                frameClient,
                app.Services.GetRequiredService<SqlHttpRequestAdmission>());
        }

        private static async Task<bool> ProbeIsHttp11Async(string address)
        {
            using var client = new HttpClient();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                using HttpResponseMessage response = await client.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            FrameClient.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            try
            {
                if (Directory.Exists(_dataRoot))
                    Directory.Delete(_dataRoot, recursive: true);
            }
            catch
            {
                // best effort cleanup for test data
            }
        }
    }

    private class TrackingJsonContent : HttpContent
    {
        private readonly byte[] _payload;
        private int _serializeCalled;

        public TrackingJsonContent(string payload)
        {
            _payload = Encoding.UTF8.GetBytes(payload);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        public bool SerializeCalled => Volatile.Read(ref _serializeCalled) != 0;

        public Task SerializationStarted => _serializationStarted.Task;

        private readonly TaskCompletionSource _serializationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _serializeCalled, 1);
            _serializationStarted.TrySetResult();
            await WritePayloadAsync(stream, cancellationToken);
        }

        protected virtual ValueTask WritePayloadAsync(Stream stream, CancellationToken cancellationToken)
            => stream.WriteAsync(_payload, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }
    }

    private sealed class GatedJsonContent(string payload) : TrackingJsonContent(payload)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        protected override async ValueTask WritePayloadAsync(Stream stream, CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken);
            await base.WritePayloadAsync(stream, cancellationToken);
        }
    }

}
