using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex DatabaseNameRegex();

    private readonly ConcurrentDictionary<string, Tsdb> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly string _dataRoot;
    private readonly EventBroadcaster? _broadcaster;
    private readonly KvOptions _kvOptions;
    private bool _disposed;

    /// <summary>
    /// 使用 Core 默认 KV 选项构造注册表。<paramref name="dataRoot"/> 为所有数据库的父目录。
    /// </summary>
    /// <param name="dataRoot">数据库根目录。</param>
    /// <param name="broadcaster">可选事件广播器；非 null 时 Create/Drop 会广播 <c>db</c> 事件。</param>
    public TsdbRegistry(string dataRoot, EventBroadcaster? broadcaster = null)
        : this(dataRoot, broadcaster, kvOptions: null)
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
        _broadcaster = broadcaster;
        _kvOptions = kvOptions ?? KvOptions.Default;
        Directory.CreateDirectory(_dataRoot);
    }

    /// <summary>
    /// 数据库根目录。
    /// </summary>
    public string DataRoot => _dataRoot;

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
            var instance = Tsdb.Open(new TsdbOptions
            {
                RootDirectory = path,
                AllowUserFunctions = false,
                Kv = _kvOptions,
            });
            _databases[name] = instance;
            tsdb = instance;
            _broadcaster?.Publish(ServerEventFactory.Database(
                new DatabaseEvent(name, DatabaseEvent.ActionCreated)));
            return true;
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
                var name = Path.GetFileName(dir);
                if (!IsValidName(name) || _databases.ContainsKey(name))
                    continue;
                var instance = Tsdb.Open(new TsdbOptions
                {
                    RootDirectory = dir,
                    AllowUserFunctions = false,
                    Kv = _kvOptions,
                });
                _databases[name] = instance;
            }
        }
    }

    /// <summary>
    /// 删除数据库：从注册表移除、Dispose 实例，并把磁盘目录尝试删除（best-effort）。
    /// </summary>
    public bool Drop(string name)
    {
        EnsureNotDisposed();
        lock (_sync)
        {
            if (!_databases.TryRemove(name, out var instance))
                return false;
            instance.Dispose();
            try
            {
                var path = Path.Combine(_dataRoot, name);
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
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
