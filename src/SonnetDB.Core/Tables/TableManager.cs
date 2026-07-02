using SonnetDB.Kv;
using SonnetDB.Sql.Ast;

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

    /// <summary>
    /// 删除关系表外键约束声明。
    /// </summary>
    /// <param name="tableName">表名。</param>
    /// <param name="constraintName">外键约束名。</param>
    /// <returns>外键存在并删除时返回 true。</returns>
    public bool DropForeignKey(string tableName, string constraintName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);
        lock (_sync)
        {
            ThrowIfDisposed();
            var current = Catalog.TryGet(tableName)
                ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
            var updated = current.WithoutForeignKey(constraintName);
            if (ReferenceEquals(updated, current))
                return false;

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
    /// 在同一数据库内原子提交多表 DML 轻事务。
    /// </summary>
    /// <param name="mutationsByTable">按表名分组的行变更。</param>
    /// <returns>实际影响的行数。</returns>
    public int ApplyTransaction(IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> mutationsByTable)
    {
        ArgumentNullException.ThrowIfNull(mutationsByTable);
        if (mutationsByTable.Count == 0)
            return 0;

        lock (_sync)
        {
            ThrowIfDisposed();
            // 在准备 batch 之前展开 ON DELETE CASCADE：递归把所有引用了被删父行的级联子行加入待删队列，
            // 这样后续 ValidatePrincipalDeletesLocked 看到子行已被该事务删除，不会误报外键违反。
            var expandedMutations = ExpandCascadeDeletesLocked(mutationsByTable);
            var prepared = new Dictionary<string, (TableStore Store, TableStore.PreparedTableBatch Batch)>(StringComparer.Ordinal);
            foreach (var (tableName, mutations) in expandedMutations)
            {
                var schema = Catalog.TryGet(tableName)
                    ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
                var store = OpenStoreLocked(schema);
                prepared.Add(tableName, (store, store.PrepareBatch(mutations)));
            }

            ValidateForeignKeysLocked(prepared);

            var applied = new List<(TableStore Store, TableStore.PreparedTableBatch Batch)>(prepared.Count);
            try
            {
                var affected = 0;
                foreach (var entry in prepared.Values)
                {
                    affected += entry.Store.ApplyPreparedBatch(entry.Batch);
                    applied.Add(entry);
                }

                return affected;
            }
            catch
            {
                for (var i = applied.Count - 1; i >= 0; i--)
                    applied[i].Store.RollbackPreparedBatch(applied[i].Batch);
                throw;
            }
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

    /// <summary>
    /// 在准备 batch 之前展开 ON DELETE CASCADE 子行删除：从用户提交的纯 DELETE 出发，
    /// 沿 CASCADE FK 链 BFS 把所有引用了被删父行 PK 的子行加为额外删除。
    /// 已经在事务里被用户显式删除的子行不会重复加入；其它修改类型的 mutation 原样保留。
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> ExpandCascadeDeletesLocked(
        IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> mutationsByTable)
    {
        // 先快速检查：catalog 里是否存在任何 CASCADE FK，否则零开销直接原路返回。
        bool anyCascade = false;
        foreach (var schema in Catalog.Snapshot())
        {
            foreach (var fk in schema.ForeignKeys)
            {
                if (fk.OnDelete == ForeignKeyAction.Cascade) { anyCascade = true; break; }
            }
            if (anyCascade) break;
        }
        if (!anyCascade)
            return mutationsByTable;

        // 构造可变工作集（保留 mutation 顺序），以及每表"已计划删除"的 PK 集合（HEX 编码）。
        var working = new Dictionary<string, List<TableRowMutation>>(StringComparer.Ordinal);
        var pendingDeletePks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var queue = new Queue<(string Table, IReadOnlyList<object?> Pk)>();

        foreach (var (tableName, mutations) in mutationsByTable)
        {
            var schema = Catalog.TryGet(tableName)
                ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
            var list = new List<TableRowMutation>(mutations);
            working[tableName] = list;
            var deletedSet = new HashSet<string>(StringComparer.Ordinal);
            pendingDeletePks[tableName] = deletedSet;
            foreach (var mutation in mutations)
            {
                if (mutation.NewValues is null && mutation.PrimaryKeyValues is not null)
                {
                    byte[] pkBytes = TableKeyCodec.EncodePrimaryKeyValues(schema, mutation.PrimaryKeyValues);
                    string pkText = Convert.ToHexString(pkBytes);
                    if (deletedSet.Add(pkText))
                        queue.Enqueue((tableName, mutation.PrimaryKeyValues));
                }
            }
        }

        while (queue.Count > 0)
        {
            var (parentTable, parentPk) = queue.Dequeue();
            var parentSchema = Catalog.TryGet(parentTable);
            if (parentSchema is null) continue;

            foreach (var childSchema in Catalog.Snapshot())
            {
                foreach (var fk in childSchema.ForeignKeys)
                {
                    if (fk.OnDelete != ForeignKeyAction.Cascade) continue;
                    if (!string.Equals(fk.PrincipalTable, parentTable, StringComparison.Ordinal)) continue;

                    // 防御性检查：与 ValidateForeignKeyRowLocked 保持一致——若 FK 引用列
                    // 顺序与父表 PRIMARY KEY 不一致，按 PK 顺序提取的 parentPk 与按 FK 顺序
                    // 提取的 childFkValues 会逐元素错位，导致级联静默漏删（数据完整性破坏）。
                    // 当前 schema 校验通常已经在 INSERT 路径上挡住这种 FK，但为防御 ALTER 类
                    // 漏校验路径，cascade 这一层显式拒绝执行——主动报错而不是错删/漏删。
                    if (!fk.PrincipalColumns.SequenceEqual(parentSchema.PrimaryKey, StringComparer.Ordinal))
                        throw new NotSupportedException(
                            $"外键 '{fk.Name}' ON DELETE CASCADE 要求引用列顺序与父表 PRIMARY KEY 完全一致。");

                    var childStore = OpenStoreLocked(childSchema);
                    foreach (var childRow in childStore.Scan())
                    {
                        var childFkValues = ExtractForeignKeyValues(childSchema, childRow, fk);
                        if (childFkValues is null) continue;
                        if (!ValuesEqual(childFkValues, parentPk)) continue;

                        var childPk = ExtractPrimaryKeyValues(childSchema, childRow);
                        byte[] childPkBytes = TableKeyCodec.EncodePrimaryKeyValues(childSchema, childPk);
                        string childPkText = Convert.ToHexString(childPkBytes);

                        if (!pendingDeletePks.TryGetValue(childSchema.Name, out var childSet))
                        {
                            childSet = new HashSet<string>(StringComparer.Ordinal);
                            pendingDeletePks[childSchema.Name] = childSet;
                        }
                        if (!childSet.Add(childPkText))
                            continue;

                        if (!working.TryGetValue(childSchema.Name, out var childList))
                        {
                            childList = new List<TableRowMutation>();
                            working[childSchema.Name] = childList;
                        }
                        childList.Add(new TableRowMutation(PrimaryKeyValues: childPk, NewValues: null));
                        queue.Enqueue((childSchema.Name, childPk));
                    }
                }
            }
        }

        var result = new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal);
        foreach (var (k, v) in working)
            result[k] = v;
        return result;
    }

    private void ValidateForeignKeysLocked(
        IReadOnlyDictionary<string, (TableStore Store, TableStore.PreparedTableBatch Batch)> prepared)
    {
        foreach (var (tableName, entry) in prepared)
        {
            var schema = entry.Batch.Schema;
            foreach (var row in entry.Batch.FinalRows)
            {
                foreach (var foreignKey in schema.ForeignKeys)
                    ValidateForeignKeyRowLocked(tableName, row, foreignKey, prepared);
            }
        }

        foreach (var principalSchema in Catalog.Snapshot())
        {
            if (!prepared.TryGetValue(principalSchema.Name, out var principalEntry))
                continue;
            if (principalEntry.Batch.DeletedRows.Count == 0)
                continue;

            foreach (var childSchema in Catalog.Snapshot())
            {
                foreach (var foreignKey in childSchema.ForeignKeys.Where(fk =>
                    string.Equals(fk.PrincipalTable, principalSchema.Name, StringComparison.Ordinal)))
                {
                    ValidatePrincipalDeletesLocked(principalEntry.Batch, childSchema, foreignKey, prepared);
                }
            }
        }
    }

    private void ValidateForeignKeyRowLocked(
        string tableName,
        TableRow row,
        TableForeignKey foreignKey,
        IReadOnlyDictionary<string, (TableStore Store, TableStore.PreparedTableBatch Batch)> prepared)
    {
        var childSchema = Catalog.TryGet(tableName)
            ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
        var principalSchema = Catalog.TryGet(foreignKey.PrincipalTable)
            ?? throw new InvalidOperationException($"外键 '{foreignKey.Name}' 引用的表 '{foreignKey.PrincipalTable}' 不存在。");
        if (!foreignKey.PrincipalColumns.SequenceEqual(principalSchema.PrimaryKey, StringComparer.Ordinal))
            throw new NotSupportedException($"外键 '{foreignKey.Name}' 第一版仅支持引用被引用表 PRIMARY KEY。");

        var keyValues = ExtractForeignKeyValues(childSchema, row, foreignKey);
        if (keyValues is null)
            return;

        if (!PrincipalExistsAfterTransactionLocked(principalSchema, keyValues, prepared))
        {
            throw new TableConstraintException(
                TableConstraintException.ForeignKeyViolation,
                tableName,
                foreignKey.Name,
                $"外键 '{foreignKey.Name}' 冲突：table '{tableName}' 引用了不存在的 '{foreignKey.PrincipalTable}' 主键。");
        }
    }

    private void ValidatePrincipalDeletesLocked(
        TableStore.PreparedTableBatch principalBatch,
        TableSchema childSchema,
        TableForeignKey foreignKey,
        IReadOnlyDictionary<string, (TableStore Store, TableStore.PreparedTableBatch Batch)> prepared)
    {
        var childStore = OpenStoreLocked(childSchema);
        foreach (var deleted in principalBatch.DeletedRows)
        {
            var deletedKeyValues = ExtractPrimaryKeyValues(principalBatch.Schema, deleted);
            foreach (var childRow in childStore.Scan())
            {
                if (prepared.TryGetValue(childSchema.Name, out var childPrepared)
                    && RowIsDeletedOrReplaced(childPrepared.Batch, childRow))
                {
                    continue;
                }

                var childKeyValues = ExtractForeignKeyValues(childSchema, childRow, foreignKey);
                if (childKeyValues is not null && ValuesEqual(childKeyValues, deletedKeyValues))
                    throw ForeignKeyDeleteViolation(childSchema, foreignKey);
            }

            if (prepared.TryGetValue(childSchema.Name, out var preparedChild))
            {
                foreach (var childRow in preparedChild.Batch.FinalRows)
                {
                    var childKeyValues = ExtractForeignKeyValues(childSchema, childRow, foreignKey);
                    if (childKeyValues is not null && ValuesEqual(childKeyValues, deletedKeyValues))
                        throw ForeignKeyDeleteViolation(childSchema, foreignKey);
                }
            }
        }
    }

    private bool PrincipalExistsAfterTransactionLocked(
        TableSchema principalSchema,
        IReadOnlyList<object?> keyValues,
        IReadOnlyDictionary<string, (TableStore Store, TableStore.PreparedTableBatch Batch)> prepared)
    {
        if (prepared.TryGetValue(principalSchema.Name, out var principalPrepared))
        {
            if (principalPrepared.Batch.FinalRows.Any(row => ValuesEqual(ExtractPrimaryKeyValues(principalSchema, row), keyValues)))
                return true;
            if (principalPrepared.Batch.DeletedRows.Any(row => ValuesEqual(ExtractPrimaryKeyValues(principalSchema, row), keyValues)))
                return false;
        }

        return OpenStoreLocked(principalSchema).GetByPrimaryKey(keyValues) is not null;
    }

    private static bool RowIsDeletedOrReplaced(TableStore.PreparedTableBatch batch, TableRow row)
        => batch.DeletedRows.Any(deleted => deleted.PrimaryKey.Span.SequenceEqual(row.PrimaryKey.Span))
           || batch.FinalRows.Any(updated => updated.PrimaryKey.Span.SequenceEqual(row.PrimaryKey.Span));

    private static IReadOnlyList<object?>? ExtractForeignKeyValues(
        TableSchema childSchema,
        TableRow row,
        TableForeignKey foreignKey)
    {
        var values = new object?[foreignKey.Columns.Count];
        for (var i = 0; i < foreignKey.Columns.Count; i++)
        {
            var column = childSchema.TryGetColumn(foreignKey.Columns[i])
                ?? throw new InvalidOperationException($"外键 '{foreignKey.Name}' 引用了未知列 '{foreignKey.Columns[i]}'。");
            values[i] = row.Values[column.Ordinal];
            if (values[i] is null)
                return null;
        }

        return values;
    }

    private static IReadOnlyList<object?> ExtractPrimaryKeyValues(TableSchema schema, TableRow row)
    {
        var values = new object?[schema.PrimaryKey.Count];
        for (var i = 0; i < schema.PrimaryKey.Count; i++)
        {
            var column = schema.TryGetColumn(schema.PrimaryKey[i])
                ?? throw new InvalidOperationException($"PRIMARY KEY 引用了未知列 '{schema.PrimaryKey[i]}'。");
            values[i] = row.Values[column.Ordinal];
        }

        return values;
    }

    private static bool ValuesEqual(IReadOnlyList<object?> left, IReadOnlyList<object?> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!Equals(left[i], right[i]))
                return false;
        }

        return true;
    }

    private static TableConstraintException ForeignKeyDeleteViolation(TableSchema childSchema, TableForeignKey foreignKey)
        => new(
            TableConstraintException.ForeignKeyViolation,
            childSchema.Name,
            foreignKey.Name,
            $"外键 '{foreignKey.Name}' 冲突：不能删除仍被 table '{childSchema.Name}' 引用的 '{foreignKey.PrincipalTable}' 主键。");

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
