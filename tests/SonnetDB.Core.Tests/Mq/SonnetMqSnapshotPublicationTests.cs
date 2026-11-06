using SonnetMQ;

namespace SonnetDB.Core.Tests.Mq;

public sealed class SonnetMqSnapshotPublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-mq-snapshot-publication-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotAndRestore_TargetAppearsBeforePublish_PreservesForeignBytes(bool restore)
    {
        string source = Path.Combine(_root, "source");
        string snapshot = Path.Combine(_root, "snapshot");
        string target = restore ? Path.Combine(_root, "restore-target") : snapshot;
        using var store = SonnetMqStore.Open(new SonnetMqOptions { Path = source, RetentionInterval = TimeSpan.Zero });
        store.Publish("events", [1]);
        if (restore) store.CreateSnapshot(snapshot);
        byte[] bytes = [0, 17, 255, 128];
        string foreign = Path.Combine(target, "foreign.bin");
        SonnetMqStore.SnapshotBeforePublishTestHook = (_, directory) =>
        {
            if (directory != target) return;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(foreign, bytes);
        };
        try
        {
            Assert.Throws<IOException>(() =>
            {
                if (restore) SonnetMqStore.RestoreSnapshot(snapshot, target);
                else store.CreateSnapshot(snapshot);
            });
        }
        finally { SonnetMqStore.SnapshotBeforePublishTestHook = null; }
        Assert.Equal(bytes, File.ReadAllBytes(foreign));
        Assert.Empty(Directory.EnumerateDirectories(_root, restore ? "restore-target.restore-*" : "snapshot.snapshot-*"));
        Assert.Single(store.Pull("events", 0, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotAndRestore_InterruptedBeforePublish_LeavesNoPublishedTarget(bool restore)
    {
        string source = Path.Combine(_root, "source");
        string snapshot = Path.Combine(_root, "snapshot");
        string target = restore ? Path.Combine(_root, "restore-target") : snapshot;
        using var store = SonnetMqStore.Open(new SonnetMqOptions { Path = source, RetentionInterval = TimeSpan.Zero });
        store.Publish("events", [1]);
        if (restore) store.CreateSnapshot(snapshot);
        SonnetMqStore.SnapshotBeforePublishTestHook = (_, directory) =>
        {
            if (directory == target) throw new IOException("injected interrupted snapshot publication");
        };
        try
        {
            Assert.Throws<IOException>(() =>
            {
                if (restore) SonnetMqStore.RestoreSnapshot(snapshot, target);
                else store.CreateSnapshot(snapshot);
            });
        }
        finally { SonnetMqStore.SnapshotBeforePublishTestHook = null; }
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.EnumerateDirectories(_root, restore ? "restore-target.restore-*" : "snapshot.snapshot-*"));
    }

    [Fact]
    public void DirectoryOwner_SecondOpenRejectsAndDisposeAllowsReopen()
    {
        var options = new SonnetMqOptions { Path = _root, RetentionInterval = TimeSpan.Zero };
        using (var owner = SonnetMqStore.Open(options))
        {
            Assert.Throws<IOException>(() => SonnetMqStore.Open(options));
            owner.Publish("events", [1]);
        }
        using var reopened = SonnetMqStore.Open(options);
        Assert.Single(reopened.Pull("events", 0, 1));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
