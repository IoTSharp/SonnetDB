using System.Text;
using SonnetMQ;
using Xunit;

namespace SonnetMQ.Tests;

public sealed class SonnetMqStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetmq-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Publish_ThenPull_ReturnsMessagesInOffsetOrder()
    {
        using var store = Open();

        long first = store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("a"));
        long second = store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("b"));

        var messages = store.Pull("iot.telemetry", "iotsharp", 10);

        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(["a", "b"], messages.Select(m => Encoding.UTF8.GetString(m.Payload)).ToArray());
    }

    [Fact]
    public void Ack_WithOffset_SkipsAcknowledgedMessages()
    {
        using var store = Open();
        store.Publish("iot.events", Encoding.UTF8.GetBytes("a"));
        store.Publish("iot.events", Encoding.UTF8.GetBytes("b"));

        long next = store.Ack("iot.events", "rules", 0);
        var messages = store.Pull("iot.events", "rules", 10);

        Assert.Equal(1, next);
        Assert.Single(messages);
        Assert.Equal(1, messages[0].Offset);
    }

    [Fact]
    public void PublishMany_ReturnsContiguousOffsetsAndPreservesOrder()
    {
        using var store = Open();

        var offsets = store.PublishMany(
            "iot.telemetry",
            [
                new SonnetMqPublishEntry(Encoding.UTF8.GetBytes("a")),
                new SonnetMqPublishEntry(Encoding.UTF8.GetBytes("b")),
                new SonnetMqPublishEntry(Encoding.UTF8.GetBytes("c"))
            ]);

        var messages = store.Pull("iot.telemetry", "iotsharp", 10);

        Assert.Equal([0L, 1L, 2L], offsets);
        Assert.Equal(["a", "b", "c"], messages.Select(m => Encoding.UTF8.GetString(m.Payload)).ToArray());
    }

    [Fact]
    public void Pull_FromOffset_ReturnsMessagesAtOrAfterOffset()
    {
        using var store = Open(offsetIndexStride: 2);
        store.PublishMany(
            "iot.telemetry",
            Enumerable.Range(0, 8)
                .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes(i.ToString())))
                .ToArray());

        var messages = store.Pull("iot.telemetry", 5, 10);

        Assert.Equal([5L, 6L, 7L], messages.Select(m => m.Offset).ToArray());
        Assert.Equal(["5", "6", "7"], messages.Select(m => Encoding.UTF8.GetString(m.Payload)).ToArray());
    }

    [Fact]
    public void Pull_AfterAck_UsesSparseOffsetIndexForLargeOffsets()
    {
        using var store = Open(offsetIndexStride: 4);
        store.PublishMany(
            "iot.telemetry",
            Enumerable.Range(0, 16)
                .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes(i.ToString())))
                .ToArray());

        store.Ack("iot.telemetry", "iotsharp", 10);
        var messages = store.Pull("iot.telemetry", "iotsharp", 3);

        Assert.Equal([11L, 12L, 13L], messages.Select(m => m.Offset).ToArray());
    }

    [Fact]
    public void Open_WithExistingLog_ReplaysMessagesAndAcks()
    {
        using (var store = Open())
        {
            store.Publish("iot.commands", Encoding.UTF8.GetBytes("a"));
            store.Publish("iot.commands", Encoding.UTF8.GetBytes("b"));
            store.Ack("iot.commands", "device-agent", 0);
        }

        using var reopened = Open();
        var messages = reopened.Pull("iot.commands", "device-agent", 10);
        var stats = reopened.GetStats("iot.commands");

        Assert.Single(messages);
        Assert.Equal("b", Encoding.UTF8.GetString(messages[0].Payload));
        Assert.Equal(2, stats.MessageCount);
        Assert.Equal(2, stats.NextOffset);
        Assert.Equal(1, stats.ConsumerOffsets["device-agent"]);
    }

    [Fact]
    public void Open_WithSingleFileMode_PersistsInOneFile()
    {
        string file = Path.Combine(_root, "queue.smq");
        using (var store = SonnetMqStore.Open(new SonnetMqOptions { Path = file, OpenMode = SonnetMqOpenMode.SingleFile }))
        {
            store.Publish("iot.audit", Encoding.UTF8.GetBytes("created"));
        }

        Assert.True(File.Exists(file));
        using var reopened = SonnetMqStore.Open(new SonnetMqOptions { Path = file, OpenMode = SonnetMqOpenMode.SingleFile });
        Assert.Single(reopened.Pull("iot.audit", "audit-sink", 10));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private SonnetMqStore Open(int offsetIndexStride = 1024)
        => SonnetMqStore.Open(new SonnetMqOptions { Path = _root, OffsetIndexStride = offsetIndexStride });
}
