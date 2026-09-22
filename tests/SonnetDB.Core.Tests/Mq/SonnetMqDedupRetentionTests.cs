using SonnetMQ;

namespace SonnetDB.Core.Tests.Mq;

public sealed class SonnetMqDedupRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetmq-dedup-retention-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(SonnetMqOpenMode.Directory)]
    [InlineData(SonnetMqOpenMode.SingleFile)]
    public void TombstoneBefore_ClearsExpiredMessageIds_AndRetainsCutoffIds(SonnetMqOpenMode mode)
    {
        var options = Options(mode);
        using var store = SonnetMqStore.Open(options);
        var expiredHeaders = Headers("expired");
        var retainedHeaders = Headers("retained");

        Assert.Equal(0, store.Publish("events", "expired"u8, new SonnetMqPublishOptions(expiredHeaders)));
        Assert.Equal(1, store.Publish("events", "retained"u8, new SonnetMqPublishOptions(retainedHeaders)));
        Assert.Equal(1, store.TombstoneBefore("events", 1));

        // The expired ID must be a new publish, while the retained ID still deduplicates.
        Assert.Equal(2, store.Publish("events", "replacement"u8, new SonnetMqPublishOptions(expiredHeaders)));
        Assert.Equal(1, store.Publish("events", "duplicate"u8, new SonnetMqPublishOptions(retainedHeaders)));
        Assert.Equal(3, store.GetStats("events").NextOffset);
        Assert.Equal([1L, 2L], store.Pull("events", 1, 10).Select(message => message.Offset).ToArray());
    }

    [Theory]
    [InlineData(SonnetMqOpenMode.Directory)]
    [InlineData(SonnetMqOpenMode.SingleFile)]
    public void TombstoneBefore_ClearsExpiredMessageIds_AcrossReopen(SonnetMqOpenMode mode)
    {
        var options = Options(mode);
        var expiredHeaders = Headers("expired");
        var retainedHeaders = Headers("retained");

        using (var store = SonnetMqStore.Open(options))
        {
            store.Publish("events", "expired"u8, new SonnetMqPublishOptions(expiredHeaders));
            store.Publish("events", "retained"u8, new SonnetMqPublishOptions(retainedHeaders));
            Assert.Equal(1, store.TombstoneBefore("events", 1));
        }

        using var reopened = SonnetMqStore.Open(options);
        Assert.Equal(2, reopened.Publish("events", "replacement"u8, new SonnetMqPublishOptions(expiredHeaders)));
        Assert.Equal(1, reopened.Publish("events", "duplicate"u8, new SonnetMqPublishOptions(retainedHeaders)));
        Assert.Equal(3, reopened.GetStats("events").NextOffset);
        Assert.Equal([1L, 2L], reopened.Pull("events", 1, 10).Select(message => message.Offset).ToArray());
    }

    private SonnetMqOptions Options(SonnetMqOpenMode mode)
        => new()
        {
            Path = mode == SonnetMqOpenMode.SingleFile
                ? Path.Combine(_root, "queue.smq")
                : _root,
            OpenMode = mode,
            RetentionInterval = TimeSpan.Zero,
            TrimAcknowledgedMessages = false,
            MessageIdDeduplicationWindow = 8,
        };

    private static IReadOnlyDictionary<string, string> Headers(string messageId)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["message-id"] = messageId,
        };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
