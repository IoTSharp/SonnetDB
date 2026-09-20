using System.Text;
using SonnetMQ;

namespace SonnetDB.Core.Tests.Mq;

public sealed class SonnetMqNackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetmq-nack-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Nack_BeforeLimit_RedeliversAndExposesAttemptHeader()
    {
        using var store = Open(maxAttempts: 3);
        store.Publish("events", "payload"u8);

        SonnetMqMessage first = Assert.Single(store.Pull("events", "workers", 1));
        SonnetMqNackResult nack = store.Nack("events", "workers", first.Offset, "temporary");

        Assert.False(nack.DeadLettered);
        Assert.Equal(1, nack.DeliveryAttempt);
        SonnetMqMessage redelivery = Assert.Single(store.Pull("events", "workers", 1));
        Assert.Equal(first.Offset, redelivery.Offset);
        Assert.Equal("1", redelivery.Headers["x-delivery-attempt"]);
    }

    [Fact]
    public void Nack_AtLimit_PublishesDeadLetterAndAdvancesGroup()
    {
        using var store = Open(maxAttempts: 2);
        store.Publish("events", "payload"u8, new SonnetMqPublishOptions(new Dictionary<string, string> { ["kind"] = "test" }));

        var first = Assert.Single(store.Pull("events", "workers", 1));
        store.Nack("events", "workers", first.Offset, "temporary");
        var retry = Assert.Single(store.Pull("events", "workers", 1));
        SonnetMqNackResult dead = store.Nack("events", "workers", retry.Offset, "permanent");

        Assert.True(dead.DeadLettered);
        Assert.Equal(1, dead.NextOffset);
        var dlq = Assert.Single(store.Pull("events.dlq", 0, 1));
        Assert.Equal("payload", Encoding.UTF8.GetString(dlq.Payload));
        Assert.Equal("events", dlq.Headers["x-original-topic"]);
        Assert.Equal("permanent", dlq.Headers["x-dead-letter-reason"]);
        Assert.Empty(store.Pull("events", "workers", 1));
        Assert.Equal(1, store.GetDiagnostics("events").DeadLetterCount);
    }

    [Fact]
    public void Nack_Record_ReplaysAcrossReopen()
    {
        var options = Options(maxAttempts: 2);
        using (var store = SonnetMqStore.Open(options))
        {
            store.Publish("events", "payload"u8);
            var message = Assert.Single(store.Pull("events", "workers", 1));
            store.Nack("events", "workers", message.Offset, "retry");
        }

        using var reopened = SonnetMqStore.Open(options);
        var redelivery = Assert.Single(reopened.Pull("events", "workers", 1));
        Assert.Equal("1", redelivery.Headers["x-delivery-attempt"]);
        SonnetMqNackResult dead = reopened.Nack("events", "workers", redelivery.Offset, "final");
        Assert.True(dead.DeadLettered);
    }

    [Fact]
    public void ResetConsumerOffset_SupportsEarliestLatestExplicitAndTime()
    {
        var options = Options(maxAttempts: 2);
        long second;
        using (var store = SonnetMqStore.Open(options))
        {
            long first = store.Publish("reset", "first"u8);
            second = store.Publish("reset", "second"u8);
            Assert.Equal(2, store.ResetConsumerOffset("reset", "workers", SonnetMqOffsetResetMode.Latest));
            Assert.Empty(store.Pull("reset", "workers", 1));
            Assert.Equal(0, store.ResetConsumerOffset("reset", "workers", SonnetMqOffsetResetMode.Earliest));
            Assert.Equal(first, store.Pull("reset", "workers", 1)[0].Offset);
            Assert.Equal(second, store.ResetConsumerOffset("reset", "workers", SonnetMqOffsetResetMode.Explicit, second));
            Assert.Equal(second, store.Pull("reset", "workers", 1)[0].Offset);
            long threshold = store.Pull("reset", 0, 2)[1].TimestampUtc.UtcTicks;
            Assert.Equal(second, store.ResetConsumerOffset("reset", "workers", SonnetMqOffsetResetMode.Time, threshold));
            Assert.Equal(second, store.Pull("reset", "workers", 1)[0].Offset);
        }

        using var reopened = SonnetMqStore.Open(options);
        Assert.Equal(second, reopened.Pull("reset", "workers", 1)[0].Offset);
    }

    private SonnetMqStore Open(int maxAttempts)
        => SonnetMqStore.Open(Options(maxAttempts));

    private SonnetMqOptions Options(int maxAttempts)
        => new()
        {
            Path = _root,
            MaxDeliveryAttempts = maxAttempts,
            RetentionInterval = TimeSpan.Zero,
        };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
