using SonnetDB.Documents.Vector;
using SonnetDB.FullText;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Documents;

/// <summary>
/// 管理同一数据库目录下的 JSON 文档集合 schema 与 KV-backed 主数据。
/// </summary>
public sealed class DocumentCollectionManager : IDisposable
{
    private readonly object _sync = new();
    private readonly object _schemaSync;
    private readonly string _rootDirectory;
    private readonly KvOptions _kvOptions;
    private readonly Action<string, string>? _nameAvailabilityGuard;
    private readonly Action<string, string>? _schemaMutationGuard;
    private readonly Dictionary<string, DocumentCollectionStore> _stores = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// 初始化文档集合管理器。
    /// </summary>
    /// <param name="rootDirectory">documents 根目录。</param>
    /// <param name="kvOptions">底层 KV 选项。</param>
    public DocumentCollectionManager(string rootDirectory, KvOptions kvOptions)
        : this(
            rootDirectory,
            kvOptions,
            nameAvailabilityGuard: null,
            schemaMutationGuard: null,
            synchronizationRoot: new object())
    {
    }

    internal DocumentCollectionManager(
        string rootDirectory,
        KvOptions kvOptions,
        Action<string, string>? nameAvailabilityGuard,
        Action<string, string>? schemaMutationGuard,
        object synchronizationRoot)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(kvOptions);
        ArgumentNullException.ThrowIfNull(synchronizationRoot);

        _schemaSync = synchronizationRoot;
        _rootDirectory = rootDirectory;
        _kvOptions = kvOptions;
        _nameAvailabilityGuard = nameAvailabilityGuard;
        _schemaMutationGuard = schemaMutationGuard;
        Directory.CreateDirectory(_rootDirectory);

