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
    /// 为已有关系表创建二级索引并持久化 schema。
    /// </summary>
    /// <param name="tableName">表名。</param>
    /// <param name="definition">索引声明。</param>
    /// <returns>新建的索引声明。</returns>
    public TableIndex CreateIndex(string tableName, TableIndexDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            ThrowIfDisposed();
            var current = Catalog.TryGet(tableName)
                ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
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
                ?? throw new InvalidOperationException("内部错误：索引创建后未能读取 schema。");
        }
    }

    /// <summary>
    /// 删除关系表二级索引声明。
    /// </summary>
    /// <param name="tableName">表名。</param>
    /// <param name="indexName">索引名。</param>
    /// <returns>索引存在并删除时返回 true。</returns>
    public bool DropIndex(string tableName, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (_sync)
        {
            ThrowIfDisposed();
            var current = Catalog.TryGet(tableName)
                ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
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

    public void AlterTableAddColumn(string tableName, string columnName, TableColumnType dataType, bool isNullable, object? defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        lock (_sync)
        {
            ThrowIfDisposed();
            var current = Catalog.TryGet(tableName)
                ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
            var updated = current.WithAddedColumn(columnName, dataType, isNullable);
            var store = OpenStoreLocked(current);
            ApplySchemaTransformLocked(current, updated, store, (_, row) =>
            {
                var values = new object?[updated.Columns.Count];
                for (var i = 0; i < row.Values.Count; i++)
                    values[i] = row.Values[i];
                values[^1] = defaultValue;
                return values;
            });
        }
    }

    public void AlterTableDropColumn(string tableName, string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        lock (_sync)
        {
            ThrowIfDisposed();
            var current = Catalog.TryGet(tableName)
                ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
            var dropped = current.TryGetColumn(columnName)
                ?? throw new InvalidOperationException($"table '{tableName}' 中不存在列 '{columnName}'。");
            var updated = current.WithoutColumn(columnName);
            var store = OpenStoreLocked(current);
            ApplySchemaTransformLocked(current, updated, store, (oldSchema, row) =>
            {
                var values = new object?[updated.Columns.Count];
                var target = 0;
                foreach (var column in oldSchema.Columns)
                {
                    if (column.Ordinal == dropped.Ordinal)
                        continue;
                    values[target++] = row.Values[column.Ordinal];
                }

                return values;
            });
        }
    }

    public void AlterTableRenameColumn(string tableName, string oldColumnName, string newColumnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldColumnName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newColumnName);
        lock (_sync)
        {
            ThrowIfDisposed();
            var current = Catalog.TryGet(tableName)
                ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
            var updated = current.WithRenamedColumn(oldColumnName, newColumnName);
            var store = OpenStoreLocked(current);
            ApplySchemaTransformLocked(current, updated, store, (_, row) => row.Values.ToArray());
        }
    }

    public void RenameTable(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        lock (_sync)
        {
            ThrowIfDisposed();
            var current = Catalog.TryGet(oldName)
                ?? throw new InvalidOperationException($"table '{oldName}' 不存在。");
            if (Catalog.TryGet(newName) is not null)
                throw new InvalidOperationException($"table '{newName}' 已存在。");

            var updated = current.WithName(newName);
            var oldDirectory = TableDirectory(oldName);
            var newDirectory = TableDirectory(newName);
            if (Directory.Exists(newDirectory))
                throw new InvalidOperationException($"table '{newName}' 的 rowstore 目录已存在。");

            TableStore? existingStore = null;
            if (_stores.Remove(oldName, out existingStore))
                existingStore.Dispose();

            Catalog.Remove(oldName);
            Catalog.Add(updated);
            try
            {
                if (Directory.Exists(oldDirectory))
                    Directory.Move(oldDirectory, newDirectory);
                PersistCatalogLocked();
                _ = OpenStoreLocked(updated);
            }
            catch
            {
                if (Directory.Exists(newDirectory) && !Directory.Exists(oldDirectory))
                    Directory.Move(newDirectory, oldDirectory);
                Catalog.Remove(newName);
                Catalog.Add(current);
                PersistCatalogLocked();
                _ = OpenStoreLocked(current);
                throw;
            }
        }
    }

    /// <summary>
    /// 从关系表主数据重建指定二级索引。
    /// </summary>
    /// <param name="tableName">表名。</param>
    /// <param name="indexName">索引名。</param>
    /// <returns>重建后的索引声明。</returns>
    public TableIndex RebuildIndex(string tableName, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (_sync)
        {
            ThrowIfDisposed();
            var schema = Catalog.TryGet(tableName)
                ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
            var index = schema.TryGetIndex(indexName)
                ?? throw new InvalidOperationException($"table '{tableName}' 中索引 '{indexName}' 不存在。");
            OpenStoreLocked(schema).ApplySchema(schema);
            return index;
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
    /// 为所有关系表 rowstore 创建 KV 快照，确保备份可独立恢复最近写入。
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

    private void ApplySchemaTransformLocked(
        TableSchema current,
        TableSchema updated,
        TableStore store,
        Func<TableSchema, TableRow, IReadOnlyList<object?>> transform)
    {
        var rollback = store.ApplySchemaTransform(updated, transform);
        Catalog.LoadOrReplace(updated);
        try
        {
            PersistCatalogLocked();
        }
        catch
        {
            rollback();
            Catalog.LoadOrReplace(current);
            throw;
        }
    }

    private static string EncodeName(string name)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
