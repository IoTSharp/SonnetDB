using SonnetDB.Samples;
using Xunit;

namespace SonnetDB.Core.Tests.Audits;

/// <summary>验证真实 JSON 文件、文档/measurement 向量预算和数据库重开的组合入口。</summary>
public sealed class SqlBudgetJourneyTests
{
    /// <summary>所有查询拒绝后仍可正常查询，且重开保存了同一向量结果。</summary>
    [Fact]
    public async Task RunAsync_FileAndVectorQueries_ReopensAndRejectsBudgets()
    {
        string root = Directory.CreateTempSubdirectory("sonnetdb-budget-journey-").FullName;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Assert.Equal(new SqlBudgetJourneyResult(3, "a", 0, 3), await SqlBudgetJourney.RunAsync(root, timeout.Token));
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root), ignoreCase: true);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>预取消不创建数据库或输入文件。</summary>
    [Fact]
    public async Task RunAsync_PreCancelled_DoesNotCreateArtifacts()
    {
        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-budget-journey-" + Guid.NewGuid().ToString("N"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlBudgetJourney.RunAsync(root, cancelled.Token));
        Assert.False(Directory.Exists(root));
    }
}
