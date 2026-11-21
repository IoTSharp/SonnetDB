using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SonnetDB.Backup;
using SonnetDB.Contracts;
using SonnetDB.Endpoints;
using SonnetDB.Engine;
using SonnetDB.Kv;

namespace SonnetDB.Hosting;

/// <summary>
/// 进程内多 <see cref="Tsdb"/> 实例注册表。每个数据库 = 一个子目录 + 一个引擎实例。
/// </summary>
/// <remarks>
/// 线程安全：所有 mutating 操作在 <c>_sync</c> 内串行化；
/// <see cref="TryGet"/> 是 lock-free 读。
/// </remarks>
public sealed partial class TsdbRegistry : IDisposable
{
    /// <summary>合法数据库名匹配。</summary>
    [GeneratedRegex(@"\A[a-zA-Z0-9_-]{1,64}\z")]
    private static partial Regex DatabaseNameRegex();

    private readonly ConcurrentDictionary<string, Tsdb> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mountedDatabaseNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly string _dataRoot;
    private readonly EventBroadcaster? _broadcaster;
    private readonly KvOptions _kvOptions;
    private readonly SqlMemoryOptions _sqlMemoryOptions;
    private readonly MeasurementSchemaPolicy _measurementSchemaPolicy;
    private readonly string? _objectStorageRootDirectory;
    private bool _disposed;

    /// <summary>
    /// 使用 Core 默认 KV 选项构造注册表。<paramref name="dataRoot"/> 为所有数据库的父目录。
    /// </summary>
    /// <param name="dataRoot">数据库根目录。</param>
    /// <param name="broadcaster">可选事件广播器；非 null 时 Create/Drop 会广播 <c>db</c> 事件。</param>
    public TsdbRegistry(string dataRoot, EventBroadcaster? broadcaster = null)
        : this(dataRoot, broadcaster, kvOptions: null, sqlMemoryOptions: null)
    {
    }

    /// <summary>
    /// 使用指定 KV 选项构造注册表。<paramref name="dataRoot"/> 为所有数据库的父目录。
    /// </summary>
    /// <param name="dataRoot">数据库根目录。</param>
    /// <param name="broadcaster">可选事件广播器；非 null 时 Create/Drop 会广播 <c>db</c> 事件。</param>
    /// <param name="kvOptions">所有数据库共用的 KV 选项；为空时使用 Core 默认值。</param>
    public TsdbRegistry(
        string dataRoot,
        EventBroadcaster? broadcaster,
        KvOptions? kvOptions)
        : this(dataRoot, broadcaster, kvOptions, sqlMemoryOptions: null)
    {
    }

    /// <summary>
    /// 使用指定 KV 与 SQL 内部资源选项构造注册表。传入选项会作为所有新建和自动加载数据库的固定配置。
    /// </summary>
    /// <param name="dataRoot">数据库根目录。</param>
    /// <param name="broadcaster">可选事件广播器；非 null 时 Create/Drop 会广播 <c>db</c> 事件。</param>
    /// <param name="kvOptions">所有数据库共用的 KV 选项；为空时使用 Core 默认值。</param>
    /// <param name="sqlMemoryOptions">所有数据库共用的 SQL 内部资源选项；为空时使用 Core 默认值。</param>
    public TsdbRegistry(
        string dataRoot,
        EventBroadcaster? broadcaster,
        KvOptions? kvOptions,
        SqlMemoryOptions? sqlMemoryOptions)
        : this(dataRoot, broadcaster, kvOptions, sqlMemoryOptions, measurementSchemaPolicy: null)
    {
    }

