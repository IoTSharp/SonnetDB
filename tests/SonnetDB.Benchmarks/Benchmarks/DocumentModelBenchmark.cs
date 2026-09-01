using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Kv;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// JSON 文档模型基准：在固定的 10k 文档与原生 JSON path 索引上度量 ID 点读和索引查询吞吐。
/// </summary>
[Config(typeof(DocumentModelBenchmarkConfig))]
[MemoryDiagnoser]
[BenchmarkCategory("Model", "Document")]
public class DocumentModelBenchmark
{
    private const int DocumentCount = 10_000;
    private const int SeedBatchSize = 250;
    private const int SiteCount = 100;
    private const int QueryBatchSize = 32;
    private const int QueryResultLimit = 32;
    private const string CollectionName = "device_documents";
    private const string SiteIndexName = "idx_site";
    private static readonly string Payload = new('x', 96);
    private string _rootDirectory = string.Empty;
    private Tsdb? _database;
    private DocumentCollectionStore? _store;
    private string _pointReadId = string.Empty;
    private DocumentQuery[] _indexedQueries = [];

    /// <summary>创建固定 JSON 语料、JSON path 索引和落盘 KV state。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "SonnetDB.Benchmarks",
            $"document-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDirectory);

        try
        {
            _database = Tsdb.Open(CreateOptions(_rootDirectory));
            DocumentCollectionSchema schema = DocumentCollectionSchema.Create(
                CollectionName,
                indexes: [new DocumentPathIndexDefinition(SiteIndexName, "$.site")]);
            _database.Documents.Create(schema);
            _store = _database.Documents.Open(CollectionName);

            for (int start = 0; start < DocumentCount; start += SeedBatchSize)
            {
                int count = Math.Min(SeedBatchSize, DocumentCount - start);
                var requests = new DocumentWriteRequest[count];
                for (int offset = 0; offset < count; offset++)
                {
                    int ordinal = start + offset;
                    requests[offset] = new DocumentWriteRequest(
                        CreateDocumentId(ordinal),
                        CreateDocumentJson(ordinal));
                }

                DocumentWriteResult result = _store.InsertMany(requests);
                if (!result.Committed || result.Inserted != count || result.HasErrors)
                    throw new InvalidDataException("Document benchmark fixture 未完整写入固定批次。");
            }

            _database.Documents.CompactAll();
            _pointReadId = CreateDocumentId(DocumentCount / 2);
            _indexedQueries = new DocumentQuery[QueryBatchSize];
            for (int index = 0; index < _indexedQueries.Length; index++)
                _indexedQueries[index] = CreateSiteQuery(index % SiteCount);

            DocumentQueryResult validation = DocumentQueryPlanner.Execute(
                _store,
                _store.Schema,
                _indexedQueries[0]);
            if (_store.Get(_pointReadId) is null
                || validation.AccessPath != "document_index"
                || validation.IndexName != SiteIndexName
                || validation.MatchedCount != DocumentCount / SiteCount
                || validation.Items.Count != QueryResultLimit)
            {
                throw new InvalidDataException("Document benchmark fixture 或索引访问路径校验失败。");
            }
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    /// <summary>度量原生文档 API 按 ID 读取一条 JSON 文档的延迟与分配。</summary>
    /// <returns>文档内容与版本的轻量校验和。</returns>
    [Benchmark(Baseline = true, Description = "Document ID read latency (10k documents)")]
    public long GetByIdLatency() => ReadByIdOnce();

    /// <summary>执行一次文档 ID 点读，供 BenchmarkDotNet 与请求级尾延迟 runner 共用。</summary>
    internal long ReadByIdOnce()
    {
        DocumentRow row = RequireStore().Get(_pointReadId)
            ?? throw new InvalidDataException("Document benchmark 点读未命中固定文档。");
        return row.Json.Length + row.Version;
    }

    /// <summary>度量 32 次原生 JSON path 索引查询的归一化吞吐。</summary>
    /// <returns>所有查询命中数的校验和。</returns>
    [Benchmark(OperationsPerInvoke = QueryBatchSize, Description = "Document indexed query throughput (32 site predicates)")]
    public int IndexedJsonPathQueryThroughput()
    {
        int checksum = 0;
        for (int index = 0; index < _indexedQueries.Length; index++)
            checksum += ReadIndexedQueryOnce(index);

        return checksum;
    }

    /// <summary>执行一次 JSON path 索引查询，并返回命中规模校验和。</summary>
    internal int ReadIndexedQueryOnce(int queryOrdinal)
    {
        DocumentCollectionStore store = RequireStore();
        int index = Math.Abs(queryOrdinal % _indexedQueries.Length);
        DocumentQueryResult result = DocumentQueryPlanner.Execute(store, store.Schema, _indexedQueries[index]);
        return result.Items.Count + result.MatchedCount;
    }

    /// <summary>关闭数据库并删除当前基准独占的临时目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _store = null;
        _database?.Dispose();
        _database = null;
        DeleteFixtureDirectory();
    }

