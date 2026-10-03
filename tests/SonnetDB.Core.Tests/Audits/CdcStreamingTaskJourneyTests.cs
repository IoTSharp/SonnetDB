using SonnetDB.Samples;

namespace SonnetDB.Core.Tests.Audits;

/// <summary>实际多分区桥接与注册任务的本地重开回归。</summary>
public sealed class CdcStreamingTaskJourneyTests
{
    [Fact]
    public async Task RunAsync_WithActualSources_ReconcilesIndependentOffsetsAndDeduplicatedWindows()
    {
        string root = Directory.CreateTempSubdirectory("sonnetdb-task-journey-test-").FullName;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            CdcStreamingTaskJourneyResult result = await CdcStreamingTaskJourney.RunAsync(root, deadline.Token);
            Assert.Equal(new CdcStreamingTaskJourneyResult(2, 3, 3, 2, 1), result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WithCancelledToken_PreservesEmptyDirectory()
    {
        string root = Directory.CreateTempSubdirectory("sonnetdb-task-journey-test-").FullName;
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CdcStreamingTaskJourney.RunAsync(root, cancellation.Token));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