    /// <summary>使用指定的 KV、SQL 内部资源、measurement schema 与对象卷策略构造注册表。</summary>
    /// <param name="dataRoot">数据库根目录。</param>
    /// <param name="broadcaster">可选事件广播器。</param>
    /// <param name="kvOptions">所有数据库共用的 KV 选项。</param>
    /// <param name="sqlMemoryOptions">所有数据库共用的 SQL 内部资源选项。</param>
    /// <param name="measurementSchemaPolicy">所有数据库共用的 measurement schema 策略。</param>
    /// <param name="objectStorageRootDirectory">
    /// 对象卷根目录。设置后数据库对象正文位于其下的数据库名/objects 子目录；为空时使用数据库目录下的 objects。
    /// </param>
    public TsdbRegistry(
        string dataRoot,
        EventBroadcaster? broadcaster,
        KvOptions? kvOptions,
        SqlMemoryOptions? sqlMemoryOptions,
        MeasurementSchemaPolicy? measurementSchemaPolicy,
        string? objectStorageRootDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
        _broadcaster = broadcaster;
        _kvOptions = kvOptions ?? KvOptions.Default;
        _sqlMemoryOptions = sqlMemoryOptions ?? SqlMemoryOptions.Default;
        _measurementSchemaPolicy = (measurementSchemaPolicy ?? MeasurementSchemaPolicy.Default).ValidateAndCopy();
        _objectStorageRootDirectory = string.IsNullOrWhiteSpace(objectStorageRootDirectory)
            ? null
            : Path.GetFullPath(objectStorageRootDirectory);
        _sqlMemoryOptions.Validate();
        Directory.CreateDirectory(_dataRoot);
    }

    /// <summary>
    /// 数据库根目录。
    /// </summary>
    public string DataRoot => _dataRoot;

    /// <summary>当前注册表使用的对象卷根目录；未配置时返回 null。</summary>
    public string? ObjectStorageRootDirectory => _objectStorageRootDirectory;

    /// <summary>
    /// 当前已注册的数据库数量。
    /// </summary>
    public int Count => _databases.Count;

    /// <summary>
    /// 当前已注册的数据库名快照（按名称排序）。
    /// </summary>
    public IReadOnlyList<string> ListDatabases()
    {
        var names = _databases.Keys.ToArray();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>判断数据库是否从外部嵌入式目录挂载。</summary>
    /// <param name="name">数据库名称。</param>
    /// <returns>外部挂载时为 true。</returns>
    public bool IsMounted(string name)
    {
        lock (_sync)
            return _mountedDatabaseNames.Contains(name);
    }

    /// <summary>
    /// 校验数据库名是否合法。
    /// </summary>
    public static bool IsValidName(string name)
        => !string.IsNullOrEmpty(name) && DatabaseNameRegex().IsMatch(name);

    /// <summary>
    /// 尝试取出数据库实例。
    /// </summary>
    public bool TryGet(string name, out Tsdb tsdb)
    {
        if (_databases.TryGetValue(name, out var existing))
        {
            tsdb = existing;
            return true;
        }
        tsdb = null!;
        return false;
    }

    /// <summary>
    /// 创建数据库。如果已存在则返回 false（不抛异常）。
    /// </summary>
    public bool TryCreate(string name, out Tsdb tsdb)
    {
        EnsureNotDisposed();
        if (!IsValidName(name))
            throw new ArgumentException($"非法数据库名 '{name}'，仅允许 [a-zA-Z0-9_-]，长度 1–64。", nameof(name));

        lock (_sync)
        {
            if (_databases.TryGetValue(name, out var existing))
            {
                tsdb = existing;
                return false;
            }

            var path = Path.Combine(_dataRoot, name);
            Directory.CreateDirectory(path);
            var instance = OpenDatabase(path, name);
            _databases[name] = instance;
            tsdb = instance;
            _broadcaster?.Publish(ServerEventFactory.Database(
                new DatabaseEvent(name, DatabaseEvent.ActionCreated)));
            return true;
        }
    }

    /// <summary>
    /// 将已校验的备份恢复为一个新的数据库实例，拒绝覆盖现有数据库目录。
    /// </summary>
    /// <param name="backupDirectory">已校验的备份目录。</param>
    /// <param name="namePrefix">自动生成名称的前缀。</param>
    /// <param name="databaseName">输出的新数据库名称。</param>
    /// <returns>新实例。</returns>
    public Tsdb RestoreAsNew(string backupDirectory, string namePrefix, out string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(namePrefix);
        string source = Path.GetFullPath(backupDirectory);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"备份目录不存在：{source}");

        lock (_sync)
        {
            EnsureNotDisposed();
            for (int attempt = 0; attempt < 100; attempt++)
            {
                string suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                string candidate = $"{namePrefix}-{suffix}-{attempt + 1}";
                if (!IsValidName(candidate) || _databases.ContainsKey(candidate))
                    continue;

                string target = Path.Combine(_dataRoot, candidate);
                if (Path.Exists(target)
                    || (ResolveObjectStorageRoot(candidate) is { } objectTarget && Path.Exists(Path.GetDirectoryName(objectTarget)!)))
                    continue;

                bool restored = false;
                try
                {
                    new BackupService().Restore(new BackupRestoreOptions
                    {
                        BackupDirectory = source,
                        TargetDirectory = target,
                    });
                    restored = true;
                    Tsdb instance = OpenDatabase(target, candidate);
                    _databases[candidate] = instance;
                    databaseName = candidate;
                    _broadcaster?.Publish(ServerEventFactory.Database(
                        new DatabaseEvent(candidate, DatabaseEvent.ActionCreated)));
                    return instance;
                }
                catch
                {
                    if (restored && Directory.Exists(target))
                        Directory.Delete(target, recursive: true);
                    throw;
                }
            }
        }

        throw new IOException("无法为恢复生成不冲突的数据库名称。");
    }

