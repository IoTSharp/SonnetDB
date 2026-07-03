using System.Data;
using System.Data.Common;
using SonnetDB.Data.Internal;

namespace SonnetDB.Data;

/// <summary>
/// SonnetDB ADO.NET 命令对象。把 SQL 与参数交给当前连接的内部实现执行（嵌入式或远程）。
/// </summary>
/// <remarks>
/// <para>
/// 参数支持位置 <c>?</c> 与命名 <c>@name</c> / <c>:name</c> 占位符（#213）。嵌入式模式下参数值
/// 直接绑定进已解析的 AST（值绑定而非字符串拼接，从根上防注入，并可复用解析缓存）；远程模式因
/// 线协议只接受 SQL 字符串，仍在客户端把命名占位符按安全字面量替换后发送，保留既有类型序列化保真度。
/// </para>
/// <para>
/// <see cref="ExecuteNonQuery"/> 返回值约定：INSERT 返回写入行数；DELETE 返回新增墓碑数；
/// CREATE MEASUREMENT 返回 0；SELECT 返回 -1（与 <see cref="DbCommand"/> 标准一致）。
/// </para>
/// </remarks>
public sealed class SndbCommand : DbCommand
{
    private SndbConnection? _connection;
    private SndbTransaction? _transaction;
    private string _commandText = string.Empty;
    private CommandType _commandType = CommandType.Text;
    private readonly SndbParameterCollection _parameters = new();

    /// <summary>构造一个未关联连接的命令。</summary>
    public SndbCommand() { }

    /// <summary>用 SQL 文本与连接构造命令。</summary>
    public SndbCommand(string commandText, SndbConnection? connection = null)
    {
        _commandText = commandText ?? string.Empty;
        _connection = connection;
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override int CommandTimeout { get; set; }

    /// <inheritdoc />
    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (value != CommandType.Text && value != CommandType.TableDirect)
                throw new NotSupportedException(
                    "SonnetDB 仅支持 CommandType.Text（普通 SQL）与 CommandType.TableDirect（批量入库快路径）。");
            _commandType = value;
        }
    }

    /// <inheritdoc />
    public override bool DesignTimeVisible { get; set; }

    /// <inheritdoc />
    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    /// <inheritdoc />
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value as SndbConnection
            ?? (value is null ? null : throw new InvalidCastException("Connection 必须是 SndbConnection。"));
    }

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <inheritdoc />
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            _transaction = value switch
            {
                null => null,
                SndbTransaction transaction => transaction,
                _ => throw new InvalidCastException("Transaction 必须是 SndbTransaction。"),
            };
        }
    }

    /// <summary>强类型参数集合。</summary>
    public new SndbParameterCollection Parameters => _parameters;

    /// <summary>强类型连接。</summary>
    public new SndbConnection? Connection
    {
        get => _connection;
        set => _connection = value;
    }

    /// <summary>强类型事务。</summary>
    public new SndbTransaction? Transaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    /// <inheritdoc />
    public override void Cancel() { /* no-op：单线程同步执行，不可取消 */ }

    /// <inheritdoc />
    public override void Prepare() { /* no-op */ }

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter() => new SndbParameter();

    /// <inheritdoc />
    public override int ExecuteNonQuery()
    {
        using var result = ExecuteCore(CommandBehavior.Default);
        // 对于 SELECT，把游标走完以保持语义一致（一般上层不会调用）
        if (result.RecordsAffected == -1)
        {
            while (result.ReadNextRow()) { }
        }
        return result.RecordsAffected;
    }

    /// <inheritdoc />
    public override object? ExecuteScalar()
    {
        using var result = ExecuteCore(CommandBehavior.Default);
        if (result.Columns.Count == 0)
            return null;
        if (!result.ReadNextRow())
            return null;
        var v = result.GetValue(0);
        // 把后续行消费掉
        while (result.ReadNextRow()) { }
        return v;
    }

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var result = ExecuteCore(behavior);
        return new SndbDataReader(result, behavior, _connection);
    }

    private IExecutionResult ExecuteCore(CommandBehavior behavior)
        => ExecuteCoreAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        using var result = await ExecuteCoreAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        if (result.RecordsAffected == -1)
        {
            while (result.ReadNextRow())
                cancellationToken.ThrowIfCancellationRequested();
        }

        return result.RecordsAffected;
    }

    /// <inheritdoc />
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        using var result = await ExecuteCoreAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        if (result.Columns.Count == 0)
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.ReadNextRow())
            return null;
        var v = result.GetValue(0);
        while (result.ReadNextRow())
            cancellationToken.ThrowIfCancellationRequested();
        return v;
    }

    /// <inheritdoc />
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(behavior, cancellationToken).ConfigureAwait(false);
        return new SndbDataReader(result, behavior, _connection);
    }

    private async Task<IExecutionResult> ExecuteCoreAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connection is null)
            throw new InvalidOperationException("Command 没有关联 Connection。");
        if (string.IsNullOrWhiteSpace(_commandText))
            throw new InvalidOperationException("CommandText 为空。");

        var impl = _connection.GetOpenImpl();
        var transactionState = _connection.GetTransactionStateForCommand(_transaction);
        if (_commandType == CommandType.TableDirect)
        {
            // 批量入库快路径：CommandText 即 payload（含可选首行 measurement 前缀），
            // 不做 ParameterBinder 的 SQL 字面量替换。
            return await impl.ExecuteBulkAsync(_commandText, _parameters, transactionState, cancellationToken)
                .ConfigureAwait(false);
        }

        // #213：不再在此处做字符串字面量替换。原始 SQL + 参数下沉给具体连接实现：
        // 嵌入式走 Core AST 值绑定（防注入 + 复用解析缓存）；远程仍在其 impl 内按需绑定。
        if (IsSqlTransactionControl(_commandText))
            throw new InvalidOperationException("请通过 SndbConnection.BeginTransaction()/SndbTransaction 控制事务。");
        return await impl.ExecuteAsync(_commandText, _parameters, behavior, transactionState, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsSqlTransactionControl(string sql)
    {
        var text = sql.Trim();
        while (text.EndsWith(';'))
            text = text[..^1].TrimEnd();

        return text.Equals("BEGIN", StringComparison.OrdinalIgnoreCase)
            || text.Equals("BEGIN TRANSACTION", StringComparison.OrdinalIgnoreCase)
            || text.Equals("COMMIT", StringComparison.OrdinalIgnoreCase)
            || text.Equals("COMMIT TRANSACTION", StringComparison.OrdinalIgnoreCase)
            || text.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase)
            || text.Equals("ROLLBACK TRANSACTION", StringComparison.OrdinalIgnoreCase);
    }
}
