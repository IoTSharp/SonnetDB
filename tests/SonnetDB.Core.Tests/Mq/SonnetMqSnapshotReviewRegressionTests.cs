using SonnetDB.Cli;
using SonnetMQ;
using Xunit.Abstractions;

namespace SonnetDB.Core.Tests.Mq;

public sealed class SonnetMqSnapshotReviewRegressionTests : IDisposable
{
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-mq-review-delta-" + Guid.NewGuid().ToString("N"));

    public SonnetMqSnapshotReviewRegressionTests(ITestOutputHelper output)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "delta-owner"), _owner);
        output.WriteLine("OWNER root=" + _root + ";owner=" + _owner);
    }

    [Fact]
    public void Snapshot_CaseVariedLifecycleFile_UsesOperatingSystemPathComparison()
    {
        string server = Path.Combine(_root, "server");
        string source = Path.Combine(server, ".system", "mq");
        string nativeSnapshot = Path.Combine(_root, "native-snapshot");
        string cliSnapshot = Path.Combine(_root, "cli-snapshot");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "SONNETMQ.LOCK"), [23, 42]);
        using (var store = SonnetMqStore.Open(new SonnetMqOptions { Path = source, RetentionInterval = TimeSpan.Zero }))
        {
            store.Publish("events", [1]);
            store.CreateSnapshot(nativeSnapshot);
        }
        AssertLifecycleFileContract(nativeSnapshot);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new CliApplication(new StringReader(string.Empty), stdout, stderr);
        Assert.Equal(0, cli.Run(["mq", "snapshot", "--server-root", server, "--output", cliSnapshot, "--offline"]));
        AssertLifecycleFileContract(cliSnapshot);
    }

    [Theory]
    [InlineData(SonnetMqOpenMode.Directory)]
    [InlineData(SonnetMqOpenMode.SingleFile)]
    public void Dispose_FirstTopicCloseFails_ClosesRemainingResourcesAndRetainsOriginalException(SonnetMqOpenMode mode)
    {
        var options = new SonnetMqOptions
        {
            Path = Path.Combine(_root, mode == SonnetMqOpenMode.Directory ? "queue" : "queue.smq"),
            OpenMode = mode,
            RetentionInterval = TimeSpan.Zero,
            HotTailMaxBytes = 1,
            TrimAcknowledgedMessages = false,
        };
        var store = SonnetMqStore.Open(options);
        store.Publish("alpha", [1, 2, 3]);
        store.Publish("alpha", [4, 5, 6]);
        store.Publish("beta", [7, 8, 9]);
        Assert.Single(store.Pull("alpha", 0, 1));
        if (mode == SonnetMqOpenMode.Directory)
            Assert.True(store.ColdPayloadBytesRead > 0);
        var failure = new IOException("injected first-topic close failure");
        var closed = new List<string>();
        store.AfterTopicDisposeTestHook = topic =>
        {
            closed.Add(topic);
            if (closed.Count != 1) return;
            if (mode == SonnetMqOpenMode.Directory)
                Assert.Throws<IOException>(() => SonnetMqStore.Open(options));
            throw failure;
        };
        try
        {
            Assert.Same(failure, Record.Exception(store.Dispose));
            Assert.Equal(new[] { "alpha", "beta" }, closed.Order(StringComparer.Ordinal).ToArray());
            string[] files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Take(16).ToArray();
            Assert.InRange(files.Length, 1, 15);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (string path in files)
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            GC.KeepAlive(store);
            using var reopened = SonnetMqStore.Open(options);
            Assert.Equal(2, reopened.Pull("alpha", 0, 10).Count);
            Assert.Single(reopened.Pull("beta", 0, 10));
            Assert.Equal(2, reopened.Publish("alpha", [10]));
        }
        finally
        {
            store.AfterTopicDisposeTestHook = null;
            store.Dispose();
        }
    }

    private static void AssertLifecycleFileContract(string snapshot)
    {
        var manifest = SonnetMqStore.ReadSnapshotManifest(snapshot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert.DoesNotContain(manifest.Files, file => string.Equals(file.Path, "sonnetmq.lock", comparison));
        if (!OperatingSystem.IsWindows())
            Assert.Contains(manifest.Files, file => file.Path == "SONNETMQ.LOCK");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
