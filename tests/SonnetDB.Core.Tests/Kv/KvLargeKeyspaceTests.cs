using System.Text;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvLargeKeyspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-kv-large-tests", Guid.NewGuid().ToString("N"));

    public KvLargeKeyspaceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RangeCursor_ReadNextPageAsync_StreamsBoundedPagesAndHonorsCancellation()
    {
        using var keyspace = KvKeyspace.Open("cursor", _root, new KvOptions { SyncWalOnEveryWrite = false });
        for (int index = 0; index < 5; index++)
            keyspace.Put($"item:{index:D2}", Encoding.UTF8.GetBytes(index.ToString()));

        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = "item:"u8.ToArray(),
            PageSize = 2,
        });

        var values = new List<string>();
        await foreach (KvEntry entry in cursor.ReadAllAsync())
            values.Add(Encoding.UTF8.GetString(entry.Key.Span));

        Assert.Equal(new[] { "item:00", "item:01", "item:02", "item:03", "item:04" }, values);

        using KvRangeCursor canceledCursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = "item:"u8.ToArray(),
            PageSize = 2,
        });
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await canceledCursor.ReadNextPageAsync(canceled.Token));
    }

    [Fact]
    public void Diagnostics_ReportsExpiryCapacityAndHotKeyReads()
    {
        using var keyspace = KvKeyspace.Open("diagnostics", _root, new KvOptions { SyncWalOnEveryWrite = false });
        keyspace.Put("hot", "value"u8);
        keyspace.Put("expires", "value"u8, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.NotNull(keyspace.Get("hot"));
        Assert.NotNull(keyspace.Get("hot"));

        KvDiagnostics diagnostics = keyspace.GetDiagnostics();

        Assert.Equal(2, diagnostics.ActiveKeys);
        Assert.Equal(1, diagnostics.ExpiringKeys);
        KvHotKey hot = Assert.Single(diagnostics.HotKeys);
        Assert.Equal("hot", hot.Key);
        Assert.Equal(2, hot.Reads);
        Assert.True(diagnostics.WalBytes > 0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
