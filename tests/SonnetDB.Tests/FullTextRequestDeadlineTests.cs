using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SonnetDB.Contracts;
using SonnetDB.Endpoints;
using SonnetDB.Exceptions;
using SonnetDB.FullText;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

public sealed class FullTextRequestDeadlineTests
{
    [Fact]
    public async Task ABodyReleasedAfterTheRequestedDeadlineCannotStartAFreshQueryBudget()
    {
        var request = new FullTextSearchRequest("logs", "by_message", "$.message", "shared")
        {
            Budget = new FullTextSearchBudgetOptions { TimeoutMilliseconds = 1 },
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, ServerJsonContext.Default.FullTextSearchRequest);
        using var body = new GatedBody(payload);
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        context.Request.ContentLength = payload.Length;
        long started = Stopwatch.GetTimestamp();
        Task<FullTextSearchRequest?> reading = SonnetDbEndpoints.ReadFullTextRequestBoundedAsync(context, started);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 用期限事件释放读取，不按执行速度猜测 body 已经到达。
        using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using CancellationTokenRegistration release = releaseDeadline.Token.Register(() => body.Release.TrySetResult());
        FullTextSearchRequest parsed = Assert.IsType<FullTextSearchRequest>(await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(releaseDeadline.IsCancellationRequested);
        Assert.Throws<FullTextSearchBudgetExceededException>(() =>
        {
            using FullTextSearchBudget budget = SonnetDbEndpoints.CreateFullTextRemainingBudget(parsed, started, default);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ARequestedNonPositiveDeadlineRemainsAnInvalidOption(int timeout)
    {
        var request = new FullTextSearchRequest("logs", "by_message", "$.message", "shared")
        {
            Budget = new FullTextSearchBudgetOptions { TimeoutMilliseconds = timeout },
        };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using FullTextSearchBudget budget = SonnetDbEndpoints.CreateFullTextRemainingBudget(request, Stopwatch.GetTimestamp(), default);
        });
    }

    [Fact]
    public void RemainingBudgetIsCappedByTheOriginalHardDeadline()
    {
        var request = new FullTextSearchRequest("logs", "by_message", "$.message", "shared")
        {
            Budget = new FullTextSearchBudgetOptions { TimeoutMilliseconds = int.MaxValue },
        };
        using FullTextSearchBudget budget = SonnetDbEndpoints.CreateFullTextRemainingBudget(request, Stopwatch.GetTimestamp(), default);
        Assert.InRange(budget.TimeoutMilliseconds, 1, FullTextSearchBudgetOptions.DefaultTimeoutMilliseconds);
    }

    private sealed class GatedBody(byte[] payload) : MemoryStream(payload, writable: false)
    {
        private bool _waited;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_waited)
            {
                _waited = true;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
