namespace SonnetDB.Kv;

/// <summary>
/// 管理同一个 SonnetDB 数据库目录下的 KV Keyspace。
/// </summary>
public sealed class KvKeyspaceManager : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, KvKeyspace> _opened = new(StringComparer.Ordinal);
    private readonly KvOptions _options;
    private bool _disposed;

    /// <summary>
    /// 初始化 KV Keyspace 管理器。
    /// </summary>
    /// <param name="rootDirectory">KV 根目录。</param>
    /// <param name="options">KV 存储选项。</param>
    public KvKeyspaceManager(string rootDirectory, KvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        RootDirectory = rootDirectory;
        _options = options ?? KvOptions.Default;
        Directory.CreateDirectory(KeyspacesDirectory);
    }

    /// <summary>KV 根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>Keyspace 集合目录。</summary>
    public string KeyspacesDirectory => Path.Combine(RootDirectory, "keyspaces");

    /// <summary>
    /// 打开（不存在则创建）指定 keyspace。
    /// </summary>
    /// <param name="name">Keyspace 名称，只允许字母、数字、点、下划线和短横线。</param>
    /// <returns>已打开的 <see cref="KvKeyspace"/> 实例。</returns>
    public KvKeyspace Open(string name)
    {
        ValidateName(name);

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_opened.TryGetValue(name, out var existing) && !existing.IsDisposed)
                return existing;

            string root = Path.Combine(KeyspacesDirectory, name);
            var keyspace = KvKeyspace.Open(name, root, _options);
            _opened[name] = keyspace;
            return keyspace;
        }
    }

    /// <summary>
    /// 枚举磁盘上已经存在的 keyspace 名称。
    /// </summary>
    /// <returns>按名称升序排列的 keyspace 名称快照。</returns>
    public IReadOnlyList<string> List()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!Directory.Exists(KeyspacesDirectory))
                return Array.Empty<string>();

            var names = Directory.EnumerateDirectories(KeyspacesDirectory)
                .Select(Path.GetFileName)
                .Where(static x => x is not null && IsValidName(x))
                .Select(static x => x!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return names;
        }
    }

    /// <summary>
    /// 为当前已打开的 keyspace 创建一致快照，并返回成功 checkpoint 的 keyspace 名称。
    /// </summary>
    public IReadOnlyList<string> CheckpointOpened()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var names = new List<string>(_opened.Count);
            foreach (var pair in _opened.OrderBy(static x => x.Key, StringComparer.Ordinal))
            {
                if (pair.Value.IsDisposed)
                    continue;

                pair.Value.CreateSnapshot();
                names.Add(pair.Key);
            }

            return names.AsReadOnly();
        }
    }

    /// <summary>
    /// 清理当前已打开 keyspace 中的过期 key。
    /// </summary>
    /// <param name="utcNow">用于判定过期的 UTC 时间。</param>
    /// <param name="limitPerKeyspace">每个 keyspace 最多清理数量；为空表示不限制。</param>
    /// <returns>实际清理的 key 总数。</returns>
    public int CleanExpiredOpened(DateTimeOffset? utcNow = null, int? limitPerKeyspace = null)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            int removed = 0;
            foreach (var keyspace in _opened.Values.ToArray())
            {
                if (!keyspace.IsDisposed)
                    removed += keyspace.CleanExpired(utcNow, limitPerKeyspace);
            }

            return removed;
        }
    }

    /// <summary>
    /// 关闭所有已打开的 keyspace。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var keyspace in _opened.Values)
                keyspace.Dispose();
            _opened.Clear();
        }
    }

    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsValidName(name))
            throw new ArgumentException("Keyspace 名称只允许字母、数字、点、下划线和短横线，且不能为 . 或 ..。", nameof(name));
    }

    private static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 128)
            return false;

        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            bool valid =
                ch is >= 'a' and <= 'z' ||
                ch is >= 'A' and <= 'Z' ||
                ch is >= '0' and <= '9' ||
                ch is '_' or '-' or '.';
            if (!valid)
                return false;
        }

        return true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
