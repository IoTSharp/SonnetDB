using SonnetDB.Kv;

namespace SonnetDB.Tables;

/// <summary>
/// 管理同一数据库目录下的关系表 schema 与 rowstore。
/// </summary>
public sealed class TableManager : IDisposable
{
    private readonly object _sync = new();
    private readonly string _rootDirectory;
    private readonly KvOptions _kvOptions;
    private readonly Dictionary<string, TableStore> _stores = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// 初始化表管理器。
    /// </summary>
    /// <param name="rootDirectory">tables 根目录。</param>
    /// <param name="kvOptions">底层 KV 选项。</param>
    public TableManager(string rootDirectory, KvOptions kvOptions)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(kvOptions);

        _rootDirectory = rootDirectory;
        _kvOptions = kvOptions;
        Directory.CreateDirectory(_rootDirectory);

        Catalog = new TableCatalog();
        foreach (var schema in TableSchemaCodec.Load(SchemaPath))
            Catalog.LoadOrReplace(schema);
    }

    /// <summary>关系表 catalog。</summary>
    public TableCatalog Catalog { get; }

    /// <summary>表 schema 文件路径。</summary>
    public string SchemaPath => Path.Combine(_rootDirectory, TableSchemaCodec.FileName);

    /// <summary>
    /// 创建关系表并持久化 schema。
    /// </summary>
    /// <param name="schema">表 schema。</param>
    public void Create(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_sync)
        {
            ThrowIfDisposed();
            Catalog.Add(schema);
            PersistCatalogLocked();
            _ = OpenStoreLocked(schema);
        }
    }

    /// <summary>
    /// 删除关系表 schema 与 rowstore 目录。
    /// </summary>
    /// <param name="name">表名。</param>
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
            string tableDirectory = TableDirectory(name);
            if (Directory.Exists(tableDirectory))
                Directory.Delete(tableDirectory, recursive: true);

            return true;
        }
    }

    /// <summary>
    /// 打开已存在的关系表。
    /// </summary>
    /// <param name="name">表名。</param>
    public TableStore Open(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            ThrowIfDisposed();
            var schema = Catalog.TryGet(name)
                ?? throw new InvalidOperationException($"table '{name}' 不存在。");
            return OpenStoreLocked(schema);
        }
    }

    /// <summary>
    /// 尝试打开关系表。
    /// </summary>
    /// <param name="name">表名。</param>
    public TableStore? TryOpen(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            ThrowIfDisposed();
            var schema = Catalog.TryGet(name);
            return schema is null ? null : OpenStoreLocked(schema);
        }
    }

    /// <summary>
    /// 关闭所有已打开的关系表 rowstore。
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

    private TableStore OpenStoreLocked(TableSchema schema)
    {
        if (_stores.TryGetValue(schema.Name, out var existing))
            return existing;

        string tableDirectory = TableDirectory(schema.Name);
        var kv = KvKeyspace.Open("table." + schema.Name, tableDirectory, _kvOptions);
        var store = new TableStore(schema, kv);
        _stores[schema.Name] = store;
        return store;
    }

    private string TableDirectory(string name) => Path.Combine(_rootDirectory, "rowstore", EncodeName(name));

    private void PersistCatalogLocked()
        => TableSchemaCodec.Save(SchemaPath, Catalog.Snapshot());

    private static string EncodeName(string name)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
