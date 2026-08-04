using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using SonnetDB.Data.Internal;

namespace SonnetDB.Data;

/// <summary>
/// SonnetDB ADO.NET 数据读取器。基于内部 <see cref="IExecutionResult"/> 抽象，
/// 嵌入式模式下持有内存物化结果，远程模式下持有 ndjson 流式结果。
/// </summary>
public sealed class SndbDataReader : DbDataReader
{
    private readonly IExecutionResult _result;
    private readonly CommandBehavior _behavior;
    private readonly SndbConnection? _connection;
    private readonly int _commandTimeout;
    private readonly CancellationToken _commandCancellationToken;
    private readonly Action? _releaseExecutionLease;
    private CancellationTokenSource? _readTimeoutCancellation;
    private bool _hasRow;
    private bool _closed;

    internal SndbDataReader(
        IExecutionResult result,
        CommandBehavior behavior,
        SndbConnection? connection,
        int commandTimeout = 0,
        CancellationToken commandCancellationToken = default,
        Action? releaseExecutionLease = null)
    {
        _result = result;
        _behavior = behavior;
        _connection = connection;
        _commandTimeout = commandTimeout;
        _commandCancellationToken = commandCancellationToken;
        _releaseExecutionLease = releaseExecutionLease;
    }

    /// <inheritdoc />
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc />
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <inheritdoc />
    public override int Depth => 0;

    /// <inheritdoc />
    public override int FieldCount => _result.Columns.Count;

    /// <inheritdoc />
    public override bool HasRows => FieldCount > 0;

    /// <inheritdoc />
    public override bool IsClosed => _closed;

    /// <inheritdoc />
    public override int RecordsAffected => _result.RecordsAffected;

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var value = GetValue(ordinal);
        if (value is DBNull)
            throw new InvalidCastException($"列 {ordinal} 的值为 NULL。");
        if (value is not byte[] bytes)
            throw new InvalidCastException($"列 {ordinal} 不是二进制列。");
        if (dataOffset < 0 || dataOffset > bytes.Length)
            throw new ArgumentOutOfRangeException(nameof(dataOffset));
        if (buffer is null)
            return bytes.Length;
        if (bufferOffset < 0 || bufferOffset > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        int available = bytes.Length - (int)dataOffset;
        int writable = Math.Min(length, buffer.Length - bufferOffset);
        int count = Math.Min(available, writable);
        if (count <= 0)
            return 0;

        bytes.AsSpan((int)dataOffset, count).CopyTo(buffer.AsSpan(bufferOffset, count));
        return count;
    }

    /// <inheritdoc />
    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException("SonnetDB 不支持字符流读取。");

