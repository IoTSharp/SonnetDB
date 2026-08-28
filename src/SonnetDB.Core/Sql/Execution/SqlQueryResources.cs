using SonnetDB.Engine;

namespace SonnetDB.Sql.Execution;

/// <summary>单个数据库实例内并发 SQL 查询共享的阻塞算子预算。</summary>
internal sealed class SqlGlobalMemoryBudget
{
    private readonly long _limitBytes;
    private long _reservedBytes;

    internal SqlGlobalMemoryBudget(long limitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitBytes);
        _limitBytes = limitBytes;
    }

    internal bool TryReserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        while (true)
        {
            long current = Volatile.Read(ref _reservedBytes);
            if (bytes > _limitBytes - current)
                return false;
            if (Interlocked.CompareExchange(ref _reservedBytes, current + bytes, current) == current)
                return true;
        }
    }

    internal void Release(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        long remaining = Interlocked.Add(ref _reservedBytes, -bytes);
        if (remaining < 0)
            throw new InvalidOperationException("SQL 全局内存预算释放量超过已预留量。");
    }

    internal long ReservedBytes => Volatile.Read(ref _reservedBytes);
}

/// <summary>单条 SQL 根执行范围内共享的预算、取消和 spill 工作区。</summary>
internal sealed class SqlQueryResources : IDisposable
{
    private static readonly AsyncLocal<SqlQueryResources?> CurrentSlot = new();
    private readonly SqlGlobalMemoryBudget _globalBudget;
    private readonly long _queryLimitBytes;
    private readonly string _rootDirectory;
    private long _reservedBytes;
    private long _peakReservedBytes;
    private SqlSpillWorkspace? _workspace;
    private bool _disposed;

    private SqlQueryResources(Tsdb tsdb, SqlExecutionOptions options)
    {
        _globalBudget = tsdb.SqlMemoryBudget;
        _queryLimitBytes = options.BlockingOperatorMemoryLimitBytes
            ?? tsdb.SqlMemoryOptions.QueryLimitBytes;
        _rootDirectory = tsdb.RootDirectory;
        CancellationToken = options.CancellationToken;
    }

    internal static SqlQueryResources? Current => CurrentSlot.Value;

    internal CancellationToken CancellationToken { get; }

    internal static Scope EnterRoot(Tsdb tsdb, SqlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(options);
        if (CurrentSlot.Value is not null)
            return new Scope(owned: null);

        var resources = new SqlQueryResources(tsdb, options);
        CurrentSlot.Value = resources;
        return new Scope(resources);
    }

    internal SqlOperatorMemoryReservation CreateReservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new SqlOperatorMemoryReservation(this);
    }

    internal SqlSpillWorkspace GetWorkspace()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _workspace ??= SqlSpillWorkspace.Create(_rootDirectory);
    }

    internal void ThrowIfCancellationRequested()
        => CancellationToken.ThrowIfCancellationRequested();

    private bool TryReserve(long bytes)
    {
        while (true)
        {
            long current = Volatile.Read(ref _reservedBytes);
            if (bytes > _queryLimitBytes - current)
                return false;
            if (Interlocked.CompareExchange(ref _reservedBytes, current + bytes, current) != current)
                continue;
            if (!_globalBudget.TryReserve(bytes))
            {
                Interlocked.Add(ref _reservedBytes, -bytes);
                return false;
            }

            long peak = Math.Max(Volatile.Read(ref _peakReservedBytes), current + bytes);
            while (peak > Volatile.Read(ref _peakReservedBytes))
            {
                long observed = Volatile.Read(ref _peakReservedBytes);
                if (peak <= observed
                    || Interlocked.CompareExchange(ref _peakReservedBytes, peak, observed) == observed)
                {
                    break;
                }
            }
            SqlExecutionTelemetry.RecordPeakMemory(peak);
            return true;
        }
    }

    private void Release(long bytes)
    {
        if (bytes == 0)
            return;
        long remaining = Interlocked.Add(ref _reservedBytes, -bytes);
        if (remaining < 0)
            throw new InvalidOperationException("SQL 查询内存预算释放量超过已预留量。");
        _globalBudget.Release(bytes);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _workspace?.Dispose();
        }
        finally
        {
            long remaining = Interlocked.Exchange(ref _reservedBytes, 0);
            if (remaining != 0)
                _globalBudget.Release(remaining);
        }
    }

    internal sealed class SqlOperatorMemoryReservation : IDisposable
    {
        private SqlQueryResources? _owner;
        private long _bytes;

        internal SqlOperatorMemoryReservation(SqlQueryResources owner) => _owner = owner;

        internal bool TryReserve(long bytes)
        {
            ObjectDisposedException.ThrowIf(_owner is null, this);
            if (!_owner.TryReserve(bytes))
                return false;
            _bytes = checked(_bytes + bytes);
            return true;
        }

        internal void ReleaseAll()
        {
            if (_owner is null || _bytes == 0)
                return;
            long bytes = _bytes;
            _bytes = 0;
            _owner.Release(bytes);
        }

        public void Dispose()
        {
            ReleaseAll();
            _owner = null;
        }
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly SqlQueryResources? _owned;

        internal Scope(SqlQueryResources? owned) => _owned = owned;

        public void Dispose()
        {
            if (_owned is null)
                return;
            if (ReferenceEquals(CurrentSlot.Value, _owned))
                CurrentSlot.Value = null;
            _owned.Dispose();
        }
    }
}

/// <summary>查询拥有的临时文件目录；只删除含 SonnetDB 所有权标记的目录。</summary>
internal sealed class SqlSpillWorkspace : IDisposable
{
    internal const string DirectoryName = "sql-spill";
    internal const string OwnerMarkerFileName = ".sonnetdb-sql-spill";
    private readonly string _directory;
    private int _nextFileId;
    private bool _disposed;

    private SqlSpillWorkspace(string directory) => _directory = directory;

    internal string DirectoryPath => _directory;

    internal static SqlSpillWorkspace Create(string rootDirectory)
    {
        string parent = Path.Combine(rootDirectory, DirectoryName);
        Directory.CreateDirectory(parent);
        string directory = Path.Combine(parent, $"query-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, OwnerMarkerFileName), "SonnetDB SQL spill v1");
        return new SqlSpillWorkspace(directory);
    }

    internal string CreateFilePath(string operatorName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int id = Interlocked.Increment(ref _nextFileId);
        return Path.Combine(_directory, $"{operatorName}-{id:D6}.bin");
    }

    internal static int CleanupStale(string rootDirectory)
    {
        string parent = Path.Combine(rootDirectory, DirectoryName);
        if (!Directory.Exists(parent))
            return 0;

        int removed = 0;
        foreach (string directory in Directory.EnumerateDirectories(parent, "query-*", SearchOption.TopDirectoryOnly))
        {
            if (!File.Exists(Path.Combine(directory, OwnerMarkerFileName)))
                continue;
            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (IOException)
            {
                // 另一个仍存活的实例可能持有文件；下一次启动重试。
            }
            catch (UnauthorizedAccessException)
            {
                // 权限恢复后由下一次启动重试，绝不扩大删除范围。
            }
        }
        return removed;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (File.Exists(Path.Combine(_directory, OwnerMarkerFileName)))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            SqlExecutionTelemetry.RecordSpillCleanupFailure();
        }
        catch (UnauthorizedAccessException)
        {
            SqlExecutionTelemetry.RecordSpillCleanupFailure();
        }
    }
}
