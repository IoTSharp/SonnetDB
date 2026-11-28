using SonnetDB.Diagnostics;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal sealed record OrderedSelectStream(
    IReadOnlyList<string> Columns,
    IReadOnlyList<SelectColumnInfo>? ColumnInfo,
    IEnumerable<IReadOnlyList<object?>> Rows,
    IDisposable? ReadView = null)
{
    internal IReadOnlyList<TableColumn>? ColumnSchema { get; init; }
}

/// <summary>具有固定列元数据的单次 SQL 结果流。调用方必须释放此对象，包括提前停止读取时。</summary>
public sealed class SqlStreamingSelectResult : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SqlQueryResources _resources;
    private readonly CancellationTokenSource? _deadline;
    private IDisposable? _readView;
    private readonly IDisposable _queryActivity;
    private IEnumerator<IReadOnlyList<object?>>? _rows;
    private IReadOnlyList<object?>? _current;
    private bool _disposed;

    internal SqlStreamingSelectResult(
        OrderedSelectStream stream,
        SqlQueryResources resources,
        CancellationTokenSource? deadline)
    {
        _resources = resources;
        _deadline = deadline;
        _readView = stream.ReadView;
        _rows = stream.Rows.GetEnumerator();
        _queryActivity = QueryActivityTracker.Enter();
        CancellationToken = resources.CancellationToken;
        Columns = stream.Columns;
        ColumnInfo = stream.ColumnInfo;
        ColumnSchema = stream.ColumnSchema;
    }

    /// <summary>输出列名称。</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>输出列的声明信息。</summary>
    public IReadOnlyList<SelectColumnInfo>? ColumnInfo { get; }

    /// <summary>适用时返回输出列的关系 schema。</summary>
    public IReadOnlyList<TableColumn>? ColumnSchema { get; }

    /// <summary>贯穿 SQL 读取和传输的语句取消令牌。</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>最近一次成功读取返回的行。</summary>
    public IReadOnlyList<object?> Current => _current ?? throw new InvalidOperationException("SQL 结果流没有当前行。");

    /// <summary>读取下一行。失败或取消时关闭结果流并释放读视图。</summary>
    /// <param name="cancellationToken">仅本次读取的附加取消令牌。</param>
    /// <returns>存在下一行时为 true。</returns>
    public bool MoveNext(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current = null;
            if (_rows is null)
                return false;

            using var linked = cancellationToken.CanBeCanceled && CancellationToken.CanBeCanceled
                && cancellationToken != CancellationToken
                ? CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, cancellationToken)
                : null;
            CancellationToken effective = linked?.Token
                ?? (cancellationToken.CanBeCanceled ? cancellationToken : CancellationToken);
            using var activation = _resources.Activate(effective);
            using IDisposable? telemetry = _resources.Options.Metrics is { } metrics
                ? SqlExecutionTelemetry.Enter(metrics)
                : null;
            try
            {
                effective.ThrowIfCancellationRequested();
                bool hasRow = _rows.MoveNext();
                effective.ThrowIfCancellationRequested();
                if (hasRow)
                {
                    _current = _rows.Current;
                    return true;
                }

                ReleaseRows();
                return false;
            }
            catch
            {
                Dispose();
                throw;
            }
        }
    }

    /// <summary>释放尚未读完的行、读视图和语句期限。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _current = null;
            using var deadline = _deadline;
            using var activation = _resources.Activate();
            using IDisposable? telemetry = _resources.Options.Metrics is { } metrics
                ? SqlExecutionTelemetry.Enter(metrics)
                : null;
            ReleaseRows();
        }
    }

    private void ReleaseRows()
    {
        if (_rows is not { } rows)
            return;
        _rows = null;
        using var activity = _queryActivity;
        using var resources = _resources;
        using var readView = _readView;
        _readView = null;
        rows.Dispose();
    }
}
