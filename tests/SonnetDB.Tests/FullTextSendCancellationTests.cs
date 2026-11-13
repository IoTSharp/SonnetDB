using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using SonnetDB.Endpoints;
using SonnetDB.FullText;
using Xunit;

namespace SonnetDB.Tests;

public sealed class FullTextSendCancellationTests
{
    [Theory]
    [InlineData("write-cancel")]
    [InlineData("flush-cancel")]
    [InlineData("flush-deadline")]
    public async Task SendingCancellationAbortsWithoutAppendingAnErrorDocument(string phase)
    {
        using var caller = new CancellationTokenSource();
        using var budget = new FullTextSearchBudget(new FullTextSearchBudgetOptions
        {
            TimeoutMilliseconds = phase == "flush-deadline" ? 1_000 : 10_000,
        }, caller.Token);
        var lifetime = new RequestLifetime { RequestAborted = caller.Token };
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        using var stream = new SendingStream(phase, caller);
        context.Response.Body = stream;
        byte[] payload = "{\"hits\":[]}"u8.ToArray();
        await SonnetDbEndpoints.WriteFullTextResponseAsync(context, payload, budget)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(lifetime.Aborted);
        Assert.Equal(1, stream.WriteCalls);
        Assert.Equal(phase == "write-cancel" ? 0 : 1, stream.FlushCalls);
        Assert.Equal(phase == "write-cancel" ? 0 : payload.Length, stream.Length);
        Assert.Equal(payload.Length, context.Response.ContentLength);
    }

    [Fact]
    public async Task NormalSendCompletesWriteAndFlushWithinTheBudget()
    {
        using var caller = new CancellationTokenSource();
        using var budget = new FullTextSearchBudget(cancellationToken: caller.Token);
        var lifetime = new RequestLifetime { RequestAborted = caller.Token };
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        using var stream = new SendingStream("normal", caller);
        context.Response.Body = stream;
        await SonnetDbEndpoints.WriteFullTextResponseAsync(context, "{}"u8.ToArray(), budget);
        Assert.False(lifetime.Aborted);
        Assert.Equal(1, stream.WriteCalls);
        Assert.Equal(1, stream.FlushCalls);
        Assert.Equal(2, stream.Length);
    }

    private sealed class RequestLifetime : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private sealed class SendingStream(string phase, CancellationTokenSource caller) : MemoryStream
    {
        public int WriteCalls { get; private set; }
        public int FlushCalls { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            if (phase == "write-cancel")
                caller.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            base.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            if (phase == "flush-cancel")
                caller.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (phase == "flush-deadline")
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                await completion.Task;
            }
        }
    }
}
