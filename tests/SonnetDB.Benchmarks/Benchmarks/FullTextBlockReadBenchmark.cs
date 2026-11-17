using BenchmarkDotNet.Attributes;
using SonnetDB.FullText.Index;
using SonnetDB.FullText.Query;
using SonnetDB.FullText.Storage;
using SonnetDB.FullText.Tokenizers.Unicode;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// 度量持久化全文索引的冷读和热读路径；索引内部保留 block 读取统计供 smoke/测试对账。
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("FullText", "BlockRead")]
public class FullTextBlockReadBenchmark
{
    private const int CorpusSize = 1_024;
    private string _directory = string.Empty;
    private OrQuery _query = null!;
    private PersistentFullTextIndex _hotIndex = null!;

    /// <summary>创建固定小语料和热读索引。</summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "sonnetdb-fulltext-block-read-benchmark",
            Guid.NewGuid().ToString("N"));
        PersistentIndexOptions options = new()
        {
            EnableBackgroundMerge = false,
            FullTextBlockCacheMaxBytes = 4L * 1024 * 1024,
        };

        PersistentFullTextIndex seed = PersistentFullTextIndex.Open(
            _directory,
            new UnicodeTokenizer(),
            options: options);
        var documents = new Document[CorpusSize];
        for (int index = 0; index < documents.Length; index++)
        {
            string body = index % 2 == 0 ? "alpha beta" : "alpha";
            documents[index] = new Document(new DocumentId($"doc-{index:D5}")).Set("body", body);
        }

        seed.IndexMany(documents);
        _query = new OrQuery([new TermQuery("body", "alpha"), new TermQuery("body", "beta")]);
        _hotIndex = PersistentFullTextIndex.Open(_directory, new UnicodeTokenizer(), options: options);
        _ = _hotIndex.Search(_query, topK: 20);
    }

    /// <summary>每次打开索引并查询，缓存为空，代表端到端冷读。</summary>
    [Benchmark(Description = "PersistentFullTextIndex cold block read")]
    public int ColdRead()
    {
        PersistentFullTextIndex coldIndex = PersistentFullTextIndex.Open(
            _directory,
            new UnicodeTokenizer(),
            options: new PersistentIndexOptions
            {
                EnableBackgroundMerge = false,
                FullTextBlockCacheMaxBytes = 4L * 1024 * 1024,
            });
        return coldIndex.Search(_query, topK: 20).Count;
    }

    /// <summary>复用同一索引实例和 block 缓存，代表端到端热读。</summary>
    [Benchmark(Description = "PersistentFullTextIndex hot block read")]
    public int HotRead()
        => _hotIndex.Search(_query, topK: 20).Count;

    /// <summary>释放本基准创建的临时目录。</summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