    /// <summary>在单一数据根内重命名已加载的数据库。</summary>
    /// <param name="currentName">当前数据库名称。</param>
    /// <param name="newName">新数据库名称。</param>
    /// <returns>重命名成功时返回 true。</returns>
    public bool TryRename(string currentName, string newName)
    {
        if (!IsValidName(currentName) || !IsValidName(newName)
            || string.Equals(currentName, newName, StringComparison.OrdinalIgnoreCase))
            return false;

        lock (_sync)
        {
            EnsureNotDisposed();
            if (_objectStorageRootDirectory is not null
                || _mountedDatabaseNames.Contains(currentName)
                || _databases.ContainsKey(newName)
                || !_databases.TryGetValue(currentName, out Tsdb? instance))
                return false;

            string oldPath = instance.RootDirectory;
            string newPath = Path.Combine(_dataRoot, newName);
            if (Path.Exists(newPath))
                return false;
            string registeredName = Path.GetFileName(oldPath);
            bool moved = false;

            _databases.TryRemove(registeredName, out _);
            instance.Dispose();
            try
            {
                Directory.Move(oldPath, newPath);
                moved = true;

                Tsdb renamed = OpenDatabase(newPath, newName);
                _databases[newName] = renamed;
                _broadcaster?.Publish(ServerEventFactory.Database(
                    new DatabaseEvent(newName, DatabaseEvent.ActionCreated)));
                return true;
            }
            catch
            {
                if (moved)
                    Directory.Move(newPath, oldPath);
                _databases[registeredName] = OpenDatabase(oldPath, registeredName);
                throw;
            }
        }
    }

    /// <summary>
    /// 启动时扫描 <see cref="DataRoot"/>，把每个子目录注册为现有数据库。
    /// </summary>
    public void LoadExisting()
    {
        EnsureNotDisposed();
        lock (_sync)
        {
            foreach (var dir in Directory.EnumerateDirectories(_dataRoot))
            {
                // 外置对象卷可能位于 DataRoot 下，但它不是数据库目录，不能被自动加载为数据库。
                if (_objectStorageRootDirectory is not null
                    && PathsEqual(dir, _objectStorageRootDirectory))
                    continue;
                var name = Path.GetFileName(dir);
                if (!IsValidName(name) || _databases.ContainsKey(name))
                    continue;
                var instance = OpenDatabase(dir, name);
                _databases[name] = instance;
            }
        }
    }

