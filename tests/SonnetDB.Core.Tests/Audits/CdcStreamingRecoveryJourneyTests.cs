using SonnetDB.Samples;

namespace SonnetDB.Core.Tests.Audits;

/// <summary>M43 #396 真实组合入口的恢复及取消回归。</summary>
public sealed class CdcStreamingRecoveryJourneyTests
{
    /// <summary>两分区源变更及 ACK 前中断恢复后，精确对账物化结果和窗口计数。</summary>
    [Fact]
    public async Task RunAsync_AfterSnapshotAndUnacknowledgedWindowReopen_ReconcilesProductState()
    {
        string directory = Directory.CreateTempSubdirectory("sonnetdb-composed-test-").FullName;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            CdcStreamingJourneyResult result = await CdcStreamingRecoveryJourney.RunAsync(directory, timeout.Token);
            Assert.Equal(2, result.Partitions);
            Assert.Equal(3, result.ReconciledRows);
            Assert.Equal(3, result.WindowCount);
            Assert.Equal(2, result.RedeliveryAttempt);
            Assert.Equal(0, result.PendingEvents);
            Assert.True(result.WindowClosed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>预先取消时不创建文件或改写已有调用方数据。</summary>
    [Fact]
    public async Task RunAsync_WithCancelledToken_DoesNotCreateState()
    {
        string directory = Directory.CreateTempSubdirectory("sonnetdb-composed-test-").FullName;
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CdcStreamingRecoveryJourney.RunAsync(directory, cancellation.Token));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>非空目录被显式拒绝，已有调用方文件保持原样。</summary>
    [Fact]
    public async Task RunAsync_WithExistingData_RejectsWithoutOverwriting()
    {
        string directory = Directory.CreateTempSubdirectory("sonnetdb-composed-test-").FullName;
        try
        {
            string marker = Path.Combine(directory, "caller.txt");
            await File.WriteAllTextAsync(marker, "preserve");
            await Assert.ThrowsAsync<IOException>(() => CdcStreamingRecoveryJourney.RunAsync(directory));
            Assert.Equal("preserve", await File.ReadAllTextAsync(marker));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