    /// <summary>创建关闭后台 KV 维护且不逐写 fsync 的固定读基准选项。</summary>
    private static TsdbOptions CreateOptions(string rootDirectory)
        => new()
        {
            RootDirectory = rootDirectory,
            Kv = KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            },
        };

    /// <summary>生成固定宽度的文档 ID。</summary>
    private static string CreateDocumentId(int ordinal) => $"device-{ordinal:D8}";

    /// <summary>生成包含稳定分布字段的 JSON 文档。</summary>
    private static string CreateDocumentJson(int ordinal)
        => $"{{\"tenant\":\"tenant-{ordinal % 10:D2}\",\"site\":\"site-{ordinal % SiteCount:D3}\","
            + $"\"status\":\"{(ordinal % 4 == 0 ? "alarm" : "normal")}\",\"reading\":{ordinal},"
            + $"\"payload\":\"{Payload}\"}}";

    /// <summary>创建命中固定站点分区的 JSON path 查询。</summary>
    private static DocumentQuery CreateSiteQuery(int siteOrdinal)
        => new(
            Filter: new DocumentFieldFilter(
                DocumentFieldRef.JsonPath("$.site"),
                DocumentFilterOperator.Equal,
                $"site-{siteOrdinal:D3}"),
            Limit: QueryResultLimit);

    /// <summary>返回已完成 setup 的文档 store。</summary>
    private DocumentCollectionStore RequireStore()
        => _store ?? throw new InvalidOperationException("请先调用 Setup 创建 Document benchmark fixture。");

    /// <summary>仅删除本类创建且位于专用临时父目录下的 fixture。</summary>
    private void DeleteFixtureDirectory()
    {
        if (string.IsNullOrWhiteSpace(_rootDirectory) || !Directory.Exists(_rootDirectory))
            return;

        string allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SonnetDB.Benchmarks"));
        string fixtureRoot = Path.GetFullPath(_rootDirectory);
        string relative = Path.GetRelativePath(allowedRoot, fixtureRoot);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("拒绝删除基准专用临时目录之外的路径。");
        }

        Directory.Delete(fixtureRoot, recursive: true);
        _rootDirectory = string.Empty;
    }
}

/// <summary>为文档模型报告 BenchmarkDotNet 迭代统计、吞吐与分配。</summary>
internal sealed class DocumentModelBenchmarkConfig : ManualConfig
{
    /// <summary>配置固定预热与测量轮次；请求级分位数由独立 evidence runner 采集。</summary>
    public DocumentModelBenchmarkConfig()
    {
        BuildTimeout = TimeSpan.FromMinutes(5);
        AddJob(Job.Default.WithWarmupCount(2).WithIterationCount(8));
        AddColumn(
            StatisticColumn.Median,
            StatisticColumn.P95,
            StatisticColumn.OperationsPerSecond);
    }
}
