using System.Text.Json;
using SonnetDB.Cli;
using SonnetMQ;

namespace SonnetDB.Core.Tests.Cli;

public sealed class MqSnapshotCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-cli-instance-mq-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SnapshotRestore_OfflineInstance_PreservesAllTopicOwnersAndRecoveryStateAcrossTwoReopens()
    {
        string source = Path.Combine(_root, "source");
        string snapshot = Path.Combine(_root, "snapshot");
        string target = Path.Combine(_root, "target");
        string mqPath = Path.Combine(source, ".system", "mq");
        var options = Options(mqPath);
        using (var store = SonnetMqStore.Open(options))
        {
            store.Publish("db_a.events", [1], new SonnetMqPublishOptions(new Dictionary<string, string> { ["message-id"] = "first" }));
            store.Publish("db_a.events", [2]);
            store.Ack("db_a.events", "ack", 0);
            store.Nack("db_a.events", "retry", 0, "retry-1");
            store.Nack("db_a.events", "retry", 0, "retry-2");
            store.Publish("db_b.events", [3]);
            store.Publish("legacy_unscoped", [4]);
            store.Publish("db_a.dead", [5]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            for (int attempt = 0; attempt < 5; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                store.Nack("db_a.dead", "failed", 0, "permanent");
            }
        }
        File.WriteAllText(Path.Combine(source, ".system", "users.json"), "identity-config-is-outside-mq-backup");

        var copied = Run("mq", "snapshot", "--server-root", source, "--output", snapshot, "--offline");
        Assert.Equal(0, copied.ExitCode);
        using (var receipt = JsonDocument.Parse(copied.Output))
        {
            Assert.Equal("instance-mq-only", receipt.RootElement.GetProperty("storageScope").GetString());
            Assert.Equal(mqPath, receipt.RootElement.GetProperty("mqDirectory").GetString());
            Assert.Equal(64, receipt.RootElement.GetProperty("manifestSha256").GetString()!.Length);
        }
        var manifest = SonnetMqStore.ReadSnapshotManifest(snapshot);
        Assert.DoesNotContain(manifest.Files, file => file.Path.Contains("users.json", StringComparison.Ordinal)
            || file.Path.EndsWith("sonnetmq.lock", StringComparison.Ordinal));
        Assert.Equal(0, Run("mq", "inspect", "--snapshot", snapshot).ExitCode);
        Directory.Move(source, source + "-offline");
        var restored = Run("mq", "restore", "--snapshot", snapshot, "--server-root", target, "--offline");
        Assert.Equal(0, restored.ExitCode);
        options = Options(Path.Combine(target, ".system", "mq"));
        Assert.False(File.Exists(Path.Combine(target, ".system", "users.json")));

        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (int reopen = 0; reopen < 2; reopen++)
        {
            overall.Token.ThrowIfCancellationRequested();
            using var store = SonnetMqStore.Open(options);
            Assert.Equal(new[] { "db_a.dead", "db_a.dead.dlq", "db_a.events", "db_b.events", "legacy_unscoped" },
                store.ListTopicStats().Select(item => item.Topic).Order(StringComparer.Ordinal).ToArray());
            var acknowledged = Assert.Single(store.Pull("db_a.events", "ack", 1));
            Assert.Equal(reopen == 0 ? 1 : 2, acknowledged.Offset);
            Assert.Equal(reopen == 0 ? new byte[] { 2 } : new byte[] { 6 }, acknowledged.Payload);
            var retry = Assert.Single(store.Pull("db_a.events", "retry", 1));
            Assert.Equal("first", retry.Headers["message-id"]);
            Assert.Equal(reopen == 0 ? "2" : "3", retry.Headers["x-delivery-attempt"]);
            Assert.Equal(new byte[] { 3 }, Assert.Single(store.Pull("db_b.events", 0, 1)).Payload);
            Assert.Equal(new byte[] { 4 }, Assert.Single(store.Pull("legacy_unscoped", 0, 1)).Payload);
            var dead = Assert.Single(store.Pull("db_a.dead.dlq", 0, 1));
            Assert.Equal("db_a.dead", dead.Headers["x-original-topic"]);
            Assert.Equal("permanent", dead.Headers["x-dead-letter-reason"]);
            Assert.Empty(store.Pull("db_a.dead", "failed", 1));
            if (reopen == 0)
            {
                Assert.Equal(2, store.Publish("db_a.events", [6]));
                store.Ack("db_a.events", "ack", 1);
                Assert.Equal(3, store.Nack("db_a.events", "retry", 0, "continue").DeliveryAttempt);
            }
        }
        Assert.Equal(0, Run("mq", "inspect", "--snapshot", snapshot).ExitCode);
    }

    [Fact]
    public void Snapshot_WhileServerMqOwnerIsOpen_RejectsWithoutCreatingOutput()
    {
        string source = Path.Combine(_root, "source");
        string output = Path.Combine(_root, "snapshot");
        using var owner = SonnetMqStore.Open(Options(Path.Combine(source, ".system", "mq")));
        var result = Run("mq", "snapshot", "--server-root", source, "--output", output, "--offline");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("owner", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
        Assert.Equal(0, owner.Publish("still-live", [1]));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("truncated")]
    [InlineData("path")]
    [InlineData("missing")]
    public void InspectAndRestore_InvalidSnapshot_RejectWithoutCreatingTarget(string corruption)
    {
        string source = Path.Combine(_root, "source");
        string snapshot = Path.Combine(_root, "snapshot");
        string target = Path.Combine(_root, "target");
        using (var store = SonnetMqStore.Open(Options(source)))
        {
            store.Publish("events", [1]);
            store.CreateSnapshot(snapshot);
        }
        string manifestPath = Path.Combine(snapshot, SonnetMqInstanceSnapshotManifest.FileName);
        if (corruption is "hash" or "missing")
        {
            var manifest = SonnetMqStore.ReadSnapshotManifest(snapshot);
            string file = Path.Combine(snapshot, manifest.Files[0].Path.Replace('/', Path.DirectorySeparatorChar));
            if (corruption == "hash") File.AppendAllText(file, "damage");
            else File.Delete(file);
        }
        else
        {
            File.WriteAllText(manifestPath, corruption switch
            {
                "version" => "{\"formatVersion\":999,\"files\":[]}",
                "path" => "{\"formatVersion\":1,\"files\":[{\"path\":\"../escape\",\"length\":0,\"sha256\":\"bad\"}]}",
                _ => "{\"formatVersion\":1",
            });
        }
        Assert.Equal(1, Run("mq", "inspect", "--snapshot", snapshot).ExitCode);
        Assert.Equal(1, Run("mq", "restore", "--snapshot", snapshot, "--server-root", target, "--offline").ExitCode);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void SnapshotAndRestore_ExistingOrNestedTargets_RejectWithoutChangingForeignFiles()
    {
        string server = Path.Combine(_root, "server");
        string snapshot = Path.Combine(_root, "snapshot");
        using (var store = SonnetMqStore.Open(Options(Path.Combine(server, ".system", "mq"))))
            store.Publish("events", [1]);
        Directory.CreateDirectory(snapshot);
        string foreign = Path.Combine(snapshot, "foreign.bin");
        File.WriteAllBytes(foreign, [0, 255, 17]);
        Assert.Equal(1, Run("mq", "snapshot", "--server-root", server, "--output", snapshot, "--offline").ExitCode);
        Assert.Equal(new byte[] { 0, 255, 17 }, File.ReadAllBytes(foreign));
        Assert.Equal(2, Run("mq", "snapshot", "--server-root", server, "--output", Path.Combine(server, "nested"), "--offline").ExitCode);
        Assert.False(Directory.Exists(Path.Combine(server, "nested")));
        Assert.Equal(2, Run("mq", "snapshot", "--server-root", server, "--output", Path.Combine(_root, "new")).ExitCode);
        Assert.Equal(2, Run("mq", "inspect", "--snapshot", "relative").ExitCode);
    }

    private static SonnetMqOptions Options(string path)
        => new() { Path = path, RetentionInterval = TimeSpan.Zero, TrimAcknowledgedMessages = false };

    private static (int ExitCode, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new CliApplication(new StringReader(string.Empty), output, error);
        return (app.Run(args), output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
