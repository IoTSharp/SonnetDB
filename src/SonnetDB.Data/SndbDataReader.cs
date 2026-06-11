using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    private bool _hasRow;
    private bool _closed;

    internal SndbDataReader(IExecutionResult result, CommandBehavior behavior, SndbConnection? connection)
    {
        _result = result;
        _behavior = behavior;
        _connection = connection;
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

    /// <inheritdoc />
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

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
        _hasRow = _result.ReadNextRow();
        return _hasRow;
    }

    /// <inheritdoc />
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed) return false;
        _hasRow = await _result.ReadNextRowAsync(cancellationToken).ConfigureAwait(false);
        return _hasRow;
    }

    /// <inheritdoc />
    public override void Close()
    {
        if (_closed) return;
        _closed = true;
        _result.Dispose();
        if ((_behavior & CommandBehavior.CloseConnection) != 0)
            _connection?.Close();
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
}
