using System.Text;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvNamespaceCursorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-kv-namespace-cursor-tests",
        Guid.NewGuid().ToString("N"));

    public KvNamespaceCursorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void NamespaceCursor_QualifiesLocalBoundsAndProjectsKeys()
    {
        using var keyspace = KvKeyspace.Open("namespace-cursor", _root, Options());
        KvNamespace tenant = keyspace.Namespace("tenant");
        tenant.Put("device:01", [1]);
        tenant.Put("device:02", [2]);
        tenant.Put("device:03", [3]);
        tenant.Put("other", [9]);
        keyspace.Put("tenantless", [8]);

        using KvNamespaceReadSnapshot snapshot = tenant.AcquireReadSnapshot();
        using KvNamespaceRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = Bytes("device:"),
            StartInclusive = Bytes("device:01"),
            EndExclusive = Bytes("device:03"),
            PageSize = 1,
        });
        snapshot.Dispose();
        Assert.Throws<ObjectDisposedException>(() => snapshot.OpenRangeCursor());

        IReadOnlyList<KvEntry> first = cursor.ReadNextPage();
        IReadOnlyList<KvEntry> second = cursor.ReadNextPage();

        Assert.Equal("device:01", Text(Assert.Single(first).Key));
        Assert.Equal([1], first[0].Value.ToArray());
        Assert.Equal("device:02", Text(Assert.Single(second).Key));
        Assert.Empty(cursor.ReadNextPage());
        Assert.True(cursor.IsExhausted);
        Assert.Equal(snapshot.Sequence, cursor.SnapshotSequence);
    }

    [Fact]
    public async Task OpenRangeCursor_RemainsStableAfterNamespaceSnapshotIsReleased()
    {
        using var keyspace = KvKeyspace.Open("namespace-stable", _root, Options());
        KvNamespace tenant = keyspace.Namespace("tenant");
        tenant.Put("item:01", [1]);
        tenant.Put("item:02", [2]);

        using KvNamespaceRangeCursor cursor = tenant.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = Bytes("item:"),
            PageSize = 1,
        });
        tenant.Put("item:01", [9]);
        tenant.Put("item:03", [3]);

        var values = new List<string>();
        await foreach (KvEntry entry in cursor.ReadAllAsync())
            values.Add($"{Text(entry.Key)}={entry.Value.Span[0]}");

        Assert.Equal(["item:01=1", "item:02=2"], values);
    }

    [Fact]
    public void NamespaceCursor_CancellationTerminatesUnderlyingCursor()
    {
        using var keyspace = KvKeyspace.Open("namespace-cancel", _root, Options());
        KvNamespace tenant = keyspace.Namespace("tenant");
        tenant.Put("item:01", [1]);

        using KvNamespaceRangeCursor cursor = tenant.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = Bytes("item:"),
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => cursor.ReadNextPage(cancellation.Token));
        Assert.Throws<InvalidOperationException>(() => cursor.ReadNextPage());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Test cleanup must not hide the assertion result.
        }
    }

    private static KvOptions Options()
        => KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            SyncWalOnEveryWrite = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        };

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.UTF8.GetString(value.Span);
}
