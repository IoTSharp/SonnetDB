using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.ObjectStorage;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>在真实 Kestrel 响应已开始后检查取消、断连与资源释放，不以预取消代替。</summary>
public sealed class ObjectTransferCancellationTests : IAsyncLifetime
{
    private const string Token = "object-cancel-test-token";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-object-cancel-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
    private readonly PausedResponse _observation = new();
    private WebApplication? _app;
    private string _connectionString = string.Empty;

    /// <summary>为每个用例启动独占真实服务，并安装只作用于指定对象 GET 的响应观测器。</summary>
    public async Task InitializeAsync()
    {
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = Path.Combine(_root, "server"),
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin },
        }, services => services.AddSingleton<IStartupFilter>(new PauseStartupFilter(_observation, _deadline.Token)));
        await _app.StartAsync(_deadline.Token);
        string url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var created = await http.PostAsJsonAsync("/v1/db", new CreateDatabaseRequest("cancel"),
            ServerJsonContext.Default.CreateDatabaseRequest, _deadline.Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _connectionString = $"Data Source=sonnetdb+http://{new Uri(url).Authority}/cancel;Token={Token};Protocol=rest;Timeout=15";
    }

    /// <summary>下载已收到响应正文后取消，或者释放未读完的响应，服务端均应退出并允许后续完整下载。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_InFlightCancellationOrDisconnect_ReleasesResponseAndPreservesFile(bool disconnect)
    {
        using var client = new SndbObjectStorageClient(_connectionString, useDedicatedHttpHandler: true);
        await client.CreateBucketAsync("cancel-bucket", cancellationToken: _deadline.Token);
        byte[] source = new byte[3 * 1024 * 1024];
        Array.Fill(source, (byte)0x5a);
        await client.PutObjectAsync("cancel-bucket", "payload.bin", new MemoryStream(source), cancellationToken: _deadline.Token);
        string target = Path.Combine(_root, "download.bin");
        await File.WriteAllTextAsync(target, "previous-file", _deadline.Token);
        _observation.Armed = true;
        var manager = new SndbObjectTransferManager(client);
        try
        {
            if (disconnect)
            {
                var opened = await client.OpenReadAsync("cancel-bucket", "payload.bin", cancellationToken: _deadline.Token);
                Assert.NotNull(opened);
                await using (var content = opened.Content)
                {
                    byte[] first = new byte[64];
                    await content.ReadExactlyAsync(first, _deadline.Token);
                    Assert.All(first, value => Assert.Equal((byte)0x5a, value));
                    await _observation.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
                }
            }
            else
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
                var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<SndbObjectTransferResult> download = manager.DownloadToFileAsync("cancel-bucket", "payload.bin", target,
                    progress: new InlineProgress(value => { if (value.Operation == "download") received.TrySetResult(); }), cancellationToken: cancel.Token);
                await _observation.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token));
            }
            // HTTP/1.1 的对端半关闭可能要到下一次真实写入才可见；先恢复写入，再核对断连和退出。
            _observation.Release.TrySetResult();
            await _observation.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
            await _observation.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
            Assert.Equal("previous-file", await File.ReadAllTextAsync(target, _deadline.Token));
            Assert.Empty(Directory.EnumerateFiles(_root, "*.sndb-download-*.tmp"));
        }
        finally
        {
            _observation.Armed = false;
            _observation.Release.TrySetResult();
        }

        var result = await manager.DownloadToFileAsync("cancel-bucket", "payload.bin", target, cancellationToken: _deadline.Token);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(source)), result.Sha256);
        Assert.Equal(source, await File.ReadAllBytesAsync(target, _deadline.Token));
    }

    /// <summary>真实分片请求失败后释放原客户端，新客户端从磁盘清单复用已确认分片并校验完整正文。</summary>
    [Fact]
    public async Task Multipart_NewClientAfterFailedPart_ResumesConfirmedPartsAndVerifiesContent()
    {
        byte[] source = new byte[5 * 64 * 1024];
        Array.Fill(source, (byte)0x6b);
        string manifest = Path.Combine(_root, "upload-resume.json");
        var options = new SndbObjectTransferOptions(64 * 1024, 64 * 1024, 1, 0, manifest);
        _observation.InjectPartFailure = true;
        using (var first = new SndbObjectStorageClient(_connectionString, useDedicatedHttpHandler: true))
        {
            await first.CreateBucketAsync("resume-bucket", cancellationToken: _deadline.Token);
            var manager = new SndbObjectTransferManager(first, options);
            Exception? failure = await Record.ExceptionAsync(() => manager.UploadAsync("resume-bucket", "resume.bin", new MemoryStream(source), cancellationToken: _deadline.Token));
            Assert.NotNull(failure);
            Assert.False(failure is OperationCanceledException);
            Assert.Equal(1, _observation.InjectedFailures);
            Assert.True(File.Exists(manifest));
            Assert.True(_app!.Services.GetRequiredService<TsdbRegistry>().TryGet("cancel", out var db));
            Assert.Single(new SndbObjectStore(db).ListMultipartUploads("resume-bucket").Uploads);
        }

        using var second = new SndbObjectStorageClient(_connectionString, useDedicatedHttpHandler: true);
        var resumed = new SndbObjectTransferManager(second, options);
        var operations = new List<string>();
        var result = await resumed.UploadAsync("resume-bucket", "resume.bin", new MemoryStream(source),
            progress: new InlineProgress(value => operations.Add(value.Operation)), cancellationToken: _deadline.Token);
        Assert.True(result.Resumed);
        Assert.Contains("resume", operations);
        Assert.False(File.Exists(manifest));
        using var downloaded = new MemoryStream();
        var readback = await resumed.DownloadAsync("resume-bucket", "resume.bin", downloaded, cancellationToken: _deadline.Token);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(source)), readback.Sha256);
        Assert.Equal(source, downloaded.ToArray());
    }

    /// <summary>仅回收本测试的服务与带随机身份的独占文件目录。</summary>
    public async Task DisposeAsync()
    {
        _observation.Release.TrySetResult();
        if (_app is not null)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.StopAsync(stop.Token);
            await _app.DisposeAsync();
        }
        _deadline.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(Path.GetFullPath(_root), recursive: true);
    }

    /// <summary>只保存单次响应的开始、断连及退出信号。</summary>
    private sealed class PausedResponse
    {
        internal bool Armed { get; set; }
        internal bool InjectPartFailure { get; set; }
        internal int InjectedFailures;
        internal TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>用真实 HTTP RequestAborted 验证服务端响应退出。</summary>
    private sealed class PauseStartupFilter(PausedResponse observation, CancellationToken deadline) : IStartupFilter
    {
        /// <summary>在产品端点之前安装仅供测试的正文背压。</summary>
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(handler => async context =>
            {
                if (observation.InjectPartFailure && context.Request.Method == "PUT" && context.Request.Query["partNumber"] == "2"
                    && Interlocked.CompareExchange(ref observation.InjectedFailures, 1, 0) == 0)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsync("{\"code\":\"injected_part_failure\",\"message\":\"test part failure\"}", context.RequestAborted);
                    return;
                }
                if (!observation.Armed || context.Request.Method != "GET" || !context.Request.Path.Value!.EndsWith("/payload.bin", StringComparison.Ordinal))
                {
                    await handler(context);
                    return;
                }
                Stream original = context.Response.Body;
                // 保持正文为 identity，固定首个真实对象数据块，避免压缩器先写 gzip 头造成观测歧义。
                context.Request.Headers.AcceptEncoding = "identity";
                context.Response.Body = new PausingWriteStream(original, observation, context.RequestAborted, deadline);
                using var registration = context.RequestAborted.Register(() => observation.Aborted.TrySetResult());
                try { await handler(context); }
                finally
                {
                    context.Response.Body = original;
                    observation.Exited.TrySetResult();
                }
            });
            next(app);
        };
    }

    /// <summary>同步记录进度，避免测试在异步 Progress 回调尚未投递时检查结果。</summary>
    private sealed class InlineProgress(Action<SndbObjectTransferProgress> report) : IProgress<SndbObjectTransferProgress>
    {
        /// <summary>将已确认的传输进度交给本用例的有界收集器。</summary>
        public void Report(SndbObjectTransferProgress value) => report(value);
    }

    /// <summary>发送第一个真实数据块后施加有超时、可取消的背压，内部流仍归 Kestrel 所有。</summary>
    private sealed class PausingWriteStream(Stream inner, PausedResponse observation, CancellationToken aborted, CancellationToken deadline) : Stream
    {
        private bool _paused;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>旧 Stream 重载同样进入可观测的异步写路径。</summary>
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <summary>首块暂停后只发一个有界探测块，保留未完成正文直到真实断连回调发生。</summary>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_paused)
            {
                _paused = true;
                // FileStream 允许一次交出完整正文，必须在转发剩余数据之前暂停，保证响应仍未发送完整。
                int first = Math.Min(64, buffer.Length);
                await inner.WriteAsync(buffer[..first], cancellationToken);
                await inner.FlushAsync(cancellationToken);
                observation.BodyStarted.TrySetResult();
                await observation.Release.Task.WaitAsync(TimeSpan.FromSeconds(15), deadline);
                buffer = buffer[first..];
            }
            if (buffer.IsEmpty)
                return;

            // 整个剩余正文可能被套接字一次接收，导致响应先完成、observer先注销。
            // 只再写/flush至多4KiB，使3MiB对象仍有未发送正文，真实取消传播期间保持handler存活。
            Exception? writeFailure = null;
            try
            {
                await inner.WriteAsync(buffer[..Math.Min(4096, buffer.Length)], cancellationToken);
                await inner.FlushAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                writeFailure = exception;
            }
            // 不用RequestAborted令牌取消此等待：它的取消回调可能先于唯一observer运行。
            // 必须等observer实际收到同一RequestAborted，再允许finally注销registration。
            await observation.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline);
            if (writeFailure is not null)
                ExceptionDispatchInfo.Throw(writeFailure);
            aborted.ThrowIfCancellationRequested();
            throw new InvalidOperationException("RequestAborted回调未对应真实取消令牌。");
        }
    }
}
