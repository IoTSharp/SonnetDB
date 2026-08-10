using SonnetDB.Graphs;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphStoreMarkerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-graph-marker-" + Guid.NewGuid().ToString("N"));

    public GraphStoreMarkerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Open_OversizedMarkerRejectsBeforeWholeFileAllocation()
    {
        string markerPath;
        using (var manager = new GraphManager(_root, Options()))
            markerPath = manager.Create("oversized").MarkerPath;

        using (var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Write, FileShare.None))
            marker.SetLength(32L * 1024 * 1024);

        using var reopened = new GraphManager(_root, Options());
        long before = GC.GetAllocatedBytesForCurrentThread();

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => reopened.Open("oversized"));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("长度", error.Message, StringComparison.Ordinal);
        Assert.True(allocated < 1024 * 1024, $"Oversized marker validation allocated {allocated:N0} bytes.");
    }

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
        catch (UnauthorizedAccessException)
        {
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
}
