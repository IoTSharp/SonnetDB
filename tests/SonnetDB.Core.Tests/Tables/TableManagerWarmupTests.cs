using SonnetDB.Kv;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证关系表冷开预热不会把恢复成本留给首个业务查询。</summary>
public sealed class TableManagerWarmupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-table-warmup-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建独立关系表目录。</summary>
    public TableManagerWarmupTests() => Directory.CreateDirectory(_root);

    /// <summary>清理测试生成的关系表目录。</summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>验证重启后预热会打开全部已有关系表，重复预热不会新增 store。</summary>
    [Fact]
    public void WarmUpAll_AfterRestart_OpensEveryExistingTableOnce()
    {
        using (var preparation = new TableManager(_root, KvOptions.Default))
        {
            preparation.Create(CreateSchema("weights"));
            preparation.Create(CreateSchema("captures"));
            preparation.Open("weights").Insert([1L, "weight-1"]);
            preparation.Open("captures").Insert([2L, "capture-2"]);
        }

        using var restarted = new TableManager(_root, KvOptions.Default);
        Assert.Equal(0, restarted.OpenedStoreCountForEvidence);

        IReadOnlyList<string> warmed = restarted.WarmUpAll();

        Assert.Equal(2, warmed.Count);
        Assert.Contains("weights", warmed);
        Assert.Contains("captures", warmed);
        Assert.Equal(2, restarted.OpenedStoreCountForEvidence);
        Assert.Equal(1, restarted.Open("weights").RowCount);
        Assert.Equal(1, restarted.Open("captures").RowCount);

        IReadOnlyList<string> repeated = restarted.WarmUpAll();
        Assert.Equal(2, repeated.Count);
        Assert.Equal(2, restarted.OpenedStoreCountForEvidence);
    }

    /// <summary>验证不同关系表能够同时进入冷开阶段，避免恢复被单表串行限制。</summary>
    [Fact]
    public void WarmUpAll_WithConcurrency_OpensDifferentTablesInParallel()
    {
        using (var preparation = new TableManager(_root, KvOptions.Default))
        {
            preparation.Create(CreateSchema("weights"));
            preparation.Create(CreateSchema("captures"));
        }

        using var restarted = new TableManager(_root, KvOptions.Default);
        using var barrier = new Barrier(participantCount: 2);
        restarted.WarmUpBeforeOpenTestHook = _ =>
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));

        IReadOnlyList<string> warmed = restarted.WarmUpAll(
            CancellationToken.None,
            maxDegreeOfParallelism: 2);

        Assert.Equal(2, warmed.Count);
        Assert.Equal(2, restarted.OpenedStoreCountForEvidence);
    }

    /// <summary>创建带主键的最小关系表 schema。</summary>
    private static TableSchema CreateSchema(string name)
        => TableSchema.Create(
            name,
            [("id", TableColumnType.Int64, false), ("value", TableColumnType.String, false)],
            ["id"]);
}
