using System.Text;
using SonnetDB.Data.Mq;
using Xunit;

namespace SonnetDB.Core.Tests.Mq;

public sealed class SndbMqClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-mq-client-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PullAsync_WithTwoEmbeddedClientsOnSameDirectory_SeesMessagesWithoutReopen()
    {
        string connectionString = $"Data Source={_root};Mode=Embedded";
        using var producer = new SndbMqClient(connectionString);
        using var consumer = new SndbMqClient(connectionString);

        long offset = await producer.PublishAsync(
            "events.shared",
            Encoding.UTF8.GetBytes("ready"),
            new Dictionary<string, string> { ["source"] = "test" });

        var messages = await consumer.PullAsync("events.shared", "workers", 10);
        long nextOffset = await consumer.AckAsync("events.shared", "workers", offset);
        var stats = await producer.GetStatsAsync("events.shared");

        Assert.Equal(0, offset);
        Assert.Single(messages);
        Assert.Equal("ready", Encoding.UTF8.GetString(messages[0].Payload));
        Assert.Equal("test", messages[0].Headers["source"]);
        Assert.Equal(1, nextOffset);
        Assert.Equal(1, stats.ConsumerOffsets["workers"]);
    }

    [Fact]
    public async Task PublishManyAsync_EmbeddedBatch_ReturnsContiguousOffsetsInOrder()
    {
        string connectionString = $"Data Source={_root};Mode=Embedded";
        using var client = new SndbMqClient(connectionString);

        var offsets = await client.PublishManyAsync(
            "events.batch",
            [
                new SndbMqPublishEntry(Encoding.UTF8.GetBytes("a")),
                new SndbMqPublishEntry(Encoding.UTF8.GetBytes("b"), new Dictionary<string, string> { ["k"] = "v" }),
                new SndbMqPublishEntry(Encoding.UTF8.GetBytes("c")),
            ]);

        var messages = await client.PullAsync("events.batch", "workers", 10);

        Assert.Equal([0L, 1L, 2L], offsets);
        Assert.Equal(["a", "b", "c"], messages.Select(m => Encoding.UTF8.GetString(m.Payload)).ToArray());
        Assert.Equal("v", messages[1].Headers["k"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
