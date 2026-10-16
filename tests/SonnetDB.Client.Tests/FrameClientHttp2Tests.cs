using System.Buffers;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Client.Tests;

public sealed class FrameClientHttp2Tests
{
    [Fact]
    public async Task SendUnary_RealH2c_UsesHttp2AndPreservesBinaryPayload()
    {
        string? protocol = null;
        string? authorization = null;
        byte[] expected = [0, 255, 42];
        await using var server = await LoopbackServer.StartAsync(async context =>
        {
            protocol = context.Request.Protocol;
            authorization = context.Request.Headers.Authorization;
            byte[] input = new byte[FrameHeader.Size + expected.Length];
            await context.Request.Body.ReadExactlyAsync(input, context.RequestAborted);
            var sequence = new ReadOnlySequence<byte>(input);
            Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out var payload));
            Assert.Equal(expected, payload.ToArray());
            Assert.Equal(42u, header.StreamId);
            await WriteReplyAsync(context, expected);
        });
        using var client = new SndbFrameClient(server.Address, "configured-token", timeout: TimeSpan.FromSeconds(5));
        Assert.Equal(expected, (await client.SendUnaryAsync(5, 1, 42, expected)).ToArray());
        Assert.Equal("HTTP/2", protocol);
        Assert.Equal("Bearer configured-token", authorization);
    }

    [Fact]
    public async Task SendUnary_RealRedirect_DoesNotFollow()
    {
        await using var server = await LoopbackServer.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = "/unexpected";
            return Task.CompletedTask;
        });
        using var client = new SndbFrameClient(server.Address, timeout: TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<SndbFrameException>(() => client.SendUnaryAsync(5, 1, 42, new byte[1]));
        Assert.Equal("http_error", failure.Code);
        Assert.Contains("307", failure.Message);
        Assert.True(failure.OutcomeUnknown);
    }

    [Fact]
    public async Task SendUnary_CancelDuringRealBody_ReleasesStreamAndConnectionRemainsUsable()
    {
        int requests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await LoopbackServer.StartAsync(async context =>
        {
            if (Interlocked.Increment(ref requests) > 1)
            {
                await WriteReplyAsync(context, [7]);
                return;
            }
            await WritePartialAsync(context);
            started.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { aborted.TrySetResult(); }
        });
        using var client = new SndbFrameClient(server.Address, timeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var pending = client.SendUnaryAsync(5, 1, 42, new byte[1], cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var failure = await Assert.ThrowsAsync<SndbFrameCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.True(failure.OutcomeUnknown);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 7 }, (await client.SendUnaryAsync(5, 1, 42, new byte[1])).ToArray());
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task SendUnary_RealBodyNeverCompletes_EnforcesTotalDeadline()
    {
        int requests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await LoopbackServer.StartAsync(async context =>
        {
            Interlocked.Increment(ref requests);
            await WritePartialAsync(context);
            started.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { aborted.TrySetResult(); }
        });
        using var client = new SndbFrameClient(server.Address, timeout: TimeSpan.FromSeconds(1));
        var pending = client.SendUnaryAsync(5, 1, 42, new byte[1]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<SndbFrameException>(() => pending);
        Assert.Equal("request_timeout", failure.Code);
        Assert.True(failure.OutcomeUnknown);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task SendUnary_DisposeDuringRealBody_AbortsStreamAndKeepsUnknownOutcome()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await LoopbackServer.StartAsync(async context =>
        {
            await WritePartialAsync(context);
            started.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { aborted.TrySetResult(); }
        });
        var client = new SndbFrameClient(server.Address, timeout: TimeSpan.FromSeconds(5));
        using (client)
        {
            var pending = client.SendUnaryAsync(5, 1, 42, new byte[1]);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.Dispose();
            var failure = await Assert.ThrowsAsync<SndbFrameException>(() => pending);
            Assert.Equal("frame_transport", failure.Code);
            Assert.True(failure.OutcomeUnknown);
            await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task WriteReplyAsync(HttpContext context, byte[] payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        FrameCodec.WriteFrame(buffer, new FrameHeader((uint)payload.Length, FrameHeader.CurrentVersion,
            5, 1, (byte)FrameFlags.Response, 42), payload);
        context.Response.ContentType = "application/x-sonnetdb-frame";
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted);
    }

    private static async Task WritePartialAsync(HttpContext context)
    {
        byte[] bytes = new byte[FrameHeader.Size + 1];
        new FrameHeader(2, FrameHeader.CurrentVersion, 5, 1, (byte)FrameFlags.Response, 42).Write(bytes);
        context.Response.ContentType = "application/x-sonnetdb-frame";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private sealed class LoopbackServer(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public static async Task<LoopbackServer> StartAsync(RequestDelegate handler)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.Protocols = HttpProtocols.Http2));
            var app = builder.Build();
            app.MapPost("/v1/frame", handler);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await app.StartAsync(deadline.Token);
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                return new LoopbackServer(app, new Uri(addresses.Addresses.Single() + "/"));
            }
            catch
            {
                await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(deadline.Token); }
            finally { await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
