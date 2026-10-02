using SonnetDB.Samples;

namespace SonnetDB.Core.Tests.Audits;

/// <summary>实际文档变更、CDC 桥接及自动投递窗口的组合恢复回归。</summary>
public sealed class CdcStreamingBridgeJourneyTests
{
    [Fact]
    public async Task RunAsync_WithActualDocumentChanges_ReconcilesAfterRetryAndReopen()
    {
        string root = Directory.CreateTempSubdirectory("sonnetdb-bridge-journey-test-").FullName;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            CdcStreamingBridgeJourneyResult result = await CdcStreamingBridgeJourney.RunAsync(root, deadline.Token);
            Assert.Equal(new CdcStreamingBridgeJourneyResult(4, 3, 4, 2, 0), result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WithCancelledToken_PreservesEmptyDirectory()
    {
        string root = Directory.CreateTempSubdirectory("sonnetdb-bridge-journey-test-").FullName;
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CdcStreamingBridgeJourney.RunAsync(root, cancellation.Token));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
