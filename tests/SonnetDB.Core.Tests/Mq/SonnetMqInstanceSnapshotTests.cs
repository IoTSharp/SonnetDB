using System.Text;
using SonnetMQ;

namespace SonnetDB.Core.Tests.Mq;

public sealed class SonnetMqInstanceSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetmq-snapshot-" + Guid.NewGuid().ToString("N"));
    private readonly string _snapshot = Path.Combine(Path.GetTempPath(), "sonnetmq-snapshot-copy-" + Guid.NewGuid().ToString("N"));
    private readonly string _restored = Path.Combine(Path.GetTempPath(), "sonnetmq-snapshot-restored-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateAndRestoreSnapshot_PreservesMessagesHeadersAndConsumerOffsets()
    {
        var options = new SonnetMqOptions
        {
            Path = _root,
            RetentionInterval = TimeSpan.Zero,
            TrimAcknowledgedMessages = false,
            MessageIdDeduplicationWindow = 16,
        };
        using (var store = SonnetMqStore.Open(options))
        {
            store.Publish("events", "first"u8, new SonnetMqPublishOptions(
                new Dictionary<string, string>
                {
                    ["message-id"] = "evt-1",
                    ["kind"] = "snapshot",
                }));
            store.Publish("events", "second"u8);
            store.Ack("events", "workers", 0);

            SonnetMqInstanceSnapshotResult result = store.CreateSnapshot(_snapshot);
            Assert.True(result.FileCount > 0);
            Assert.True(File.Exists(result.ManifestPath));
        }

        SonnetMqInstanceSnapshotManifest manifest = SonnetMqStore.RestoreSnapshot(_snapshot, _restored);
        Assert.Equal(SonnetMqInstanceSnapshotManifest.CurrentFormatVersion, manifest.FormatVersion);

        using var restored = SonnetMqStore.Open(options with { Path = _restored });
        var message = Assert.Single(restored.Pull("events", "workers", 10));
        Assert.Equal("second", Encoding.UTF8.GetString(message.Payload));
        Assert.Equal(1, message.Offset);
        Assert.Equal("snapshot", (Assert.Single(restored.Pull("events", 0, 1))).Headers["kind"]);
        Assert.Equal(2, restored.Publish("events", "third"u8));
    }

    [Fact]
    public void CreateAndRestoreSnapshot_SingleFileMode_PreservesLog()
    {
        string sourceFile = Path.Combine(_root, "queue.smq");
        var options = new SonnetMqOptions
        {
            Path = sourceFile,
            OpenMode = SonnetMqOpenMode.SingleFile,
            RetentionInterval = TimeSpan.Zero,
            TrimAcknowledgedMessages = false,
        };

        using (var store = SonnetMqStore.Open(options))
        {
            Assert.Equal(0, store.Publish("events", "single-file"u8));
            SonnetMqInstanceSnapshotResult result = store.CreateSnapshot(_snapshot);
            Assert.Equal(1, result.FileCount);
        }

        SonnetMqInstanceSnapshotManifest manifest = SonnetMqStore.RestoreSnapshot(_snapshot, _restored);
        string restoredFile = Path.Combine(_restored, Path.GetFileName(sourceFile));
        Assert.Contains(manifest.Files, file => file.Path == Path.GetFileName(sourceFile));

        using var restored = SonnetMqStore.Open(options with { Path = restoredFile });
        SonnetMqMessage message = Assert.Single(restored.Pull("events", 0, 1));
        Assert.Equal("single-file", Encoding.UTF8.GetString(message.Payload));
        Assert.Equal(1, restored.Publish("events", "after"u8));
    }

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_snapshot);
        TryDelete(_restored);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