    /// <summary>
    /// 挂载已有的嵌入式数据库目录；该目录不会由 Server 的删除数据库操作移除。
    /// </summary>
    /// <param name="name">对外显示的数据库名。</param>
    /// <param name="path">已有嵌入式数据库的绝对目录。</param>
    public void MountExisting(string name, string path)
    {
        EnsureNotDisposed();
        if (!IsValidName(name))
            throw new ArgumentException("挂载数据库名称无效。", nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)
            || (!File.Exists(TsdbPaths.CatalogPath(fullPath))
                && !File.Exists(Path.Combine(TsdbPaths.WalDir(fullPath), TsdbPaths.ActiveWalFileName))))
            throw new DirectoryNotFoundException($"已有 SonnetDB 数据库目录无效：{fullPath}");

        lock (_sync)
        {
            if (_databases.ContainsKey(name))
                throw new InvalidOperationException($"数据库名称已存在：{name}");
            _databases[name] = OpenDatabase(fullPath, name);
            _mountedDatabaseNames.Add(name);
        }
    }

    /// <summary>使用注册表冻结的存储、SQL 与对象卷配置打开一个数据库实例。</summary>
    /// <param name="path">数据库目录绝对路径。</param>
    /// <param name="databaseName">数据库显示名，用于隔离对象卷目录。</param>
    /// <returns>已打开的数据库实例。</returns>
    private Tsdb OpenDatabase(string path, string databaseName)
        => Tsdb.Open(new TsdbOptions
        {
            RootDirectory = path,
            AllowUserFunctions = false,
            Kv = _kvOptions,
            SqlMemory = _sqlMemoryOptions,
            MeasurementSchemaPolicy = _measurementSchemaPolicy,
            ObjectStorageRootDirectory = ResolveObjectStorageRoot(databaseName),
        });

    /// <summary>为数据库计算对象卷路径，保持不同数据库的对象正文相互隔离。</summary>
    /// <param name="databaseName">数据库显示名。</param>
    /// <returns>该数据库的对象正文根目录，未配置外置卷时返回 null。</returns>
    private string? ResolveObjectStorageRoot(string databaseName)
        => _objectStorageRootDirectory is null
            ? null
            : Path.Combine(_objectStorageRootDirectory, databaseName, "objects");

    /// <summary>比较目录规范路径，兼容 Windows 大小写和末尾分隔符差异。</summary>
    /// <param name="left">左侧目录。</param>
    /// <param name="right">右侧目录。</param>
    /// <returns>两条路径指向同一规范目录时返回 true。</returns>
    private static bool PathsEqual(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }

    /// <summary>
    /// 删除数据库：从注册表移除、Dispose 实例，并把磁盘目录尝试删除（best-effort）。
    /// </summary>
    public bool Drop(string name)
    {
        EnsureNotDisposed();
        lock (_sync)
        {
            if (_mountedDatabaseNames.Contains(name))
                return false;
            if (!_databases.TryRemove(name, out var instance))
                return false;
            instance.Dispose();
            try
            {
                var path = Path.Combine(_dataRoot, name);
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);

                string? objectStorageRoot = ResolveObjectStorageRoot(name);
                if (objectStorageRoot is not null && Directory.Exists(objectStorageRoot))
                    Directory.Delete(objectStorageRoot, recursive: true);
            }
            catch (IOException)
            {
                // best-effort：句柄可能仍被异步 worker 持有，等下次重启清理
            }
            _broadcaster?.Publish(ServerEventFactory.Database(
                new DatabaseEvent(name, DatabaseEvent.ActionDropped)));
            return true;
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var instance in _databases.Values)
            {
                try { instance.Dispose(); } catch { /* swallow on shutdown */ }
            }
            _databases.Clear();
        }
    }
}
