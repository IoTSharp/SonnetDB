using System.Globalization;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Views;

/// <summary>
/// 管理同一数据库目录下的物化视图定义、刷新状态和独立物理代际。
/// </summary>
public sealed class MaterializedViewManager
{
    private const string DataDirectoryName = "data";
    private const string SnapshotPrefix = "generation-";
    private const string SnapshotExtension = ".sdbmvsnap";
    private readonly object _sync = new();
    private readonly object _schemaSync;
    private readonly Action<string, string>? _nameAvailabilityGuard;
    private readonly Dictionary<(Guid StorageId, long Generation), SelectExecutionResult> _snapshotCache = new();

    /// <summary>
    /// 初始化管理器，加载物化视图目录并恢复被进程中断的刷新状态。
    /// </summary>
    /// <param name="rootDirectory">物化视图根目录。</param>
    public MaterializedViewManager(string rootDirectory)
        : this(rootDirectory, nameAvailabilityGuard: null, synchronizationRoot: new object())
    {
    }

    /// <summary>
    /// 使用数据库级 schema 锁和跨模型名称守卫初始化物化视图管理器。
    /// </summary>
    /// <param name="rootDirectory">物化视图根目录。</param>
    /// <param name="nameAvailabilityGuard">跨模型名称占用检查。</param>
    /// <param name="synchronizationRoot">数据库级 schema 同步根。</param>
    internal MaterializedViewManager(
        string rootDirectory,
        Action<string, string>? nameAvailabilityGuard,
        object synchronizationRoot)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(synchronizationRoot);
        _schemaSync = synchronizationRoot;
        _nameAvailabilityGuard = nameAvailabilityGuard;
        RootDirectory = rootDirectory;
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(DataDirectory);
        CatalogPath = Path.Combine(rootDirectory, MaterializedViewDefinitionCodec.FileName);
        Catalog = new MaterializedViewCatalog();
        foreach (var definition in MaterializedViewDefinitionCodec.Load(CatalogPath))
        {
            if (definition.ActiveGeneration != 0 && !File.Exists(GetGenerationPath(definition)))
            {
                throw new InvalidDataException(
                    $"materialized view '{definition.Name}' 的活动代际 {definition.ActiveGeneration} 不存在。");
            }
            Catalog.LoadOrReplace(definition);
        }
        RecoverInterruptedRefreshes();
        CleanupUnpublishedArtifacts();
        Catalog.MutationGuard = EnsureManagedCatalogMutation;
    }

    /// <summary>物化视图定义目录。</summary>
    public MaterializedViewCatalog Catalog { get; }

    /// <summary>物化视图根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>物化视图目录文件路径。</summary>
    public string CatalogPath { get; }

    private string DataDirectory => Path.Combine(RootDirectory, DataDirectoryName);

    /// <summary>
    /// 新增尚未刷新的物化视图并原子持久化定义。
    /// </summary>
    /// <param name="definition">待新增定义。</param>
    public void Create(MaterializedViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_schemaSync)
        lock (_sync)
        {
            _nameAvailabilityGuard?.Invoke(definition.Name, "materialized view");
            if (Catalog.TryGet(definition.Name) is not null)
                throw new InvalidOperationException($"materialized view '{definition.Name}' 已存在。");
            PersistProjectedCatalog(definition, removeName: null);
            Catalog.Add(definition);
        }
    }

    /// <summary>
    /// 删除物化视图定义及其全部物理代际。
    /// </summary>
    /// <param name="name">物化视图名称。</param>
    /// <returns>存在并删除时返回 <c>true</c>。</returns>
    public bool Drop(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string? storageDirectory = null;
        lock (_schemaSync)
        lock (_sync)
        {
            var existing = Catalog.TryGet(name);
            if (existing is null)
                return false;

            PersistProjectedCatalog(replacement: null, removeName: name);
            Catalog.Remove(name);
            foreach (var key in _snapshotCache.Keys.Where(key => key.StorageId == existing.StorageId).ToArray())
                _snapshotCache.Remove(key);
            storageDirectory = GetStorageDirectory(existing.StorageId);
        }

        if (Directory.Exists(storageDirectory))
            Directory.Delete(storageDirectory, recursive: true);
        return true;
    }

    /// <summary>
    /// 返回直接引用指定对象的物化视图，按名称升序排列。
    /// </summary>
    /// <param name="objectName">被引用对象名称。</param>
    /// <returns>直接依赖该对象的物化视图列表。</returns>
    public IReadOnlyList<MaterializedViewDefinition> FindDependents(string objectName)
    {
        ArgumentNullException.ThrowIfNull(objectName);
        return Catalog.Snapshot()
            .Where(definition => definition.Dependencies.Contains(objectName, StringComparer.Ordinal))
            .ToArray();
    }

    internal SelectExecutionResult Refresh(string name, Func<SelectExecutionResult> materialize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(materialize);

        MaterializedViewDefinition started;
        long generation;
        lock (_schemaSync)
        lock (_sync)
        {
            var existing = Catalog.TryGet(name)
                ?? throw new InvalidOperationException($"materialized view '{name}' 不存在。");
            if (existing.Status == MaterializedViewRefreshStatus.Refreshing)
                throw new InvalidOperationException($"materialized view '{name}' 已在刷新。");
            generation = GetNextGeneration(existing.StorageId);
            started = existing.WithRefreshStarted();
            PersistAndPublish(started);
        }

        string generationPath = GetGenerationPath(started.StorageId, generation);
        try
        {
            SelectExecutionResult result = materialize();
            MaterializedViewSnapshotCodec.Save(generationPath, result);
            long completedAt = DateTime.UtcNow.Ticks;
            lock (_schemaSync)
            lock (_sync)
            {
                var current = Catalog.TryGet(name)
                    ?? throw new InvalidOperationException($"materialized view '{name}' 在刷新期间被删除。");
                if (current.Status != MaterializedViewRefreshStatus.Refreshing
                    || current.StorageId != started.StorageId)
                {
                    throw new InvalidOperationException($"materialized view '{name}' 的刷新状态已发生冲突。");
                }

                var completed = current.WithRefreshSucceeded(generation, result.Rows.Count, completedAt);
                PersistAndPublish(completed);
                _snapshotCache[(completed.StorageId, generation)] = result;
            }
            return result;
        }
        catch (Exception refreshException)
        {
            try
            {
                lock (_schemaSync)
                lock (_sync)
                {
                    var current = Catalog.TryGet(name);
                    if (current is not null
                        && current.StorageId == started.StorageId
                        && current.Status == MaterializedViewRefreshStatus.Refreshing)
                    {
                        PersistAndPublish(current.WithRefreshFailed(
                            DateTime.UtcNow.Ticks,
                            NormalizeError(refreshException.Message)));
                    }
                }
            }
            catch (Exception persistenceException)
            {
                throw new AggregateException(
                    $"materialized view '{name}' 刷新失败，且失败状态无法持久化。",
                    refreshException,
                    persistenceException);
            }
            throw;
        }
    }

    internal SelectExecutionResult ReadSnapshot(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            var definition = Catalog.TryGet(name)
                ?? throw new InvalidOperationException($"materialized view '{name}' 不存在。");
            if (definition.ActiveGeneration == 0)
            {
                string suffix = definition.LastError is null
                    ? string.Empty
                    : $" 最近一次刷新错误：{definition.LastError}";
                throw new InvalidOperationException(
                    $"materialized view '{name}' 尚无可读代际；请先执行 REFRESH MATERIALIZED VIEW。{suffix}");
            }

            var cacheKey = (definition.StorageId, definition.ActiveGeneration);
            if (_snapshotCache.TryGetValue(cacheKey, out var cached))
                return cached;

            string path = GetGenerationPath(definition);
            SelectExecutionResult snapshot = MaterializedViewSnapshotCodec.Load(path);
            if (snapshot.Rows.Count != definition.RowCount)
            {
                throw new InvalidDataException(
                    $"materialized view '{name}' 的目录行数 {definition.RowCount} 与物理代际行数 {snapshot.Rows.Count} 不一致。");
            }
            _snapshotCache[cacheKey] = snapshot;
            return snapshot;
        }
    }

    internal string GetGenerationPath(MaterializedViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return GetGenerationPath(definition.StorageId, definition.ActiveGeneration);
    }

    internal string GetGenerationPath(Guid storageId, long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        return Path.Combine(
            GetStorageDirectory(storageId),
            SnapshotPrefix + generation.ToString("D20", CultureInfo.InvariantCulture) + SnapshotExtension);
    }

    private void RecoverInterruptedRefreshes()
    {
        var interrupted = Catalog.Snapshot()
            .Where(static definition => definition.Status == MaterializedViewRefreshStatus.Refreshing)
            .ToArray();
        if (interrupted.Length == 0)
            return;

        long recoveredAt = DateTime.UtcNow.Ticks;
        var replacements = interrupted.ToDictionary(
            static definition => definition.Name,
            definition => definition.WithRefreshFailed(
                recoveredAt,
                "上一次刷新被进程终止，活动代际未切换。"),
            StringComparer.Ordinal);
        var projected = Catalog.Snapshot()
            .Select(definition => replacements.TryGetValue(definition.Name, out var replacement)
                ? replacement
                : definition)
            .ToArray();
        MaterializedViewDefinitionCodec.Save(CatalogPath, projected);
        foreach (var replacement in replacements.Values)
            Catalog.LoadOrReplace(replacement);
    }

    private void CleanupUnpublishedArtifacts()
    {
        var activePaths = Catalog.Snapshot()
            .Where(static definition => definition.ActiveGeneration != 0)
            .Select(GetGenerationPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string temporaryPath in Directory.EnumerateFiles(
            DataDirectory,
            "*.tmp-*",
            SearchOption.AllDirectories))
        {
            File.Delete(temporaryPath);
        }
        foreach (string generationPath in Directory.EnumerateFiles(
            DataDirectory,
            SnapshotPrefix + "*" + SnapshotExtension,
            SearchOption.AllDirectories))
        {
            if (!activePaths.Contains(generationPath))
                File.Delete(generationPath);
        }
    }

    private void PersistAndPublish(MaterializedViewDefinition definition)
    {
        PersistProjectedCatalog(definition, removeName: null);
        Catalog.LoadOrReplace(definition);
    }

    private void PersistProjectedCatalog(MaterializedViewDefinition? replacement, string? removeName)
    {
        var projected = Catalog.Snapshot()
            .Where(definition => !string.Equals(definition.Name, removeName, StringComparison.Ordinal)
                && (replacement is null || !string.Equals(definition.Name, replacement.Name, StringComparison.Ordinal)))
            .ToList();
        if (replacement is not null)
            projected.Add(replacement);
        MaterializedViewDefinitionCodec.Save(CatalogPath, projected);
    }

    /// <summary>阻止调用方绕过 MaterializedViewManager 的 schema 锁和持久化路径直接修改目录。</summary>
    private void EnsureManagedCatalogMutation(string viewName, string operation)
    {
        if (!Monitor.IsEntered(_schemaSync))
        {
            throw new InvalidOperationException(
                $"不能直接对受管理的 MaterializedViewCatalog 执行 {operation} '{viewName}'；请使用 MaterializedViewManager 的 schema API。");
        }
    }

    private long GetNextGeneration(Guid storageId)
    {
        string directory = GetStorageDirectory(storageId);
        Directory.CreateDirectory(directory);
        long maximum = 0;
        foreach (string path in Directory.EnumerateFiles(directory, SnapshotPrefix + "*" + SnapshotExtension))
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            if (fileName.StartsWith(SnapshotPrefix, StringComparison.Ordinal)
                && long.TryParse(
                    fileName.AsSpan(SnapshotPrefix.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long generation)
                && generation > maximum)
            {
                maximum = generation;
            }
        }
        return checked(maximum + 1);
    }

    private string GetStorageDirectory(Guid storageId)
        => Path.Combine(DataDirectory, storageId.ToString("N", CultureInfo.InvariantCulture));

    private static string NormalizeError(string error)
    {
        const int maxCharacters = 100_000;
        if (string.IsNullOrWhiteSpace(error))
            return "刷新失败，未提供错误消息。";
        string normalized = error.Trim();
        return normalized.Length <= maxCharacters ? normalized : normalized[..maxCharacters];
    }
}
