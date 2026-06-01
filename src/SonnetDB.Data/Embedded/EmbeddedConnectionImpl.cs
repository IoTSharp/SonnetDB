using System.Data;
using System.Data.Common;
using SonnetDB.Data.Internal;
using SonnetDB.Engine;
using SonnetDB.Ingest;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Data.Embedded;

/// <summary>
/// 嵌入式连接实现：直接打开本地目录上的 <see cref="Tsdb"/>，并在进程内共享。
/// </summary>
internal sealed class EmbeddedConnectionImpl : IConnectionImpl
{
    private readonly SndbConnectionStringBuilder _builder;
    private Tsdb? _tsdb;
    private ConnectionState _state = ConnectionState.Closed;

    public EmbeddedConnectionImpl(SndbConnectionStringBuilder builder)
    {
        _builder = builder;
    }

    public string DataSource => NormalizeDataSource(_builder.DataSource);

    public string Database => DataSource;

    public string ServerVersion => typeof(Tsdb).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    public ConnectionState State => _state;

    internal Tsdb? Tsdb => _tsdb;

    public void Open()
    {
        if (_state == ConnectionState.Open) return;
        var path = NormalizeDataSource(_builder.DataSource);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("ConnectionString 缺少 'Data Source'。");

        _tsdb = SharedSndbRegistry.Acquire(new TsdbOptions { RootDirectory = path });
        _state = ConnectionState.Open;
    }

    public void Close()
    {
        if (_state == ConnectionState.Closed) return;
        var t = _tsdb;
        _tsdb = null;
        _state = ConnectionState.Closed;
        if (t != null)
            SharedSndbRegistry.Release(t);
    }

    public void Dispose() => Close();

    public IExecutionResult Execute(string sql, SndbParameterCollection parameters, CommandBehavior behavior)
    {
        if (_tsdb is null || _state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");

        // 客户端拦截 SQL Console 风格元命令：USE / SELECT current_database() / SHOW CURRENT_DATABASE。
        // 嵌入式模式下 "database" 等价于 Data Source 路径，无法切换；USE 视为不支持。
        var meta = SqlMetaCommand.TryParse(sql, out var requestedDb);
        if (meta == MetaKind.CurrentDatabase)
        {
            return MaterializedExecutionResult.FromSelect(SqlMetaCommand.BuildCurrentDatabaseResult(Database));
        }
        if (meta == MetaKind.UseDatabase)
        {
            throw new NotSupportedException(
                $"嵌入式模式下不支持 USE：当前连接已绑定到 Data Source = '{DataSource}'，" +
                $"切换到 '{requestedDb}' 请关闭连接后用新的 ConnectionString 重新打开。");
        }

        var statement = SqlParser.Parse(sql);
        var result = SqlExecutor.ExecuteStatement(_tsdb, statement);
        return result switch
        {
            SelectExecutionResult select => MaterializedExecutionResult.FromSelect(select),
            InsertExecutionResult insert => MaterializedExecutionResult.NonQuery(insert.RowsInserted),
            DeleteExecutionResult delete => MaterializedExecutionResult.NonQuery(delete.TombstonesAdded),
            RowsAffectedExecutionResult affected => MaterializedExecutionResult.NonQuery(affected.RowsAffected),
            null => MaterializedExecutionResult.NonQuery(0),
            _ => MaterializedExecutionResult.NonQuery(0),
        };
    }

    public IExecutionResult ExecuteBulk(string commandText, SndbParameterCollection parameters)
    {
        if (_tsdb is null || _state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");
        ArgumentNullException.ThrowIfNull(commandText);

        // 参数：measurement / onerror / flush
        string? measurementOverride = TryGetParam(parameters, "measurement");
        BulkFlushMode flushMode = ParseFlushMode(TryGetParam(parameters, "flush"));
        var errorPolicy = string.Equals(TryGetParam(parameters, "onerror"), "skip", StringComparison.OrdinalIgnoreCase)
            ? BulkErrorPolicy.Skip
            : BulkErrorPolicy.FailFast;

        // 1) 嗅探格式 + 切首行 measurement 前缀
        var format = BulkPayloadDetector.DetectWithPrefix(commandText, out var measurementFromPrefix, out var payload);
        var measurement = measurementOverride ?? measurementFromPrefix;

        // 2) 构造 reader
        IPointReader reader = format switch
        {
            BulkPayloadFormat.LineProtocol => new LineProtocolReader(payload, measurementOverride: measurement),
            BulkPayloadFormat.Json => new JsonPointsReader(payload, measurementOverride: measurement),
            BulkPayloadFormat.BulkValues => SchemaBoundBulkValuesReader.Create(_tsdb, payload.ToString(), measurement),
            _ => throw new BulkIngestException($"未知协议格式 {format}。"),
        };

        try
        {
            var result = BulkIngestor.Ingest(_tsdb, reader, errorPolicy, flushMode);
            return MaterializedExecutionResult.NonQuery(result.Written);
        }
        finally
        {
            (reader as IDisposable)?.Dispose();
        }
    }

    private static string? TryGetParam(SndbParameterCollection parameters, string name)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            if (string.Equals(p.ParameterName?.TrimStart('@', ':'), name, StringComparison.OrdinalIgnoreCase))
                return p.Value?.ToString();
        }
        return null;
    }

    /// <summary>
    /// 解析 <c>flush</c> 参数为 <see cref="BulkFlushMode"/>（PR #48）。
    /// 接受 <c>"async"</c>（异步信号）/ <c>"true"|"sync"|"yes"|"1"</c>（同步 FlushNow）/
    /// 其它值（含 null、空、<c>"false"</c>）一律为 <see cref="BulkFlushMode.None"/>。
    /// </summary>
    internal static BulkFlushMode ParseFlushMode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return BulkFlushMode.None;
        if (string.Equals(value, "async", StringComparison.OrdinalIgnoreCase))
            return BulkFlushMode.Async;
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "sync", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || value == "1")
            return BulkFlushMode.Sync;
        return BulkFlushMode.None;
    }

    /// <summary>
    /// 兼容 <c>sonnetdb://path</c> 形式：去掉 scheme 前缀，得到真实文件系统路径。
    /// </summary>
    private static string NormalizeDataSource(string ds)
    {
        if (string.IsNullOrWhiteSpace(ds)) return ds;
        const string prefix = "sonnetdb://";
        if (ds.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return ds[prefix.Length..];
        return ds;
    }
}
