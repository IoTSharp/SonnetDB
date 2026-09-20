using System.Text;
using SonnetDB.Data.Mq;
using Xunit;

namespace SonnetDB.Core.Tests.Mq;

/// <summary>
/// 高层 MQ builder、预取、确认和 drain 回归。
/// </summary>
public sealed class SndbMqHighLevelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-mq-high-level-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Consumer_ManualAck_UsesBoundedPrefetchAndResumesAfterAck()
    {
        using var client = new SndbMqClient($"Data Source={_root};Mode=Embedded");
        await client.PublishManyAsync("events.high", [
            new SndbMqPublishEntry(Encoding.UTF8.GetBytes("a")),
            new SndbMqPublishEntry(Encoding.UTF8.GetBytes("b")),
            new SndbMqPublishEntry(Encoding.UTF8.GetBytes("c")),
        ]);

        await using var consumer = client.Consumer("events.high", "workers")
            .Prefetch(2)
            .ManualAck()
            .PollInterval(TimeSpan.Zero)
            .Build();
        await using var enumerator = consumer.PullAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        SndbMqDelivery first = enumerator.Current;
        Assert.Equal("a", Encoding.UTF8.GetString(first.Payload));
        Assert.True(await enumerator.MoveNextAsync());
        SndbMqDelivery second = enumerator.Current;
        Assert.Equal("b", Encoding.UTF8.GetString(second.Payload));

        await first.AckAsync();
        await second.AckAsync();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("c", Encoding.UTF8.GetString(enumerator.Current.Payload));
        await enumerator.Current.AckAsync();
    }

    [Fact]
    public async Task Consumer_AutoAck_AdvancesGroupBeforeYield()
    {
        using var client = new SndbMqClient($"Data Source={_root};Mode=Embedded");
        await client.PublishAsync("events.auto", [1]);

        await using var consumer = client.Consumer("events.auto", "workers")
            .AutoAck()
            .PollInterval(TimeSpan.Zero)
            .Build();
        await using var enumerator = consumer.PushAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(enumerator.Current.IsAcknowledged);
        Assert.False((await client.GetStatsAsync("events.auto")).ConsumerOffsets.TryGetValue("workers", out var next) && next == 0);
        await consumer.DrainAsync();
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task Producer_Drain_RejectsNewPublishAndWaitsForCurrentOperations()
    {
        using var client = new SndbMqClient($"Data Source={_root};Mode=Embedded");
        await using var producer = client.Producer("events.producer").MaxInFlight(1).Build();

        Assert.Equal(0, await producer.PublishAsync(new byte[] { 1 }));
        await producer.DrainAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => producer.PublishAsync(new byte[] { 2 }));
    }

    [Fact]
    public async Task Consumer_PreCanceled_DoesNotPull()
    {
        using var client = new SndbMqClient($"Data Source={_root};Mode=Embedded");
        await client.PublishAsync("events.cancel", [1]);

        await using var consumer = client.Consumer("events.cancel", "workers").Build();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await using var enumerator = consumer.PullAsync(canceled.Token).GetAsyncEnumerator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task Consumer_Dispose_CancelsPendingEnumeration()
    {
        using var client = new SndbMqClient($"Data Source={_root};Mode=Embedded");
        await using var consumer = client.Consumer("events.dispose", "workers")
            .PollInterval(TimeSpan.FromSeconds(5))
            .Build();
        await using var enumerator = consumer.PullAsync().GetAsyncEnumerator();

        ValueTask<bool> pending = enumerator.MoveNextAsync();
        await Task.Delay(20);
        consumer.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
