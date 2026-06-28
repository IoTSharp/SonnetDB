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

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