    /// <inheritdoc />
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal)
    {
        var v = GetValue(ordinal);
        return v switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.UtcDateTime,
            long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
            _ => Convert.ToDateTime(v, CultureInfo.InvariantCulture),
        };
    }

    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    /// <inheritdoc />
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
    public override Type GetFieldType(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return _result.GetFieldType(ordinal);
    }

    /// <inheritdoc />
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>
    /// 以指定类型读取当前行的列值；请求 <see cref="DateTimeOffset"/> 时统一按 UTC 时刻语义转换。
    /// </summary>
    /// <typeparam name="T">调用方期望的值类型。</typeparam>
    /// <param name="ordinal">从零开始的列序号。</param>
    /// <returns>转换后的列值。</returns>
    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        if (typeof(T) != typeof(DateTimeOffset))
            return (T)value;

        // 历史 DATETIME 列可能以整数或浮点 Unix 毫秒保存，统一恢复为 UTC 时刻。
        DateTimeOffset dateTimeOffset = value switch
        {
            DateTime dateTime => new(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            DateTimeOffset offset => offset.ToUniversalTime(),
            long unixMilliseconds => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds),
            double unixMilliseconds => FromNumericUnixMilliseconds(unixMilliseconds, ordinal),
            float unixMilliseconds => FromNumericUnixMilliseconds(unixMilliseconds, ordinal),
            decimal unixMilliseconds => FromNumericUnixMilliseconds(unixMilliseconds, ordinal),
            string timestamp => FromTextTimestamp(timestamp, ordinal),
            _ => throw new InvalidCastException($"列 {ordinal} 的值无法转换为 DateTimeOffset。"),
        };
        return (T)(object)dateTimeOffset;
    }

    /// <summary>解析远程协议返回的标准时间文本或数字型 Unix 毫秒文本。</summary>
    private static DateTimeOffset FromTextTimestamp(string value, int ordinal)
    {
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return FromNumericUnixMilliseconds(milliseconds, ordinal);
        }

        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp))
        {
            return timestamp;
        }

        throw new InvalidCastException($"列 {ordinal} 的文本不是有效的时间戳。");
    }

    /// <summary>
    /// 把存储层返回的数值型 Unix 毫秒转换为 UTC 时间，只接受有限且没有小数部分的值。
    /// </summary>
    private static DateTimeOffset FromNumericUnixMilliseconds<TNumber>(TNumber value, int ordinal)
        where TNumber : struct, IConvertible
    {
        try
        {
            var milliseconds = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (decimal.Truncate(milliseconds) != milliseconds
                || milliseconds < DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
                || milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            {
                throw new InvalidCastException($"列 {ordinal} 的数值不是有效的 Unix 毫秒时间戳。");
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(decimal.ToInt64(milliseconds));
        }
        catch (Exception exception) when (exception is OverflowException or FormatException)
        {
            throw new InvalidCastException($"列 {ordinal} 的数值不是有效的 Unix 毫秒时间戳。", exception);
        }
    }

    /// <inheritdoc />
    public override Guid GetGuid(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text),
            _ => (Guid)value
        };
    }

    /// <inheritdoc />
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string GetName(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return _result.Columns[ordinal];
    }

    /// <inheritdoc />
    public override DataTable GetSchemaTable()
    {
        var table = new DataTable("SchemaTable")
        {
            Locale = CultureInfo.InvariantCulture,
        };

        table.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        table.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        table.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        table.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(short));
        table.Columns.Add(SchemaTableColumn.NumericScale, typeof(short));
        table.Columns.Add(SchemaTableColumn.DataType, typeof(object));
        table.Columns.Add(SchemaTableColumn.ProviderType, typeof(int));
        table.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        table.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        table.Columns.Add("IsReadOnly", typeof(bool));
        table.Columns.Add("IsRowVersion", typeof(bool));
        table.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
        table.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        table.Columns.Add(SchemaTableOptionalColumn.IsAutoIncrement, typeof(bool));
        table.Columns.Add(SchemaTableOptionalColumn.BaseCatalogName, typeof(string));
        table.Columns.Add(SchemaTableColumn.BaseSchemaName, typeof(string));
        table.Columns.Add(SchemaTableColumn.BaseTableName, typeof(string));
        table.Columns.Add(SchemaTableColumn.BaseColumnName, typeof(string));

        for (int ordinal = 0; ordinal < FieldCount; ordinal++)
        {
            var fieldType = GetFieldType(ordinal);
            var row = table.NewRow();
            row[SchemaTableColumn.ColumnName] = GetName(ordinal);
            row[SchemaTableColumn.ColumnOrdinal] = ordinal;
            row[SchemaTableColumn.ColumnSize] = GetColumnSize(fieldType);
            row[SchemaTableColumn.NumericPrecision] = GetNumericPrecision(fieldType);
            row[SchemaTableColumn.NumericScale] = GetNumericScale(fieldType);
            row[SchemaTableColumn.DataType] = fieldType;
            row[SchemaTableColumn.ProviderType] = (int)GetProviderType(fieldType);
            row[SchemaTableColumn.IsLong] = fieldType == typeof(byte[]) || fieldType == typeof(string);
            row[SchemaTableColumn.AllowDBNull] = true;
            row["IsReadOnly"] = false;
            row["IsRowVersion"] = false;
            row[SchemaTableColumn.IsUnique] = false;
            row[SchemaTableColumn.IsKey] = false;
            row[SchemaTableOptionalColumn.IsAutoIncrement] = false;
            row[SchemaTableOptionalColumn.BaseCatalogName] = _connection?.Database ?? string.Empty;
            row[SchemaTableColumn.BaseSchemaName] = string.Empty;
            row[SchemaTableColumn.BaseTableName] = string.Empty;
            row[SchemaTableColumn.BaseColumnName] = GetName(ordinal);
            table.Rows.Add(row);
        }

        return table;
    }

    /// <inheritdoc />
    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (int i = 0; i < _result.Columns.Count; i++)
            if (string.Equals(_result.Columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        throw new IndexOutOfRangeException($"未找到列 '{name}'。");
    }

    /// <inheritdoc />
    public override string GetString(int ordinal)
    {
        var v = GetValue(ordinal);
        return v switch
        {
            string s => s,
            null => throw new InvalidCastException($"列 {ordinal} 的值为 NULL。"),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    /// <inheritdoc />
    public override object GetValue(int ordinal)
    {
        EnsureOnRow();
        ValidateOrdinal(ordinal);
        return _result.GetValue(ordinal) ?? DBNull.Value;
    }

    /// <inheritdoc />
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        EnsureOnRow();
        int n = Math.Min(values.Length, _result.Columns.Count);
        for (int i = 0; i < n; i++)
            values[i] = _result.GetValue(i) ?? DBNull.Value;
        return n;
    }

    /// <inheritdoc />
    public override bool IsDBNull(int ordinal)
    {
        EnsureOnRow();
        ValidateOrdinal(ordinal);
        return _result.GetValue(ordinal) is null;
    }

    /// <inheritdoc />
    public override bool NextResult() => false;

    /// <inheritdoc />
    public override bool Read()
    {
        if (_closed) return false;
        if (_commandTimeout <= 0 && !_commandCancellationToken.CanBeCanceled)
        {
            // 既无计时器也无命令取消令牌时保持原同步读取快路径。
            _hasRow = _result.ReadNextRow();
            return _hasRow;
        }

        return ReadWithTimeoutAsync(CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    /// <inheritdoc />
    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        => ReadWithTimeoutAsync(cancellationToken);

    /// <summary>
    /// 为单次行读取建立独立超时窗口，并与本次 ReadAsync 的调用方令牌组合。
    /// </summary>
    private async Task<bool> ReadWithTimeoutAsync(CancellationToken callerCancellationToken)
    {
        callerCancellationToken.ThrowIfCancellationRequested();
        if (_closed) return false;

        if (_commandTimeout <= 0)
        {
            if (!_commandCancellationToken.CanBeCanceled)
            {
                _hasRow = await _result.ReadNextRowAsync(callerCancellationToken).ConfigureAwait(false);
                return _hasRow;
            }

            if (!callerCancellationToken.CanBeCanceled)
            {
                try
                {
                    _hasRow = await _result.ReadNextRowAsync(_commandCancellationToken).ConfigureAwait(false);
                    return _hasRow;
                }
                catch (OperationCanceledException exception) when (_commandCancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(exception.Message, exception, _commandCancellationToken);
                }
            }

            using var commandReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellationToken,
                _commandCancellationToken);
            try
            {
                _hasRow = await _result.ReadNextRowAsync(commandReadCancellation.Token).ConfigureAwait(false);
                return _hasRow;
            }
            catch (OperationCanceledException exception) when (callerCancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(exception.Message, exception, callerCancellationToken);
            }
            catch (OperationCanceledException exception) when (_commandCancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(exception.Message, exception, _commandCancellationToken);
            }
        }

        var readCancellation = GetReadTimeoutCancellation();
        readCancellation.CancelAfter(TimeSpan.FromSeconds(_commandTimeout));
        // 回调只负责取消内部源，不依赖执行上下文；静态回调同时避免闭包分配。
        var callerRegistration = callerCancellationToken.CanBeCanceled
            ? callerCancellationToken.UnsafeRegister(
                static state => ((CancellationTokenSource)state!).Cancel(),
                readCancellation)
            : default;
        var commandRegistration = _commandCancellationToken.CanBeCanceled
            ? _commandCancellationToken.UnsafeRegister(
                static state => ((CancellationTokenSource)state!).Cancel(),
                readCancellation)
            : default;

        try
        {
            _hasRow = await _result.ReadNextRowAsync(readCancellation.Token).ConfigureAwait(false);
            return _hasRow;
        }
        catch (OperationCanceledException exception) when (callerCancellationToken.IsCancellationRequested)
        {
            // 调用方取消优先于同一时刻发生的读取超时，并保留原始调用方令牌。
            throw new OperationCanceledException(exception.Message, exception, callerCancellationToken);
        }
        catch (OperationCanceledException exception) when (_commandCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(exception.Message, exception, _commandCancellationToken);
        }
        catch (OperationCanceledException exception) when (readCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"SonnetDB 数据读取超过 CommandTimeout={_commandTimeout} 秒。",
                exception);
        }
        finally
        {
            // 必须先断开调用方令牌，避免旧回调在 TryReset 后取消已复用的源。
            callerRegistration.Dispose();
            commandRegistration.Dispose();
            ResetReadTimeoutCancellation(readCancellation);
        }
    }

    /// <summary>
    /// 获取 Reader 级可复用超时源；仅首次读取或上次已取消时创建新实例。
    /// </summary>
    private CancellationTokenSource GetReadTimeoutCancellation()
    {
        var cancellation = _readTimeoutCancellation;
        if (cancellation is not null && !cancellation.IsCancellationRequested)
            return cancellation;

        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        _readTimeoutCancellation = cancellation;
        return cancellation;
    }

    /// <summary>
    /// 读取完成后停止定时器并清理临时注册；已取消的源立即释放。
    /// </summary>
    private void ResetReadTimeoutCancellation(CancellationTokenSource cancellation)
    {
        if (cancellation.TryReset())
            return;

        cancellation.Dispose();
        if (ReferenceEquals(_readTimeoutCancellation, cancellation))
            _readTimeoutCancellation = null;
    }

    /// <inheritdoc />
    public override void Close()
    {
        if (_closed) return;
        _closed = true;
        _readTimeoutCancellation?.Dispose();
        _readTimeoutCancellation = null;
        ExceptionDispatchInfo? firstFailure = null;
        try
        {
            _result.Dispose();
        }
        catch (Exception exception)
        {
            firstFailure = ExceptionDispatchInfo.Capture(exception);
        }

        // 三项清理全部执行，并保留最先发生的异常，避免 CloseConnection 覆盖 Reader 根因。
        try
        {
            _releaseExecutionLease?.Invoke();
        }
        catch (Exception exception)
        {
            firstFailure ??= ExceptionDispatchInfo.Capture(exception);
        }

        if ((_behavior & CommandBehavior.CloseConnection) != 0)
        {
            try
            {
                _connection?.Close();
            }
            catch (Exception exception)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        firstFailure?.Throw();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }

    private void EnsureOnRow()
    {
        if (_closed) throw new InvalidOperationException("Reader 已关闭。");
        if (!_hasRow) throw new InvalidOperationException("当前未定位到任何行；请先调用 Read()。");
    }

    private void ValidateOrdinal(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _result.Columns.Count)
            throw new IndexOutOfRangeException($"列序号 {ordinal} 越界（列数 {_result.Columns.Count}）。");
    }

    private static int GetColumnSize(Type fieldType)
        => fieldType == typeof(string) || fieldType == typeof(byte[])
            ? int.MaxValue
            : fieldType == typeof(Guid)
                ? 16
                : -1;

    private static short GetNumericPrecision(Type fieldType)
        => fieldType == typeof(byte)
            ? (short)3
            : fieldType == typeof(short)
                ? (short)5
                : fieldType == typeof(int)
                    ? (short)10
                    : fieldType == typeof(long)
                        ? (short)19
                        : fieldType == typeof(float)
                            ? (short)7
                            : fieldType == typeof(double)
                                ? (short)15
                                : fieldType == typeof(decimal)
                                    ? (short)29
                                    : (short)0;

    private static short GetNumericScale(Type fieldType)
        => fieldType == typeof(float) || fieldType == typeof(double) || fieldType == typeof(decimal)
            ? (short)15
            : (short)0;

    private static DbType GetProviderType(Type fieldType)
    {
        if (fieldType == typeof(string)) return DbType.String;
        if (fieldType == typeof(bool)) return DbType.Boolean;
        if (fieldType == typeof(byte)) return DbType.Byte;
        if (fieldType == typeof(short)) return DbType.Int16;
        if (fieldType == typeof(int)) return DbType.Int32;
        if (fieldType == typeof(long)) return DbType.Int64;
        if (fieldType == typeof(float)) return DbType.Single;
        if (fieldType == typeof(double)) return DbType.Double;
        if (fieldType == typeof(decimal)) return DbType.Decimal;
        if (fieldType == typeof(DateTime)) return DbType.DateTime;
        if (fieldType == typeof(DateTimeOffset)) return DbType.DateTimeOffset;
        if (fieldType == typeof(Guid)) return DbType.Guid;
        if (fieldType == typeof(byte[])) return DbType.Binary;
        return DbType.Object;
    }
}
