using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using SonnetDB.Documents;
using SonnetDB.FullText;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// P3 活跃 term 基准：100k 历史 term 中 90% 已 tombstone，双字段双 token fuzzy 查询共享快照。
/// </summary>
[Config(typeof(FullTextActiveTermBenchmarkConfig))]
[BenchmarkCategory("P3", "FullText", "Fuzzy")]
public class FullTextActiveTermBenchmark
{
    private const int TotalTermCount = 100_000;
    private const int TombstonedTermCount = 90_000;
    private const int BatchSize = 5_000;

    private string _root = string.Empty;
    private DocumentFullTextIndexStore? _store;

    /// <summary>建立 100k 唯一 term，并删除前 90k 文档形成历史 tombstone。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sonnetdb-active-term-bench-{Guid.NewGuid():N}");
        _store = DocumentFullTextIndexStore.Open(
            _root,
            new DocumentFullTextIndex(
                "ft_bench",
                ["$.title", "$.body"],
                "unicode",
                DateTime.UtcNow.Ticks));

        for (int start = 0; start < TotalTermCount; start += BatchSize)
        {
            int count = Math.Min(BatchSize, TotalTermCount - start);
            var rows = new DocumentRow[count];
            for (int offset = 0; offset < count; offset++)
            {
                int ordinal = start + offset;
                string term = $"term{ordinal:D6}";
                string json = $"{{\"title\":\"{term}\",\"body\":\"{term} stable\"}}";
                rows[offset] = new DocumentRow($"doc-{ordinal:D6}", json, Version: 1);
            }
            _store.UpsertMany(rows);
        }

        _store.DeleteMany(Enumerable.Range(0, TombstonedTermCount)
            .Select(static ordinal => $"doc-{ordinal:D6}"));

        // 预热并固化活跃视图：两字段各构建一次，后续查询不得按 token 重建。
        _ = _store.TermCount;
        if (_store.ActiveTermSnapshotBuildCount != 2)
            throw new InvalidOperationException("活跃 term 快照预热次数不符合预期。");
        _ = Fuzzy_MultiFieldMultiToken_10kActiveTerms();
    }

    /// <summary>测量双字段双 token fuzzy 查询的延迟与分配。</summary>
    /// <returns>命中文档数。</returns>
    [Benchmark(Description = "P3 fuzzy: 100k terms / 90% tombstone / 2 fields / 2 tokens")]
    public int Fuzzy_MultiFieldMultiToken_10kActiveTerms()
    {
        IReadOnlyList<DocumentFullTextSearchHit> hits = _store!.Search(
            "*",
            "term099990 term099991",
            topK: 10,
            FullTextSearchMode.Fuzzy,
            FullTextQueryKind.Any);
        if (_store.ActiveTermSnapshotBuildCount != 2)
            throw new InvalidOperationException("同一代次的 fuzzy 查询重复构建了活跃 term 快照。");
        return hits.Count;
    }

    /// <summary>删除基准目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _store = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

internal sealed class FullTextActiveTermBenchmarkConfig : ManualConfig
{
    public FullTextActiveTermBenchmarkConfig()
    {
        AddJob(Job.Default.WithWarmupCount(2).WithIterationCount(5));
        AddColumn(StatisticColumn.Median, StatisticColumn.P90);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
