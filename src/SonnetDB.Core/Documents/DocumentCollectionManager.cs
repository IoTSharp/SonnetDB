using SonnetDB.FullText;
using SonnetDB.Kv;

namespace SonnetDB.Documents;

/// <summary>
/// 管理同一数据库目录下的 JSON 文档集合 schema 与 KV-backed 主数据。
/// </summary>
public sealed class DocumentCollectionManager : IDisposable
{
    private readonly object _sync = new();
    private readonly string _rootDirectory;
    private readonly KvOptions _kvOptions;
    private readonly Dictionary<string, DocumentCollectionStore> _stores = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// 初始化文档集合管理器。
    /// </summary>
    /// <param name="rootDirectory">documents 根目录。</param>
    /// <param name="kvOptions">底层 KV 选项。</param>
    public DocumentCollectionManager(string rootDirectory, KvOptions kvOptions)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(kvOptions);

        _rootDirectory = rootDirectory;
        _kvOptions = kvOptions;
        Directory.CreateDirectory(_rootDirectory);

        Catalog = new DocumentCollectionCatalog();
        foreach (var schema in DocumentCollectionSchemaCodec.Load(SchemaPath))
            Catalog.LoadOrReplace(schema);
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
        lock (_sync)
        {
            ThrowIfDisposed();
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
    /// 设置或替换已有文档集合的 validator，并持久化 schema。
    /// </summary>
    /// <param name="collectionName">集合名。</param>
    /// <param name="definition">validator 声明。</param>
    /// <returns>更新后的 validator。</returns>
    public DocumentValidator SetValidator(string collectionName, DocumentValidatorDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
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
        lock (_sync)
        {
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

    /// <summary>
    /// 删除文档集合 schema 与主数据目录。
    /// </summary>
    /// <param name="name">集合名。</param>
    /// <returns>存在并删除时返回 true。</returns>
    public bool Drop(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!Catalog.Remove(name))
                return false;

            if (_stores.Remove(name, out var store))
                store.Dispose();

            PersistCatalogLocked();
            string collectionDirectory = CollectionDirectory(name);
            if (Directory.Exists(collectionDirectory))
                Directory.Delete(collectionDirectory, recursive: true);
            string fullTextDirectory = Path.Combine(_rootDirectory, "fulltext", EncodeName(name));
            if (Directory.Exists(fullTextDirectory))
                Directory.Delete(fullTextDirectory, recursive: true);

            return true;
        }
    }

    /// <summary>
    /// 打开已存在的文档集合。
    /// </summary>
    /// <param name="name">集合名。</param>
    public DocumentCollectionStore Open(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
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
        if (_stores.TryGetValue(schema.Name, out var existing))
            return existing;

        string collectionDirectory = CollectionDirectory(schema.Name);
        var kv = KvKeyspace.Open("document." + schema.Name, collectionDirectory, _kvOptions);
        var store = new DocumentCollectionStore(
            schema,
            kv,
            index => DocumentFullTextIndexStore.Open(FullTextIndexDirectory(schema.Name, index.Name), index));
        _stores[schema.Name] = store;
        return store;
    }

    private string CollectionDirectory(string name) => Path.Combine(_rootDirectory, "collections", EncodeName(name));

    private string FullTextIndexDirectory(string collectionName, string indexName)
        => Path.Combine(_rootDirectory, "fulltext", EncodeName(collectionName), EncodeName(indexName));

    private void PersistCatalogLocked()
        => DocumentCollectionSchemaCodec.Save(SchemaPath, Catalog.Snapshot());

    private static string EncodeName(string name)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
