using SonnetDB.Diagnostics;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

/// <summary>
/// 管理同一数据库目录下的关系表 schema 与 rowstore。
/// </summary>
public sealed class TableManager : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<TableRow>> _emptyFinalRows =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<TableRow>>(
            new Dictionary<string, IReadOnlyList<TableRow>>(StringComparer.Ordinal));

    private readonly object _sync = new();
    private readonly object _schemaSync;
    private readonly string _rootDirectory;
    private readonly KvOptions _kvOptions;
    private readonly Action<string, string>? _nameAvailabilityGuard;
    private readonly Action<string, string>? _schemaMutationGuard;
    private readonly Dictionary<string, TableStore> _stores = new(StringComparer.Ordinal);
    private readonly TableStatisticsRefreshBudget _statisticsRefreshBudget = new();
    private bool _disposed;
    private Exception? _transactionFailure;

    // M39 #333 crash/commit evidence hook.  It is intentionally internal and
    // unset in production; tests use it to stop between independent keyspace
    // commits without changing the public transaction contract.
    internal Action<string>? ApplyTransactionAfterTableTestHook { get; set; }
    internal Action? ApplyTransactionBeforeCompleteTestHook { get; set; }
    internal Action? ApplyTransactionAfterCompleteTestHook { get; set; }

    /// <summary>仅供并发测试确认 schema 变更已取得数据库级 schema 锁。</summary>
    internal Action<string>? SchemaMutationLockAcquiredTestHook { get; set; }

    /// <summary>仅供测试在候选表目录落盘后、内存快照发布前建立确定性同步点。</summary>
    internal Action? AfterCatalogPersistedBeforePublishTestHook { get; set; }

    /// <summary>仅供测试确认多张关系表的冷开阶段能够并行进入。</summary>
    internal Action<string>? WarmUpBeforeOpenTestHook { get; set; }

    /// <summary>
    /// 初始化表管理器。
    /// </summary>
    /// <param name="rootDirectory">tables 根目录。</param>
    /// <param name="kvOptions">底层 KV 选项。</param>
    public TableManager(string rootDirectory, KvOptions kvOptions)
        : this(
            rootDirectory,
            kvOptions,
            nameAvailabilityGuard: null,
            schemaMutationGuard: null,
            synchronizationRoot: new object())
    {
    }

    /// <summary>
    /// 使用数据库级同步根初始化表管理器，使关系表 DDL 可与其它 schema catalog 和备份串行化。
    /// </summary>
    /// <param name="rootDirectory">tables 根目录。</param>
    /// <param name="kvOptions">底层 KV 选项。</param>
    /// <param name="nameAvailabilityGuard">跨模型名称占用检查。</param>
    /// <param name="schemaMutationGuard">关系表依赖检查。</param>
    /// <param name="synchronizationRoot">数据库级 schema 与备份同步根。</param>
    internal TableManager(
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

        Catalog = new TableCatalog();
        foreach (var schema in TableSchemaCodec.Load(SchemaPath))
            Catalog.LoadOrReplace(schema);
        Catalog.MutationGuard = EnsureManagedCatalogMutation;
        try
        {
            foreach (var undo in TableTransactionJournal.ReadPending(TransactionJournalPath))
            {
                var schema = Catalog.TryGet(undo.TableName)
                    ?? throw new InvalidDataException($"事务恢复引用了不存在的 table '{undo.TableName}'。");
                OpenStoreLocked(schema).RestoreTransactionUndo(undo);
            }
            if (File.Exists(TransactionJournalPath))
                TableTransactionJournal.Complete(TransactionJournalPath);
        }
        catch
        {
            foreach (var store in _stores.Values) store.Dispose();
            _stores.Clear();
            throw;
        }
    }

    /// <summary>关系表 catalog。</summary>
    public TableCatalog Catalog { get; }

    /// <summary>表 schema 文件路径。</summary>
    public string SchemaPath => Path.Combine(_rootDirectory, TableSchemaCodec.FileName);
    private string TransactionJournalPath => Path.Combine(_rootDirectory, TableTransactionJournal.FileName);

    /// <summary>
    /// 创建关系表并持久化 schema。
    /// </summary>
    /// <param name="schema">表 schema。</param>
    public void Create(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_schemaSync)
            lock (_sync)
            {
                _nameAvailabilityGuard?.Invoke(schema.Name, "table");
                ThrowIfDisposed();
                if (Catalog.TryGet(schema.Name) is not null)
                    throw new InvalidOperationException($"table '{schema.Name}' 已存在。");

                IReadOnlyList<TableSchema> previousSchemas = Catalog.Snapshot();
                TableStore? openedStore = null;
                var catalogPersisted = false;
                try
                {
                    // 先打开 rowstore，再把包含新表的候选 schema 文件落盘；两步都成功后才发布无锁读快照。
                    // 失败期间 SHOW、查询和 Modbus 绑定均不会观察到尚未提交的关系表。
                    openedStore = OpenStoreLocked(schema);
                    var candidateSchemas = previousSchemas.ToList();
                    candidateSchemas.Add(schema);
                    TableSchemaCodec.Save(SchemaPath, candidateSchemas);
                    catalogPersisted = true;
                    AfterCatalogPersistedBeforePublishTestHook?.Invoke();
                    Catalog.Add(schema);
                }
                catch (Exception createException)
                {
                    var rollbackErrors = new List<Exception>();

                    // 发布失败时同时恢复内存目录和已替换的 schema 文件，避免 API 报错后重启却出现新表。
                    try
                    {
                        _ = Catalog.Remove(schema.Name);
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackErrors.Add(rollbackException);
                    }

                    if (catalogPersisted)
                    {
                        try
                        {
                            TableSchemaCodec.Save(SchemaPath, previousSchemas, ".rollback.tmp");
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackErrors.Add(rollbackException);
                        }
                    }

                    // 创建失败时，本次打开的 store 必须移出管理器并释放文件租约，
                    // 使同一进程和重启后的同名 CREATE 都可以立即重试。
                    if (openedStore is not null
                        && _stores.Remove(schema.Name, out TableStore? store))
                    {
                        try
                        {
                            store.Dispose();
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackErrors.Add(rollbackException);
                        }
                    }

                    if (rollbackErrors.Count != 0)
                    {
                        rollbackErrors.Insert(0, createException);
                        throw new InvalidOperationException(
                            $"table '{schema.Name}' 创建失败，且目录或 rowstore 回滚失败。",
                            new AggregateException(rollbackErrors));
                    }

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
        lock (_schemaSync)
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
        lock (_schemaSync)
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
        lock (_schemaSync)
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(tableName, "ALTER TABLE");
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

    /// <summary>
    /// 为已有关系表添加外键约束声明，并验证当前存量数据。
    /// </summary>
    /// <param name="tableName">目标关系表名称。</param>
    /// <param name="definition">外键声明。</param>
    /// <returns>添加后的外键声明。</returns>
    public TableForeignKey AddForeignKey(string tableName, TableForeignKeyDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(definition);
        lock (_schemaSync)
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(tableName, "ALTER TABLE");
                ThrowIfDisposed();
                var current = Catalog.TryGet(tableName)
                    ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
                var updated = current.WithForeignKey(definition);
                var foreignKey = string.IsNullOrWhiteSpace(definition.Name)
                    ? updated.ForeignKeys[^1]
                    : updated.TryGetForeignKey(definition.Name)
                        ?? throw new InvalidOperationException("内部错误：外键创建后未能读取 schema。");

                ValidateAddedForeignKeyLocked(updated, foreignKey);

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

                return foreignKey;
            }
    }

    /// <summary>
    /// 删除关系表检查约束声明。
    /// </summary>
    /// <param name="tableName">表名。</param>
    /// <param name="constraintName">检查约束名。</param>
    /// <returns>约束存在并删除时返回 <c>true</c>。</returns>
    public bool DropCheckConstraint(string tableName, string constraintName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);
        lock (_schemaSync)
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(tableName, "ALTER TABLE");
                ThrowIfDisposed();
                var current = Catalog.TryGet(tableName)
                    ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
                var updated = current.WithoutCheckConstraint(constraintName);
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

    /// <summary>
    /// 为已有关系表添加检查约束，并验证当前存量数据。
    /// </summary>
    /// <param name="tableName">目标关系表名称。</param>
    /// <param name="definition">检查约束声明。</param>
    /// <returns>添加后的检查约束。</returns>
    public TableCheckConstraint AddCheckConstraint(
        string tableName,
        TableCheckConstraintDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(definition);
        lock (_schemaSync)
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(tableName, "ALTER TABLE");
                ThrowIfDisposed();
                var current = Catalog.TryGet(tableName)
                    ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
                var updated = current.WithCheckConstraint(definition);
                var checkConstraint = string.IsNullOrWhiteSpace(definition.Name)
                    ? updated.CheckConstraints[^1]
                    : updated.TryGetCheckConstraint(definition.Name)
                        ?? throw new InvalidOperationException("内部错误：检查约束创建后未能读取 schema。");

                ValidateAddedCheckConstraintLocked(updated, checkConstraint);

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

                return checkConstraint;
            }
    }

    /// <summary>向关系表追加一列，并用指定默认值回填已有行。</summary>
    /// <param name="tableName">目标表名。</param>
    /// <param name="columnName">新增列名。</param>
    /// <param name="dataType">新增列类型。</param>
    /// <param name="isNullable">新增列是否允许空值。</param>
    /// <param name="defaultValue">已有行的回填值。</param>
    public void AlterTableAddColumn(string tableName, string columnName, TableColumnType dataType, bool isNullable, object? defaultValue)
        => AlterTableAddColumn(
            tableName,
            columnName,
            dataType,
            isNullable,
            defaultValue,
            defaultExpressionSql: null);

    /// <summary>向关系表追加一列，并保留可持久化的默认表达式文本。</summary>
    internal void AlterTableAddColumn(
        string tableName,
        string columnName,
        TableColumnType dataType,
        bool isNullable,
        object? defaultValue,
        string? defaultExpressionSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        lock (_schemaSync)
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(tableName, "ALTER TABLE");
                ThrowIfDisposed();
                var current = Catalog.TryGet(tableName)
                    ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
                var updated = current.WithAddedColumn(columnName, dataType, isNullable, defaultExpressionSql);
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

    /// <summary>修改关系表列定义，并在需要时转换已有行的列值。</summary>
    internal void AlterTableAlterColumn(
        string tableName,
        string columnName,
        TableColumnType dataType,
        bool isNullable,
        string? defaultExpressionSql,
        Func<object?, object?>? convertValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        lock (_schemaSync)
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(tableName, "ALTER TABLE");
                ThrowIfDisposed();
                var current = Catalog.TryGet(tableName)
                    ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
                var currentColumn = current.TryGetColumn(columnName)
                    ?? throw new InvalidOperationException($"table '{tableName}' 中不存在列 '{columnName}'。");
                var changesType = currentColumn.DataType != dataType;
                if (changesType)
                {
                    ArgumentNullException.ThrowIfNull(convertValue);
                    EnsureColumnTypeIsNotReferencedByForeignKeyLocked(current, currentColumn);
                }

                var updated = current.WithAlteredColumn(
                    columnName,
                    dataType,
                    isNullable,
                    defaultExpressionSql);
                var store = OpenStoreLocked(current);
                if (!changesType)
                {
                    if (currentColumn.IsNullable && !isNullable)
                        store.ValidateExistingRows(updated);
                    ApplyMetadataSchemaLocked(current, updated, store);
                    return;
                }

                ApplySchemaTransformLocked(
                    current,
                    updated,
                    store,
                    (_, row) =>
                    {
                        var values = row.Values.ToArray();
                        if (changesType && values[currentColumn.Ordinal] is not null)
                            values[currentColumn.Ordinal] = convertValue!(values[currentColumn.Ordinal]);
                        return values;
                    },
                    ValidateAlteredRowCheckConstraints);
            }
    }

    /// <summary>删除关系表列，并重写已有行以移除对应值。</summary>
    /// <param name="tableName">目标表名。</param>
    /// <param name="columnName">待删除列名。</param>
    public void AlterTableDropColumn(string tableName, string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        lock (_schemaSync)
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(tableName, "ALTER TABLE");
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

    /// <summary>重命名关系表列并同步更新持久化 schema。</summary>
    /// <param name="tableName">目标表名。</param>
    /// <param name="oldColumnName">原列名。</param>
    /// <param name="newColumnName">新列名。</param>
    public void AlterTableRenameColumn(string tableName, string oldColumnName, string newColumnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldColumnName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newColumnName);
        lock (_schemaSync)
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(tableName, "ALTER TABLE");
                ThrowIfDisposed();
                var current = Catalog.TryGet(tableName)
                    ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
                var updated = current.WithRenamedColumn(oldColumnName, newColumnName);
                var store = OpenStoreLocked(current);
                ApplyMetadataSchemaLocked(current, updated, store);
            }
    }

    /// <summary>重命名关系表及其 rowstore 目录。</summary>
    /// <param name="oldName">原表名。</param>
    /// <param name="newName">新表名。</param>
    public void RenameTable(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        lock (_schemaSync)
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(oldName, "ALTER TABLE RENAME");
                _nameAvailabilityGuard?.Invoke(newName, "table");
                ThrowIfDisposed();
                var current = Catalog.TryGet(oldName)
                    ?? throw new InvalidOperationException($"table '{oldName}' 不存在。");
                if (Catalog.TryGet(newName) is not null)
                    throw new InvalidOperationException($"table '{newName}' 已存在。");
                EnsureTableIsNotReferencedByForeignKeyLocked(oldName, "重命名");

                var updated = current.WithName(newName);
                var oldDirectory = TableDirectory(oldName);
                var newDirectory = TableDirectory(newName);
                if (Directory.Exists(newDirectory))
                    throw new InvalidOperationException($"table '{newName}' 的 rowstore 目录已存在。");

                _stores.TryGetValue(oldName, out TableStore? existingStore);
                using IDisposable? statisticsPause = existingStore?.PauseAutomaticStatisticsRefreshForRename();
                existingStore?.Dispose();
                _stores.Remove(oldName);

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
        lock (_schemaSync)
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
        lock (_schemaSync)
        {
            SchemaMutationLockAcquiredTestHook?.Invoke("DROP TABLE");
            lock (_sync)
            {
                _schemaMutationGuard?.Invoke(name, "DROP TABLE");
                ThrowIfDisposed();
                if (Catalog.TryGet(name) is null)
                    return false;
                EnsureTableIsNotReferencedByForeignKeyLocked(name, "删除");
                _ = Catalog.Remove(name);

                if (_stores.Remove(name, out var store))
                    store.Dispose();

                PersistCatalogLocked();
                string tableDirectory = TableDirectory(name);
                if (Directory.Exists(tableDirectory))
                    Directory.Delete(tableDirectory, recursive: true);

                return true;
            }
        }
    }

    /// <summary>
    /// 在同一数据库内提交多表 DML 轻事务；通过同步恢复日志撤销未完成的跨表提交。
    /// </summary>
    /// <remarks>
    /// 多表事务先同步 before-images，再写入并同步各表 WAL，最后同步完成标记。
    /// 重启时，在开放关系表访问前撤销没有完整完成标记的事务；单表仍复用原子 KV batch。
    /// </remarks>
    /// <param name="mutationsByTable">按表名分组的行变更。</param>
    /// <returns>实际影响的行数。</returns>
    public int ApplyTransaction(IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> mutationsByTable)
        => ApplyTransaction(mutationsByTable, metrics: null);

    /// <summary>
    /// 在表管理器互斥锁内执行读改写操作，供需要把候选扫描与提交组成单个原子步骤的 SQL DML 使用。
    /// </summary>
    /// <typeparam name="TResult">操作结果类型。</typeparam>
    /// <param name="action">需要在锁内执行的操作。</param>
    /// <returns>操作返回值。</returns>
    internal TResult ExecuteLocked<TResult>(Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        long lockWait = SonnetDbMeter.StartLockWaitTiming();
        lock (_sync)
        {
            SonnetDbMeter.RecordTableManagerLockWait(lockWait);
            ThrowIfDisposed();
            return action();
        }
    }

    /// <summary>
    /// 在同一个 TableManager 临界区内取得多张表的稳定读快照。
    /// SQL 多表提交无法穿过捕获窗口，返回后读取仅持有各 rowstore 的不可变 KV lease。
    /// </summary>
    internal IReadOnlyDictionary<string, TableReadBinding> AcquireReadSnapshots(
        IEnumerable<string> tableNames)
    {
        ArgumentNullException.ThrowIfNull(tableNames);
        string[] names = tableNames
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (names.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("关系读快照的表名不能为空。", nameof(tableNames));

        long lockWait = SonnetDbMeter.StartLockWaitTiming();
        lock (_sync)
        {
            SonnetDbMeter.RecordTableManagerLockWait(lockWait);
            ThrowIfDisposed();
            var stores = new List<(string Name, TableStore Store)>(names.Length);
            foreach (string name in names)
            {
                TableSchema schema = Catalog.TryGet(name)
                    ?? throw new InvalidOperationException($"table '{name}' 不存在。");
                stores.Add((name, OpenStoreLocked(schema)));
            }

            var bindings = new Dictionary<string, TableReadBinding>(names.Length, StringComparer.Ordinal);
            int locksHeld = 0;
            try
            {
                foreach ((string _, TableStore store) in stores)
                {
                    Monitor.Enter(store.SynchronizationRoot);
                    locksHeld++;
                }
                foreach ((string name, TableStore store) in stores)
                    bindings.Add(name, new TableReadBinding(store, store.AcquireTableReadSnapshot()));
                return bindings;
            }
            catch
            {
                foreach (TableReadBinding binding in bindings.Values)
                    binding.Snapshot.Dispose();
                throw;
            }
            finally
            {
                for (int index = locksHeld - 1; index >= 0; index--)
                    Monitor.Exit(stores[index].Store.SynchronizationRoot);
            }
        }
    }

    /// <summary>
    /// 通过 table/keyspace generation 快速清空整表。存在入站外键时拒绝，避免绕过引用约束或级联语义。
    /// </summary>
    /// <param name="name">关系表名称。</param>
    /// <returns>切换前的关系行数。</returns>
    public int Truncate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            ThrowIfDisposed();
            var schema = Catalog.TryGet(name)
                ?? throw new InvalidOperationException($"table '{name}' 不存在。");
            if (HasInboundForeignKeyLocked(name))
            {
                throw new InvalidOperationException(
                    $"table '{name}' 被外键引用，TRUNCATE 不允许绕过引用约束；请使用 DELETE 触发既有级联/限制语义。");
            }

            return OpenStoreLocked(schema).Truncate().Rows;
        }
    }

    /// <summary>无入站外键时尝试 generation 快速清表，供 DELETE WHERE TRUE 使用。</summary>
    internal bool TryTruncateFast(string name, out int rows)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var schema = Catalog.TryGet(name)
                ?? throw new InvalidOperationException($"table '{name}' 不存在。");
            if (HasInboundForeignKeyLocked(name))
            {
                rows = 0;
                return false;
            }

            rows = OpenStoreLocked(schema).Truncate().Rows;
            return true;
        }
    }

    internal int CleanupPendingFiles(int maxFilesPerTable)
        => CleanupPendingFilesWithResult(maxFilesPerTable).ProcessedEntries;

    internal KvCleanupRoundResult CleanupPendingFilesWithResult(int maxFilesPerTable)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            DiscoverCleanupTablesLocked();

            KvCleanupRoundResult result = KvCleanupRoundResult.Empty;
            foreach (var (name, store) in _stores)
            {
                if (File.Exists(Path.Combine(TableDirectory(name), KvCleanupManifest.FileName)))
                    result += store.CleanupPendingFilesWithResult(maxFilesPerTable);
            }
            return result;
        }
    }

    internal KvCleanupRoundResult GetCleanupStatusOpened()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            DiscoverCleanupTablesLocked();
            KvCleanupRoundResult result = KvCleanupRoundResult.Empty;
            foreach (var (name, store) in _stores)
            {
                if (!File.Exists(Path.Combine(TableDirectory(name), KvCleanupManifest.FileName)))
                    continue;
                KvCleanupStatus status = store.GetCleanupStatus();
                result += new KvCleanupRoundResult(0, 0, 0, status.PendingFiles, status.PendingBytes);
            }
            return result;
        }
    }

    /// <summary>
    /// 在同一数据库内提交多表 DML，并记录级联删除查找路径的内部性能计数。
    /// </summary>
    /// <param name="mutationsByTable">按表名分组的行变更。</param>
    /// <param name="metrics">级联删除展开阶段的执行计数。</param>
    /// <returns>实际影响的行数。</returns>
    internal int ApplyTransaction(
        IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> mutationsByTable,
        CascadeDeleteExecutionMetrics? metrics)
        => ApplyTransactionCore(mutationsByTable, metrics, includeFinalRows: false, out _);

    /// <summary>提交多表 DML，并返回准备阶段生成且实际提交的最终行快照。</summary>
    internal int ApplyTransaction(
        IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> mutationsByTable,
        out IReadOnlyDictionary<string, IReadOnlyList<TableRow>> finalRows)
        => ApplyTransactionCore(mutationsByTable, metrics: null, includeFinalRows: true, out finalRows);

    private int ApplyTransactionCore(
        IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> mutationsByTable,
        CascadeDeleteExecutionMetrics? metrics,
        bool includeFinalRows,
        out IReadOnlyDictionary<string, IReadOnlyList<TableRow>> finalRows)
    {
        ArgumentNullException.ThrowIfNull(mutationsByTable);
        if (mutationsByTable.Count == 0)
        {
            finalRows = _emptyFinalRows;
            return 0;
        }

        long lockWait = SonnetDbMeter.StartLockWaitTiming();
        lock (_sync)
        {
            SonnetDbMeter.RecordTableManagerLockWait(lockWait);
            ThrowIfDisposed();
            // 在准备 batch 之前展开 ON DELETE CASCADE：递归把所有引用了被删父行的级联子行加入待删队列，
            // 这样后续 ValidatePrincipalDeletesLocked 看到子行已被该事务删除，不会误报外键违反。
            var expandedMutations = ExpandCascadeDeletesLocked(mutationsByTable, metrics);
            var prepared = new Dictionary<string, (TableStore Store, TableStore.PreparedTableBatch Batch)>(StringComparer.Ordinal);
            var lockedStores = new List<TableStore>(expandedMutations.Count);
            try
            {
                foreach (string tableName in expandedMutations.Keys.Order(StringComparer.Ordinal))
                {
                    var schema = Catalog.TryGet(tableName)
                        ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
                    var store = OpenStoreLocked(schema);
                    Monitor.Enter(store.SynchronizationRoot);
                    lockedStores.Add(store);
                }
                foreach (var (tableName, mutations) in expandedMutations)
                {
                    var store = _stores[tableName];
                    prepared.Add(tableName, (store, store.PrepareBatch(mutations)));
                }
                ValidateCheckConstraintsLocked(prepared);
                ValidateForeignKeysLocked(prepared);

                bool journaled = prepared.Values.Count(static entry => entry.Batch.AffectedRows > 0) > 1;
                var undoTables = journaled
                    ? prepared.Values.Select(static entry => entry.Store.CaptureTransactionUndo(entry.Batch)).ToArray()
                    : [];
                if (journaled)
                    TableTransactionJournal.Prepare(TransactionJournalPath, undoTables);
                var applied = new List<(TableStore Store, TableStore.PreparedTableBatch Batch)>(prepared.Count);
                var affected = 0;
                bool completing = false;
                try
                {
                    foreach (var (tableName, entry) in prepared)
                    {
                        affected += entry.Store.ApplyPreparedBatch(entry.Batch);
                        applied.Add(entry);
                        ApplyTransactionAfterTableTestHook?.Invoke(tableName);
                    }
                    if (journaled)
                    {
                        foreach (var entry in prepared.Values) entry.Store.SyncTransactionWal();
                        ApplyTransactionBeforeCompleteTestHook?.Invoke();
                        completing = true;
                        TableTransactionJournal.Complete(TransactionJournalPath);
                        ApplyTransactionAfterCompleteTestHook?.Invoke();
                    }
                }
                catch (Exception commitFailure)
                {
                    if (completing)
                    {
                        // 完成标记的 fsync 失败可能已持久化提交决定，不能再写相反的补偿。
                        _transactionFailure = commitFailure;
                        foreach (var store in _stores.Values) store.InvalidateTransaction(commitFailure);
                        throw new TableTransactionRecoveryException("关系事务提交结果未知，必须关闭并重新打开数据库完成恢复。", commitFailure);
                    }
                    try
                    {
                        if (journaled)
                        {
                            foreach (var undo in undoTables)
                                prepared[undo.TableName].Store.RestoreTransactionUndo(undo);
                            TableTransactionJournal.Complete(TransactionJournalPath);
                        }
                        else
                        {
                            for (int i = applied.Count - 1; i >= 0; i--)
                                applied[i].Store.RollbackPreparedBatch(applied[i].Batch);
                        }
                    }
                    catch (Exception rollbackFailure)
                    {
                        _transactionFailure = rollbackFailure;
                        foreach (var store in _stores.Values) store.InvalidateTransaction(rollbackFailure);
                        throw new TableTransactionRecoveryException("关系事务回滚失败，必须关闭并重新打开数据库完成恢复。",
                            new AggregateException(commitFailure, rollbackFailure));
                    }
                    throw;
                }

                finalRows = includeFinalRows
                    ? prepared.ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value.Batch.FinalRows,
                        StringComparer.Ordinal)
                    : _emptyFinalRows;
                return affected;
            }
            finally
            {
                for (int index = lockedStores.Count - 1; index >= 0; index--)
                    Monitor.Exit(lockedStores[index].SynchronizationRoot);
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
    /// 打开 catalog 中已有的关系表，使索引恢复、行数统计和状态加载在业务流量进入前完成。
    /// </summary>
    /// <param name="cancellationToken">在两张表之间停止后续预热的取消令牌。</param>
    /// <param name="maxDegreeOfParallelism">不同关系表允许同时执行冷开的最大数量。</param>
    /// <returns>本轮确认已打开的关系表名称。</returns>
    public IReadOnlyList<string> WarmUpAll(
        CancellationToken cancellationToken = default,
        int maxDegreeOfParallelism = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDegreeOfParallelism);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                IReadOnlyList<TableSchema> schemas = Catalog.Snapshot();
                var warmedTables = new List<string>(schemas.Count);
                var unopenedSchemas = new List<TableSchema>(schemas.Count);
                foreach (TableSchema catalogSchema in schemas)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TableSchema? current = Catalog.TryGet(catalogSchema.Name);
                    if (current is null)
                        continue;
                    warmedTables.Add(current.Name);
                    if (_stores.ContainsKey(current.Name))
                        continue;
                    unopenedSchemas.Add(current);
                }

                if (maxDegreeOfParallelism == 1 || unopenedSchemas.Count <= 1)
                {
                    foreach (TableSchema schema in unopenedSchemas)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        WarmUpBeforeOpenTestHook?.Invoke(schema.Name);
                        _ = OpenStoreLocked(schema);
                    }
                    return warmedTables;
                }

                // 管理器锁在整个阶段阻止查询和 DDL 观察半完成状态；各表目录相互独立，可安全并行恢复。
                var openedStores = new System.Collections.Concurrent.ConcurrentDictionary<string, TableStore>(
                    StringComparer.Ordinal);
                try
                {
                    Parallel.ForEach(
                        unopenedSchemas,
                        new ParallelOptions
                        {
                            CancellationToken = cancellationToken,
                            MaxDegreeOfParallelism = maxDegreeOfParallelism,
                        },
                        schema =>
                        {
                            WarmUpBeforeOpenTestHook?.Invoke(schema.Name);
                            TableStore store = CreateStore(schema);
                            if (!openedStores.TryAdd(schema.Name, store))
                            {
                                store.Dispose();
                                throw new InvalidOperationException($"table '{schema.Name}' 被重复安排启动预热。");
                            }
                        });

                    foreach (TableSchema schema in unopenedSchemas)
                        _stores.Add(schema.Name, openedStores[schema.Name]);
                    return warmedTables;
                }
                catch
                {
                    foreach (TableStore store in openedStores.Values)
                    {
                        try { store.Dispose(); } catch { }
                    }
                    throw;
                }
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

    internal TResult ExecuteConsistentBackup<TResult>(Func<TResult> action)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var locked = new List<TableStore>();
            try
            {
                foreach (var schema in Catalog.Snapshot().OrderBy(static schema => schema.Name, StringComparer.Ordinal))
                {
                    var store = OpenStoreLocked(schema);
                    Monitor.Enter(store.SynchronizationRoot);
                    locked.Add(store);
                }
                return action();
            }
            finally
            {
                for (int index = locked.Count - 1; index >= 0; index--)
                    Monitor.Exit(locked[index].SynchronizationRoot);
            }
        }
    }

    /// <summary>
    /// 返回已打开关系表 active WAL 的逻辑长度总和，供 M39 基准读取未刷出的 WAL。
    /// </summary>
    internal long ActiveWalBytesForEvidence
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                long total = 0;
                foreach (TableStore store in _stores.Values)
                    total = checked(total + store.ActiveWalLengthForEvidence);
                return total;
            }
        }
    }

    /// <summary>返回当前已完成冷开的关系表数量，供恢复与启动回归测试观测。</summary>
    internal int OpenedStoreCountForEvidence
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return _stores.Count;
            }
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

        TableStore store = CreateStore(schema);
        _stores[schema.Name] = store;
        return store;
    }

    /// <summary>打开单张关系表的独立 KV 存储；失败时释放已经取得的目录租约。</summary>
    private TableStore CreateStore(TableSchema schema)
    {
        string tableDirectory = TableDirectory(schema.Name);
        var kv = KvKeyspace.Open("table." + schema.Name, tableDirectory, _kvOptions);
        try
        {
            return new TableStore(schema, kv, _statisticsRefreshBudget);
        }
        catch
        {
            try
            {
                kv.Dispose();
            }
            catch
            {
                // Preserve the table-open failure; a later open will retry WAL recovery.
            }
            throw;
        }
    }

    private string TableDirectory(string name) => Path.Combine(_rootDirectory, "rowstore", EncodeName(name));

    private void PersistCatalogLocked()
        => TableSchemaCodec.Save(SchemaPath, Catalog.Snapshot());

    /// <summary>
    /// 在准备 batch 之前展开父行删除对子行的引用动作：从用户提交的纯 DELETE 出发，
    /// 沿 FK 链把引用了被删父行 PK 的子行按 ON DELETE 动作处理——CASCADE 追加删除（并继续沿链传播），
    /// SET NULL 追加"把子行外键列置空"的更新（不再传播）。
    /// 已经在事务里被用户显式修改或已计划删除的子行不会被重复加入；其它修改类型的 mutation 原样保留。
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> ExpandCascadeDeletesLocked(
        IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> mutationsByTable,
        CascadeDeleteExecutionMetrics? metrics)
    {
        // 单批只读取一次 catalog，并预先建立 principal table -> referencing FK 的反向关系。
        // 后续 BFS 不再为每个父键重复复制 catalog 或遍历无关 schema。
        IReadOnlyList<TableSchema> schemas = Catalog.Snapshot();
        if (metrics is not null)
            metrics.CatalogSnapshotCount++;

        var schemasByName = new Dictionary<string, TableSchema>(schemas.Count, StringComparer.Ordinal);
        foreach (var schema in schemas)
            schemasByName.Add(schema.Name, schema);

        var lookupsByPrincipal = new Dictionary<string, List<CascadeForeignKeyLookup>>(StringComparer.Ordinal);
        foreach (var childSchema in schemas)
        {
            foreach (var fk in childSchema.ForeignKeys)
            {
                if (fk.OnDelete is not (ForeignKeyAction.Cascade or ForeignKeyAction.SetNull))
                    continue;
                if (!schemasByName.TryGetValue(fk.PrincipalTable, out var principalSchema))
                    continue;
                if (!fk.PrincipalColumns.SequenceEqual(principalSchema.PrimaryKey, StringComparer.Ordinal))
                {
                    throw new NotSupportedException(
                        $"外键 '{fk.Name}' ON DELETE {(fk.OnDelete == ForeignKeyAction.Cascade ? "CASCADE" : "SET NULL")} 要求引用列顺序与父表 PRIMARY KEY 完全一致。");
                }

                TableIndex? index = childSchema.Indexes.FirstOrDefault(candidate =>
                    candidate.JsonPath is null
                    && candidate.Columns.SequenceEqual(fk.Columns, StringComparer.Ordinal));
                if (!lookupsByPrincipal.TryGetValue(fk.PrincipalTable, out var lookups))
                {
                    lookups = new List<CascadeForeignKeyLookup>();
                    lookupsByPrincipal.Add(fk.PrincipalTable, lookups);
                }
                lookups.Add(new CascadeForeignKeyLookup(principalSchema, childSchema, fk, index));
            }
        }
        if (lookupsByPrincipal.Count == 0)
            return mutationsByTable;

        // 构造可变工作集（保留 mutation 顺序），以及每表"已触及行"的 PK 集合（HEX 编码）：
        // pendingDeletePks 驱动删除去重与 CASCADE 的 BFS；touchedPks 额外覆盖用户显式修改与
        // SET NULL 置空，用来避免同一行在一个 batch 内被多次修改（PrepareBatch 会拒绝）。
        var working = new Dictionary<string, List<TableRowMutation>>(StringComparer.Ordinal);
        var pendingDeletePks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var touchedPks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var queue = new Queue<(string Table, IReadOnlyList<object?> Pk)>();

        foreach (var (tableName, mutations) in mutationsByTable)
        {
            if (!schemasByName.TryGetValue(tableName, out var schema))
                throw new InvalidOperationException($"table '{tableName}' 不存在。");
            var list = new List<TableRowMutation>(mutations);
            working[tableName] = list;
            var deletedSet = new HashSet<string>(StringComparer.Ordinal);
            pendingDeletePks[tableName] = deletedSet;
            var touchedSet = new HashSet<string>(StringComparer.Ordinal);
            touchedPks[tableName] = touchedSet;
            foreach (var mutation in mutations)
            {
                if (mutation.PrimaryKeyValues is not null)
                {
                    byte[] pkBytes = TableKeyCodec.EncodePrimaryKeyValues(schema, mutation.PrimaryKeyValues);
                    string pkText = Convert.ToHexString(pkBytes);
                    touchedSet.Add(pkText);
                    if (mutation.NewValues is null && deletedSet.Add(pkText))
                        queue.Enqueue((tableName, mutation.PrimaryKeyValues));
                }
            }
        }

        while (queue.Count > 0)
        {
            var (parentTable, parentPk) = queue.Dequeue();
            if (!lookupsByPrincipal.TryGetValue(parentTable, out var lookups))
                continue;

            foreach (var lookup in lookups)
            {
                var childSchema = lookup.ChildSchema;
                var fk = lookup.ForeignKey;
                var childStore = OpenStoreLocked(childSchema);
                var childDeleteSet = pendingDeletePks.TryGetValue(childSchema.Name, out var existingDeleteSet)
                    ? existingDeleteSet
                    : pendingDeletePks[childSchema.Name] = new HashSet<string>(StringComparer.Ordinal);
                var childTouchedSet = touchedPks.TryGetValue(childSchema.Name, out var existingTouchedSet)
                    ? existingTouchedSet
                    : touchedPks[childSchema.Name] = new HashSet<string>(StringComparer.Ordinal);
                var childList = working.TryGetValue(childSchema.Name, out var existingList)
                    ? existingList
                    : working[childSchema.Name] = new List<TableRowMutation>();

                foreach (var childRow in lookup.FindRows(childStore, parentPk, metrics))
                {
                    var childPk = ExtractPrimaryKeyValues(childSchema, childRow);
                    byte[] childPkBytes = TableKeyCodec.EncodePrimaryKeyValues(childSchema, childPk);
                    string childPkText = Convert.ToHexString(childPkBytes);

                    if (fk.OnDelete == ForeignKeyAction.Cascade)
                    {
                        // 用户显式修改或删除的行由原 mutation 负责；若修改后仍引用父行，
                        // 后续 FK 校验会拒绝整批，不在展开阶段制造同一行的重复 mutation。
                        if (childTouchedSet.Contains(childPkText))
                            continue;
                        if (!childDeleteSet.Add(childPkText))
                            continue;
                        childTouchedSet.Add(childPkText);
                        childList.Add(new TableRowMutation(PrimaryKeyValues: childPk, NewValues: null));
                        queue.Enqueue((childSchema.Name, childPk));
                    }
                    else
                    {
                        // SET NULL：子行已被计划删除或已被本事务触及则跳过（删除优先、避免重复修改）。
                        if (childDeleteSet.Contains(childPkText)) continue;
                        if (!childTouchedSet.Add(childPkText)) continue;

                        var nulledValues = childRow.Values.ToArray();
                        foreach (var fkColumn in fk.Columns)
                        {
                            var column = childSchema.TryGetColumn(fkColumn)
                                ?? throw new InvalidOperationException($"外键 '{fk.Name}' 引用了未知列 '{fkColumn}'。");
                            nulledValues[column.Ordinal] = null;
                        }

                        childList.Add(new TableRowMutation(PrimaryKeyValues: childPk, NewValues: nulledValues));
                    }
                }
            }
        }

        var result = new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal);
        foreach (var (k, v) in working)
            result[k] = v;
        return result;
    }

    /// <summary>
    /// 表示单条反向外键关系在一次级联展开中的行查找器。
    /// </summary>
    private sealed class CascadeForeignKeyLookup
    {
        private Dictionary<byte[], List<TableRow>>? _fallbackRows;

        public CascadeForeignKeyLookup(
            TableSchema principalSchema,
            TableSchema childSchema,
            TableForeignKey foreignKey,
            TableIndex? index)
        {
            PrincipalSchema = principalSchema;
            ChildSchema = childSchema;
            ForeignKey = foreignKey;
            Index = index;
        }

        private TableSchema PrincipalSchema { get; }

        private TableIndex? Index { get; }

        public TableSchema ChildSchema { get; }

        public TableForeignKey ForeignKey { get; }

        /// <summary>
        /// 优先按完整 FK 列索引查找；未建索引时惰性扫描一次子表并按父主键编码分桶。
        /// </summary>
        public IReadOnlyList<TableRow> FindRows(
            TableStore childStore,
            IReadOnlyList<object?> parentPrimaryKey,
            CascadeDeleteExecutionMetrics? metrics)
        {
            if (Index is not null)
            {
                if (metrics is not null)
                    metrics.PersistentIndexLookupCount++;
                return childStore.GetByIndex(Index, parentPrimaryKey);
            }

            if (_fallbackRows is null)
            {
                IReadOnlyList<TableRow> rows = childStore.Scan();
                if (metrics is not null)
                {
                    metrics.FallbackScanCount++;
                    metrics.FallbackDecodedRowCount += rows.Count;
                }

                _fallbackRows = new Dictionary<byte[], List<TableRow>>(KvKeyComparer.Instance);
                foreach (var row in rows)
                {
                    IReadOnlyList<object?>? values = ExtractForeignKeyValues(ChildSchema, row, ForeignKey);
                    if (values is null)
                        continue;

                    byte[] key = TableKeyCodec.EncodePrimaryKeyValues(PrincipalSchema, values);
                    if (!_fallbackRows.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<TableRow>();
                        _fallbackRows.Add(key, bucket);
                    }
                    bucket.Add(row);
                }
            }

            byte[] parentKey = TableKeyCodec.EncodePrimaryKeyValues(PrincipalSchema, parentPrimaryKey);
            return _fallbackRows.TryGetValue(parentKey, out var matches) ? matches : [];
        }
    }

    private void ValidateAddedForeignKeyLocked(TableSchema childSchema, TableForeignKey foreignKey)
    {
        var principalSchema = Catalog.TryGet(foreignKey.PrincipalTable)
            ?? throw new InvalidOperationException($"外键 '{foreignKey.Name}' 引用的表 '{foreignKey.PrincipalTable}' 不存在。");
        if (!foreignKey.PrincipalColumns.SequenceEqual(principalSchema.PrimaryKey, StringComparer.Ordinal))
            throw new NotSupportedException($"外键 '{foreignKey.Name}' 第一版仅支持引用被引用表 PRIMARY KEY。");

        var childStore = OpenStoreLocked(childSchema);
        var emptyPrepared = new Dictionary<string, (TableStore Store, TableStore.PreparedTableBatch Batch)>(StringComparer.Ordinal);
        foreach (var row in childStore.Scan())
        {
            var keyValues = ExtractForeignKeyValues(childSchema, row, foreignKey);
            if (keyValues is null)
                continue;

            if (!PrincipalExistsAfterTransactionLocked(principalSchema, keyValues, emptyPrepared))
            {
                throw new TableConstraintException(
                    TableConstraintException.ForeignKeyViolation,
                    childSchema.Name,
                    foreignKey.Name,
                    $"外键 '{foreignKey.Name}' 冲突：table '{childSchema.Name}' 已有数据引用了不存在的 '{foreignKey.PrincipalTable}' 主键。");
            }
        }
    }

    private void ValidateAddedCheckConstraintLocked(
        TableSchema schema,
        TableCheckConstraint checkConstraint)
    {
        var store = OpenStoreLocked(schema);
        foreach (var row in store.Scan())
            ValidateCheckConstraintRow(schema, row, checkConstraint);
    }

    private static void ValidateCheckConstraintsLocked(
        IReadOnlyDictionary<string, (TableStore Store, TableStore.PreparedTableBatch Batch)> prepared)
    {
        foreach (var entry in prepared.Values)
        {
            var schema = entry.Batch.Schema;
            foreach (var row in entry.Batch.FinalRows)
            {
                foreach (var checkConstraint in schema.CheckConstraints)
                    ValidateCheckConstraintRow(schema, row, checkConstraint);
            }
        }
    }

    private static void ValidateCheckConstraintRow(
        TableSchema schema,
        TableRow row,
        TableCheckConstraint checkConstraint)
    {
        if (TableSqlExecutor.EvaluateCheckConstraint(checkConstraint.Expression, schema, row.Values))
            return;

        throw new TableConstraintException(
            TableConstraintException.CheckViolation,
            schema.Name,
            checkConstraint.Name,
            $"检查约束 '{checkConstraint.Name}' 冲突：table '{schema.Name}' 的行不满足 CHECK ({checkConstraint.ExpressionSql})。");
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
        Func<TableSchema, TableRow, IReadOnlyList<object?>> transform,
        Action<TableSchema, IReadOnlyList<object?>>? validate = null)
    {
        var rollback = store.ApplySchemaTransform(updated, transform, validate);
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

    private void ApplyMetadataSchemaLocked(
        TableSchema current,
        TableSchema updated,
        TableStore store)
    {
        store.ApplyMetadataSchema(updated);
        Catalog.LoadOrReplace(updated);
        try
        {
            PersistCatalogLocked();
        }
        catch
        {
            store.ApplyMetadataSchema(current);
            Catalog.LoadOrReplace(current);
            throw;
        }
    }

    private void EnsureColumnTypeIsNotReferencedByForeignKeyLocked(
        TableSchema schema,
        TableColumn column)
    {
        foreach (var foreignKey in schema.ForeignKeys)
        {
            if (foreignKey.Columns.Any(name => string.Equals(name, column.Name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"列 '{column.Name}' 被外键 '{foreignKey.Name}' 引用，修改类型前必须先删除该约束。");
            }
        }

        foreach (var candidate in Catalog.Snapshot())
        {
            foreach (var foreignKey in candidate.ForeignKeys)
            {
                if (string.Equals(foreignKey.PrincipalTable, schema.Name, StringComparison.Ordinal)
                    && foreignKey.PrincipalColumns.Any(name =>
                        string.Equals(name, column.Name, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"列 '{column.Name}' 被 table '{candidate.Name}' 的外键 '{foreignKey.Name}' 引用，修改类型前必须先删除该约束。");
                }
            }
        }
    }

    private static void ValidateAlteredRowCheckConstraints(
        TableSchema schema,
        IReadOnlyList<object?> values)
    {
        var row = new TableRow(values);
        foreach (var checkConstraint in schema.CheckConstraints)
            ValidateCheckConstraintRow(schema, row, checkConstraint);
    }

    private static string EncodeName(string name)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private bool HasInboundForeignKeyLocked(string tableName)
        => Catalog.Snapshot().Any(schema => schema.ForeignKeys.Any(foreignKey =>
            string.Equals(foreignKey.PrincipalTable, tableName, StringComparison.Ordinal)));

    private void EnsureTableIsNotReferencedByForeignKeyLocked(string tableName, string operation)
    {
        foreach (var schema in Catalog.Snapshot())
        {
            foreach (var foreignKey in schema.ForeignKeys)
            {
                if (!string.Equals(foreignKey.PrincipalTable, tableName, StringComparison.Ordinal))
                    continue;

                throw new InvalidOperationException(
                    $"table '{tableName}' 被 table '{schema.Name}' 的外键 '{foreignKey.Name}' 引用，不能{operation}；请先删除该外键约束。");
            }
        }
    }

    private void DiscoverCleanupTablesLocked()
    {
        foreach (var schema in Catalog.Snapshot())
        {
            string manifestPath = Path.Combine(TableDirectory(schema.Name), KvCleanupManifest.FileName);
            if (File.Exists(manifestPath))
                _ = OpenStoreLocked(schema);
        }
    }

    /// <summary>阻止调用方绕过 TableManager 的 schema 锁、依赖检查和持久化路径直接修改目录。</summary>
    private void EnsureManagedCatalogMutation(string tableName, string operation)
    {
        if (!Monitor.IsEntered(_schemaSync))
        {
            throw new InvalidOperationException(
                $"不能直接对受管理的 TableCatalog 执行 {operation} '{tableName}'；请使用 TableManager 的 schema API。");
        }
    }

    /// <summary>管理器释放后拒绝继续访问已打开的关系表资源。</summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transactionFailure is not null)
            throw new IOException("关系事务需要重启恢复，当前表管理器已停止接受访问。", _transactionFailure);
    }
}

/// <summary>
/// 记录单次事务中级联删除展开所使用的 catalog、索引与回退扫描次数。
/// </summary>
internal sealed class CascadeDeleteExecutionMetrics
{
    /// <summary>级联展开读取 catalog 快照的次数。</summary>
    public int CatalogSnapshotCount { get; internal set; }

    /// <summary>按持久二级索引执行等值查找的次数。</summary>
    public int PersistentIndexLookupCount { get; internal set; }

    /// <summary>缺少匹配索引时执行子表全量扫描的次数。</summary>
    public int FallbackScanCount { get; internal set; }

    /// <summary>回退扫描实际解码的子表行数。</summary>
    public long FallbackDecodedRowCount { get; internal set; }
}
