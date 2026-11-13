using System.Net;
using System.Text;
using SonnetDB.Data.FullText;
using SonnetDB.Exceptions;
using SonnetDB.FullText;

namespace SonnetDB.Core.Tests.Documents;

public sealed class FullTextTransportBudgetTests
{
    [Fact]
    public async Task OversizedErrorBodyIsBoundedAndDoesNotBecomeAConsumableResult()
    {
        var stream = new FragmentedStream(Encoding.UTF8.GetBytes(new string('x', 4_096)));
        using var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StreamContent(stream),
        });
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() => client.SearchAsync(
            "logs", "by_message", "$.message", "shared",
            new SndbFullTextSearchOptions { Budget = new FullTextSearchBudgetOptions { MaxResponseBytes = 128 } }));
        Assert.InRange(stream.BytesRead, 128, 129);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task FragmentedErrorRetainsTypedBudgetErrorAndReadsBeyondOldChunkCount()
    {
        string body = "{\"error\":\"fulltext_search_budget_exceeded\",\"detail\":\"" + new string('x', 1_000) + "\"}";
        var stream = new FragmentedStream(Encoding.UTF8.GetBytes(body));
        using var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StreamContent(stream),
        });
        using var client = CreateClient(handler);
        FullTextSearchBudgetExceededException error = await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() =>
            client.SearchAsync("logs", "by_message", "$.message", "shared",
                new SndbFullTextSearchOptions { Budget = new FullTextSearchBudgetOptions { MaxResponseBytes = 2_048 } }));
        Assert.Contains("fulltext_search_budget_exceeded", error.Message, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task QueryAdmissionFailureDoesNotDispatchHttp()
    {
        using var handler = new ResponseHandler(() => throw new InvalidOperationException("Unexpected dispatch."));
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() => client.SearchAsync(
            "logs", "by_message", "$.message", new string('x', 4_097)));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ErrorBodyWaitUsesTheSameDeadlineAndCleansTheResponse()
    {
        var stream = new FragmentedStream([], waitForCancellation: true);
        using var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StreamContent(stream),
        });
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() => client.SearchAsync(
            "logs", "by_message", "$.message", "shared",
            new SndbFullTextSearchOptions { Budget = new FullTextSearchBudgetOptions { TimeoutMilliseconds = 1_000 } }));
        Assert.True(stream.Disposed);
    }

    private static SndbFullTextClient CreateClient(HttpMessageHandler handler)
        => new("Data Source=http://127.0.0.1:1;Database=test", new HttpClient(handler));

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response());
        }
    }

    private sealed class FragmentedStream(byte[] content, bool waitForCancellation = false) : Stream
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (waitForCancellation)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                await completion.Task;
            }
            if (BytesRead == content.Length)
                return 0;
            buffer.Span[0] = content[BytesRead++];
            return 1;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