        Catalog = new DocumentCollectionCatalog();
        foreach (var schema in DocumentCollectionSchemaCodec.Load(SchemaPath))
            Catalog.LoadOrReplace(schema);
        Catalog.MutationGuard = EnsureManagedCatalogMutation;
    }

    /// <summary>文档集合 catalog。</summary>
    public DocumentCollectionCatalog Catalog { get; }

    /// <summary>文档集合 schema 文件路径。</summary>
    public string SchemaPath => Path.Combine(_rootDirectory, DocumentCollectionSchemaCodec.FileName);

    /// <summary>
    /// 创建文档集合并持久化 schema。
    /// </summary>
    /// <param name="schema">集合 schema。</param>
    public void Create(DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                _nameAvailabilityGuard?.Invoke(schema.Name, "document collection");
                Catalog.Add(schema);
                try
                {
                    PersistCatalogLocked();
                    _ = OpenStoreLocked(schema);
                }
                catch
                {
                    Catalog.Remove(schema.Name);
                    throw;
                }
            }
    }

    /// <summary>
    /// 为已有文档集合创建文档二级索引并持久化 schema。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="definition">索引声明。</param>
    /// <returns>新建的索引声明。</returns>
    public DocumentPathIndex CreateIndex(string collectionName, DocumentPathIndexDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(definition);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                var current = Catalog.TryGet(collectionName)
                    ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");

                var updated = current.WithIndex(definition);
                var store = OpenStoreLocked(current);
                store.ApplySchema(updated);
                Catalog.LoadOrReplace(updated);
                try
                {
                    PersistCatalogLocked();
                }
                catch
                {
                    store.ApplySchema(current);
                    Catalog.LoadOrReplace(current);
                    throw;
                }

                return updated.TryGetIndex(definition.Name)
                    ?? throw new InvalidOperationException("内部错误：文档索引创建后未能读取 schema。");
            }
    }

    internal void EnsureIndexes(
        string collectionName,
        IReadOnlyList<DocumentPathIndexDefinition> definitions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (DocumentPathIndexDefinition definition in definitions)
            ArgumentNullException.ThrowIfNull(definition);

        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                var current = Catalog.TryGet(collectionName)
                    ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
                var updated = current;
                foreach (DocumentPathIndexDefinition definition in definitions)
                {
                    if (updated.TryGetIndex(definition.Name) is null)
                        updated = updated.WithIndex(definition);
                }

                if (ReferenceEquals(updated, current))
                    return;

                var store = OpenStoreLocked(current);
                store.ApplySchema(updated);
                Catalog.LoadOrReplace(updated);
                try
                {
                    PersistCatalogLocked();
                }
                catch
                {
                    store.ApplySchema(current);
                    Catalog.LoadOrReplace(current);
                    throw;
                }
            }
    }

    /// <summary>
    /// 为已有文档集合创建全文索引并持久化 schema。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="definition">全文索引声明。</param>
    /// <returns>新建的全文索引声明。</returns>
    public DocumentFullTextIndex CreateFullTextIndex(string collectionName, DocumentFullTextIndexDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(definition);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                var current = Catalog.TryGet(collectionName)
                    ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");

                var updated = current.WithFullTextIndex(definition);
                var store = OpenStoreLocked(current);
                store.ApplySchema(updated);
                Catalog.LoadOrReplace(updated);
                try
                {
                    PersistCatalogLocked();
                }
                catch
                {
                    store.ApplySchema(current);
                    Catalog.LoadOrReplace(current);
                    throw;
                }

                return updated.TryGetFullTextIndex(definition.Name)
                    ?? throw new InvalidOperationException("内部错误：全文索引创建后未能读取 schema。");
            }
    }

    /// <summary>
    /// 为已有文档集合创建向量（HNSW ANN）索引并持久化 schema。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="definition">向量索引声明。</param>
    /// <returns>新建的向量索引声明。</returns>
    public DocumentVectorIndex CreateVectorIndex(string collectionName, DocumentVectorIndexDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(definition);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                var current = Catalog.TryGet(collectionName)
                    ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");

                var updated = current.WithVectorIndex(definition);
                var store = OpenStoreLocked(current);
                store.ApplySchema(updated);
                Catalog.LoadOrReplace(updated);
                try
                {
                    PersistCatalogLocked();
                }
                catch
                {
                    store.ApplySchema(current);
                    Catalog.LoadOrReplace(current);
                    throw;
                }

                return updated.TryGetVectorIndex(definition.Name)
                    ?? throw new InvalidOperationException("内部错误：向量索引创建后未能读取 schema。");
            }
    }

    /// <summary>
    /// 设置或替换已有文档集合的 validator，并持久化 schema。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="definition">validator 声明。</param>
    /// <returns>更新后的 validator。</returns>
    public DocumentValidator SetValidator(string collectionName, DocumentValidatorDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(definition);
        lock (_schemaSync)
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(collectionName, "ALTER DOCUMENT COLLECTION");
                ThrowIfDisposed();
                var current = Catalog.TryGet(collectionName)
                    ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");

                var updated = current.WithValidator(definition);
                var store = OpenStoreLocked(current);
                store.ApplySchema(updated);
                Catalog.LoadOrReplace(updated);
                try
                {
                    PersistCatalogLocked();
                }
                catch
                {
                    store.ApplySchema(current);
                    Catalog.LoadOrReplace(current);
                    throw;
                }

                return updated.Validator
                    ?? throw new InvalidOperationException("内部错误：文档 validator 设置后未能读取 schema。");
            }
    }

    /// <summary>
    /// 删除已有文档集合的 validator，并持久化 schema。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <returns>validator 存在并删除时返回 true。</returns>
    public bool DropValidator(string collectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        lock (_schemaSync)
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(collectionName, "ALTER DOCUMENT COLLECTION");
                ThrowIfDisposed();
                var current = Catalog.TryGet(collectionName)
                    ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
                if (current.Validator is null)
                    return false;

                var updated = current.WithoutValidator();
                var store = OpenStoreLocked(current);
                store.ApplySchema(updated);
                Catalog.LoadOrReplace(updated);
                try
                {
                    PersistCatalogLocked();
                }
                catch
                {
                    store.ApplySchema(current);
                    Catalog.LoadOrReplace(current);
                    throw;
                }

                return true;
            }
    }

    /// <summary>
    /// 删除文档集合二级索引声明。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="indexName">索引名。</param>
    /// <returns>索引存在并删除时返回 true。</returns>
    public bool DropIndex(string collectionName, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                var current = Catalog.TryGet(collectionName)
                    ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
                if (current.TryGetIndex(indexName) is null)
                    return false;

                var updated = current.WithoutIndex(indexName);
                var store = OpenStoreLocked(current);
                store.ApplySchema(updated);
                Catalog.LoadOrReplace(updated);
                try
                {
                    PersistCatalogLocked();
                }
                catch
                {
                    store.ApplySchema(current);
                    Catalog.LoadOrReplace(current);
                    throw;
                }

                return true;
            }
    }

    /// <summary>
    /// 删除文档集合全文索引声明和派生索引目录。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="indexName">索引名。</param>
    /// <returns>索引存在并删除时返回 true。</returns>
    public bool DropFullTextIndex(string collectionName, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                var current = Catalog.TryGet(collectionName)
                    ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
                if (current.TryGetFullTextIndex(indexName) is null)
                    return false;

                var updated = current.WithoutFullTextIndex(indexName);
                var store = OpenStoreLocked(current);
                store.ApplySchema(updated);
                Catalog.LoadOrReplace(updated);
                try
                {
                    PersistCatalogLocked();
                    string indexDirectory = FullTextIndexDirectory(collectionName, indexName);
                    if (Directory.Exists(indexDirectory))
                        Directory.Delete(indexDirectory, recursive: true);
                }
                catch
                {
                    store.ApplySchema(current);
                    Catalog.LoadOrReplace(current);
                    throw;
                }

                return true;
            }
    }

    /// <summary>
    /// 删除文档集合向量索引声明和派生索引目录。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="indexName">索引名。</param>
    /// <returns>索引存在并删除时返回 true。</returns>
    public bool DropVectorIndex(string collectionName, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                var current = Catalog.TryGet(collectionName)
                    ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
                if (current.TryGetVectorIndex(indexName) is null)
                    return false;

                var updated = current.WithoutVectorIndex(indexName);
                var store = OpenStoreLocked(current);
                store.ApplySchema(updated);
                Catalog.LoadOrReplace(updated);
                try
                {
                    PersistCatalogLocked();
                    string indexDirectory = VectorIndexDirectory(collectionName, indexName);
                    if (Directory.Exists(indexDirectory))
                        Directory.Delete(indexDirectory, recursive: true);
                }
                catch
                {
                    store.ApplySchema(current);
                    Catalog.LoadOrReplace(current);
                    throw;
                }

                return true;
            }
    }

    /// <summary>
    /// 从文档集合主数据强制同步重建指定向量索引，并返回当前向量数量。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="indexName">向量索引名。</param>
    /// <returns>向量索引当前向量数量。</returns>
    public int RebuildVectorIndex(string collectionName, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (_sync)
        {
            ThrowIfDisposed();
            var schema = Catalog.TryGet(collectionName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
            var index = schema.TryGetVectorIndex(indexName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 中向量索引 '{indexName}' 不存在。");
            return OpenStoreLocked(schema).RebuildVectorIndex(index, VectorIndexDirectory(collectionName, indexName));
        }
    }

    /// <summary>读取当前 catalog 和已经加载的向量图状态，不打开集合或重建派生索引。</summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="indexName">向量索引名。</param>
    /// <returns>轻量运行状态；不会执行主数据一致性或召回检查。</returns>
    public DocumentVectorIndexHealth GetVectorIndexHealth(string collectionName, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        SqlRagResourceScope.Demand(collectionName);
        lock (_sync)
        {
            ThrowIfDisposed();
            DocumentCollectionSchema schema = Catalog.TryGet(collectionName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
            DocumentVectorIndex index = schema.TryGetVectorIndex(indexName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 中向量索引 '{indexName}' 不存在。");
            return _stores.TryGetValue(collectionName, out var store)
                ? store.GetVectorIndexHealth(index)
                : new(index, "not_loaded", null);
        }
    }

    /// <summary>
    /// 从文档集合主数据重建指定文档二级索引。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="indexName">索引名。</param>
    /// <returns>重建后的索引声明。</returns>
    public DocumentPathIndex RebuildIndex(string collectionName, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (_sync)
        {
            ThrowIfDisposed();
            var schema = Catalog.TryGet(collectionName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
            var index = schema.TryGetIndex(indexName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 中索引 '{indexName}' 不存在。");
            OpenStoreLocked(schema).ApplySchema(schema);
            return index;
        }
    }

    /// <summary>
    /// 从文档集合主数据强制同步重建指定全文索引，并返回当前可见文档数。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="indexName">全文索引名。</param>
    /// <returns>全文索引当前可见文档数。</returns>
    public int RebuildFullTextIndex(string collectionName, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (_sync)
        {
            ThrowIfDisposed();
            var schema = Catalog.TryGet(collectionName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
            var index = schema.TryGetFullTextIndex(indexName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 中全文索引 '{indexName}' 不存在。");
            return OpenStoreLocked(schema).RebuildFullTextIndex(index, FullTextIndexDirectory(collectionName, indexName));
        }
    }

    /// <summary>读取指定全文索引的字段与分析器设置。</summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="indexName">全文索引名。</param>
    /// <returns>全文索引设置。</returns>
    public DocumentFullTextIndexSettings GetFullTextIndexSettings(string collectionName, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (_sync)
        {
            ThrowIfDisposed();
            DocumentCollectionSchema schema = Catalog.TryGet(collectionName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
            DocumentFullTextIndex index = schema.TryGetFullTextIndex(indexName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 中全文索引 '{indexName}' 不存在。");
            return index.Settings;
        }
    }

    /// <summary>使用指定全文索引的分析器处理一段文本。</summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="indexName">全文索引名。</param>
    /// <param name="text">待分析文本。</param>
    /// <returns>过滤后的词元。</returns>
    public IReadOnlyList<SonnetDB.FullText.Tokenization.Token> AnalyzeFullText(
        string collectionName,
        string indexName,
        string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(text);
        lock (_sync)
        {
            ThrowIfDisposed();
            DocumentCollectionSchema schema = Catalog.TryGet(collectionName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
            DocumentFullTextIndex index = schema.TryGetFullTextIndex(indexName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 中全文索引 '{indexName}' 不存在。");
            return OpenStoreLocked(schema).AnalyzeFullText(index, text);
        }
    }

    /// <summary>解释指定全文索引对文档的相关性评分。</summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="indexName">全文索引名。</param>
    /// <param name="field">索引字段或 <c>*</c>。</param>
    /// <param name="queryText">查询文本。</param>
    /// <param name="documentId">文档 ID。</param>
    /// <param name="mode">检索模式。</param>
    /// <param name="queryKind">查询组合方式。</param>
    /// <returns>相关性解释。</returns>
    public SonnetDB.FullText.DocumentFullTextRelevanceExplanation ExplainFullText(
        string collectionName,
        string indexName,
        string field,
        string queryText,
        string documentId,
        SonnetDB.FullText.FullTextSearchMode mode = SonnetDB.FullText.FullTextSearchMode.Exact,
        SonnetDB.FullText.FullTextQueryKind queryKind = SonnetDB.FullText.FullTextQueryKind.All)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryText);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        lock (_sync)
        {
            ThrowIfDisposed();
            DocumentCollectionSchema schema = Catalog.TryGet(collectionName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
            DocumentFullTextIndex index = schema.TryGetFullTextIndex(indexName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 中全文索引 '{indexName}' 不存在。");
            return OpenStoreLocked(schema).ExplainFullText(index, field, queryText, documentId, mode, queryKind);
        }
    }

    /// <summary>读取指定全文索引最近一次重建任务状态。</summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="indexName">全文索引名。</param>
    /// <returns>重建进度。</returns>
    public SonnetDB.FullText.DocumentFullTextRebuildProgress GetFullTextRebuildProgress(
        string collectionName,
        string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (_sync)
        {
            ThrowIfDisposed();
            DocumentCollectionSchema schema = Catalog.TryGet(collectionName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
            DocumentFullTextIndex index = schema.TryGetFullTextIndex(indexName)
                ?? throw new InvalidOperationException($"document collection '{collectionName}' 中全文索引 '{indexName}' 不存在。");
            return OpenStoreLocked(schema).GetFullTextRebuildProgress(index);
        }
    }

    /// <summary>
    /// 删除文档集合 schema 与主数据目录。
    /// </summary>
    /// <param name="name">集合名。</param>
    /// <returns>存在并删除时返回 true。</returns>
    public bool Drop(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        SqlRagResourceScope.Demand(name);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                _schemaMutationGuard?.Invoke(name, "DROP DOCUMENT COLLECTION");
                return DropLocked(name, cleanupOrphanedStorage: false);
            }
    }

    /// <summary>幂等删除 retired generation 独占的 collection 及崩溃遗留目录。</summary>
    internal bool DropGenerationResource(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                _schemaMutationGuard?.Invoke(name, "DROP DOCUMENT COLLECTION");
                return DropLocked(name, cleanupOrphanedStorage: true);
            }
    }

    /// <summary>
    /// 打开已存在的文档集合。
    /// </summary>
    /// <param name="name">集合名。</param>
    public DocumentCollectionStore Open(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        SqlRagResourceScope.Demand(name);
        lock (_sync)
        {
            ThrowIfDisposed();
            var schema = Catalog.TryGet(name)
                ?? throw new InvalidOperationException($"document collection '{name}' 不存在。");
            return OpenStoreLocked(schema);
        }
    }

    /// <summary>
    /// 为所有文档集合主数据创建 KV 快照，确保备份可独立恢复最近写入。
    /// </summary>
    public IReadOnlyList<string> CheckpointAll()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var names = Catalog.Snapshot().Select(static s => s.Name).ToArray();
            foreach (string name in names)
                OpenStoreLocked(Catalog.TryGet(name)!).CreateSnapshot();
            return names;
        }
    }

    /// <summary>
    /// 为所有文档集合主数据和二级索引创建磁盘有序 KV 段，降低冷启动后的常驻 value 内存。
    /// </summary>
    public IReadOnlyList<string> CompactAll()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var names = Catalog.Snapshot().Select(static s => s.Name).ToArray();
            foreach (string name in names)
                OpenStoreLocked(Catalog.TryGet(name)!).Compact();
            return names;
        }
    }

    /// <summary>
    /// 关闭所有已打开的文档集合 store。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var store in _stores.Values)
                store.Dispose();
            _stores.Clear();
        }
    }

    private DocumentCollectionStore OpenStoreLocked(DocumentCollectionSchema schema)
    {
        SqlRagResourceScope.Demand(schema.Name);
        if (_stores.TryGetValue(schema.Name, out var existing))
            return existing;

        string collectionDirectory = CollectionDirectory(schema.Name);
        var kv = KvKeyspace.Open("document." + schema.Name, collectionDirectory, _kvOptions);
        var store = new DocumentCollectionStore(
            schema,
            kv,
            index => DocumentFullTextIndexStore.Open(FullTextIndexDirectory(schema.Name, index.Name), index),
            index => DocumentVectorIndexStore.Open(VectorIndexDirectory(schema.Name, index.Name), index, _kvOptions));
        _stores[schema.Name] = store;
        return store;
    }

    private bool DropLocked(string name, bool cleanupOrphanedStorage)
    {
        bool removedCatalog = Catalog.Remove(name);
        if (!removedCatalog && !cleanupOrphanedStorage)
            return false;

        if (_stores.Remove(name, out DocumentCollectionStore? store))
            store.Dispose();
        if (removedCatalog)
            PersistCatalogLocked();

        bool removedStorage = DeleteDirectoryIfExists(CollectionDirectory(name));
        removedStorage |= DeleteDirectoryIfExists(Path.Combine(_rootDirectory, "fulltext", EncodeName(name)));
        removedStorage |= DeleteDirectoryIfExists(Path.Combine(_rootDirectory, "vector", EncodeName(name)));
        return removedCatalog || removedStorage;
    }

    private static bool DeleteDirectoryIfExists(string directory)
    {
        if (!Directory.Exists(directory))
            return false;
        Directory.Delete(directory, recursive: true);
        return true;
    }

    private string CollectionDirectory(string name) => Path.Combine(_rootDirectory, "collections", EncodeName(name));

    private string FullTextIndexDirectory(string collectionName, string indexName)
        => Path.Combine(_rootDirectory, "fulltext", EncodeName(collectionName), EncodeName(indexName));

    private string VectorIndexDirectory(string collectionName, string indexName)
        => Path.Combine(_rootDirectory, "vector", EncodeName(collectionName), EncodeName(indexName));

    private void PersistCatalogLocked()
        => DocumentCollectionSchemaCodec.Save(SchemaPath, Catalog.SnapshotForPersistence());

    /// <summary>阻止调用方绕过 DocumentCollectionManager 的 schema 锁和持久化路径直接修改目录。</summary>
    private void EnsureManagedCatalogMutation(string collectionName, string operation)
    {
        SqlRagResourceScope.Demand(collectionName);
        if (!Monitor.IsEntered(_schemaSync))
        {
            throw new InvalidOperationException(
                $"不能直接对受管理的 DocumentCollectionCatalog 执行 {operation} '{collectionName}'；请使用 DocumentCollectionManager 的 schema API。");
        }
    }

    private static string EncodeName(string name)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
