using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SonnetDB.Data;
using SonnetDB.Data.Kv;
using SonnetDB.Model;

namespace SonnetDB.Native;

internal enum NativeValueType
{
    Null = 0,
    Int64 = 1,
    Float64 = 2,
    Boolean = 3,
    Text = 4,
}

internal sealed class NativeConnection : IDisposable
{
    private SndbConnection? _connection;

    public NativeConnection(string connectionStringOrDataSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringOrDataSource);
        ConnectionString = BuildConnectionString(connectionStringOrDataSource);
        _connection = new SndbConnection(ConnectionString);
        _connection.Open();
    }

    public string ConnectionString { get; }

    public NativeResult Execute(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var connection = _connection ?? throw new ObjectDisposedException(nameof(NativeConnection));
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        return NativeResult.From(reader);
    }

    public NativeResult ExecuteBulk(NativeBulk bulk)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        var connection = _connection ?? throw new ObjectDisposedException(nameof(NativeConnection));
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.TableDirect;
        command.CommandText = bulk.Payload;
        AddBulkParameter(command, "measurement", bulk.Measurement);
        AddBulkParameter(command, "onerror", bulk.OnError);
        AddBulkParameter(command, "flush", bulk.Flush);
        return NativeResult.NonQuery(command.ExecuteNonQuery());
    }

    public void Flush()
    {
        var connection = _connection ?? throw new ObjectDisposedException(nameof(NativeConnection));
        if (connection.UnderlyingTsdb is not { } tsdb)
            throw new NotSupportedException("sonnetdb_flush is only available for embedded connections.");

        tsdb.FlushNow();
    }

    public void Dispose()
    {
        var connection = _connection;
        _connection = null;
        connection?.Dispose();
    }

    private static string BuildConnectionString(string connectionStringOrDataSource)
    {
        if (LooksLikeConnectionString(connectionStringOrDataSource))
            return connectionStringOrDataSource;

        var builder = new SndbConnectionStringBuilder
        {
            DataSource = connectionStringOrDataSource,
        };
        return builder.ConnectionString;
    }

    private static bool LooksLikeConnectionString(string value)
    {
        if (!value.Contains('=', StringComparison.Ordinal))
            return false;

        try
        {
            var builder = new SndbConnectionStringBuilder(value);
            return builder.ContainsKey("Data Source")
                || builder.ContainsKey("Mode")
                || builder.ContainsKey("Database")
                || builder.ContainsKey("Token")
                || builder.ContainsKey("Timeout");
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void AddBulkParameter(SndbCommand command, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        command.Parameters.AddWithValue(name, value);
    }
}

internal sealed class NativeResult : IDisposable
{
    private readonly IReadOnlyList<string> _columns;
    private readonly IReadOnlyList<IReadOnlyList<object?>> _rows;
    private readonly Dictionary<int, IntPtr> _columnNamePointers = new();
    private readonly Dictionary<int, IntPtr> _valueTextPointers = new();
    private int _rowIndex = -1;
    private bool _disposed;

    private NativeResult(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int recordsAffected)
    {
        _columns = columns;
        _rows = rows;
        RecordsAffected = recordsAffected;
    }

    public int RecordsAffected { get; }

    public int ColumnCount
    {
        get
        {
            ThrowIfDisposed();
            return _columns.Count;
        }
    }

    public static NativeResult From(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var columns = new string[reader.FieldCount];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = reader.GetName(i);

        var rows = new List<IReadOnlyList<object?>>();
        while (reader.Read())
        {
            var values = new object[columns.Length];
            reader.GetValues(values);
            var row = new object?[columns.Length];
            for (int i = 0; i < values.Length; i++)
            {
                row[i] = values[i] is DBNull ? null : values[i];
            }

            rows.Add(row);
        }

        return new NativeResult(columns, rows, reader.RecordsAffected);
    }

    public IntPtr GetColumnName(int ordinal)
    {
        ThrowIfDisposed();
        ValidateColumnOrdinal(ordinal);

        if (_columnNamePointers.TryGetValue(ordinal, out var ptr))
            return ptr;

        ptr = Marshal.StringToCoTaskMemUTF8(_columns[ordinal]);
        _columnNamePointers.Add(ordinal, ptr);
        return ptr;
    }

    public int MoveNext()
    {
        ThrowIfDisposed();
        ReleaseValueTextPointers();

        if (_rowIndex + 1 >= _rows.Count)
            return 0;

        _rowIndex++;
        return 1;
    }

    public NativeValueType GetValueType(int ordinal)
    {
        var value = GetCurrentValue(ordinal);
        return value switch
        {
            null => NativeValueType.Null,
            byte or sbyte or short or ushort or int or uint or long => NativeValueType.Int64,
            ulong => NativeValueType.Int64,
            float or double or decimal => NativeValueType.Float64,
            bool => NativeValueType.Boolean,
            _ => NativeValueType.Text,
        };
    }

    public long GetInt64(int ordinal)
    {
        var value = GetCurrentValue(ordinal);
        return value switch
        {
            byte v => v,
            sbyte v => v,
            short v => v,
            ushort v => v,
            int v => v,
            uint v => v,
            long v => v,
            ulong v when v <= long.MaxValue => (long)v,
            _ => throw new InvalidOperationException($"Column {ordinal} is not an int64 value."),
        };
    }

    public double GetDouble(int ordinal)
    {
        var value = GetCurrentValue(ordinal);
        return value switch
        {
            byte v => v,
            sbyte v => v,
            short v => v,
            ushort v => v,
            int v => v,
            uint v => v,
            long v => v,
            ulong v => v,
            float v => v,
            double v => v,
            decimal v => (double)v,
            _ => throw new InvalidOperationException($"Column {ordinal} is not a double value."),
        };
    }

    public int GetBoolean(int ordinal)
    {
        var value = GetCurrentValue(ordinal);
        return value switch
        {
            bool v => v ? 1 : 0,
            _ => throw new InvalidOperationException($"Column {ordinal} is not a boolean value."),
        };
    }

    public IntPtr GetText(int ordinal)
    {
        var value = GetCurrentValue(ordinal);
        if (value is null)
            return IntPtr.Zero;

        if (_valueTextPointers.TryGetValue(ordinal, out var ptr))
            return ptr;

        ptr = Marshal.StringToCoTaskMemUTF8(FormatText(value));
        _valueTextPointers.Add(ordinal, ptr);
        return ptr;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ReleaseValueTextPointers();
        foreach (var ptr in _columnNamePointers.Values)
            Marshal.FreeCoTaskMem(ptr);
        _columnNamePointers.Clear();
        _disposed = true;
    }

    public static NativeResult NonQuery(int recordsAffected)
        => new(Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>(), recordsAffected);

    private object? GetCurrentValue(int ordinal)
    {
        ThrowIfDisposed();
        ValidateColumnOrdinal(ordinal);
        if (_rowIndex < 0 || _rowIndex >= _rows.Count)
            throw new InvalidOperationException("Result is not positioned on a row.");
        return _rows[_rowIndex][ordinal];
    }

    private void ValidateColumnOrdinal(int ordinal)
    {
        if ((uint)ordinal >= (uint)_columns.Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Column ordinal is out of range.");
    }

    private void ReleaseValueTextPointers()
    {
        foreach (var ptr in _valueTextPointers.Values)
            Marshal.FreeCoTaskMem(ptr);
        _valueTextPointers.Clear();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string FormatText(object value)
        => value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            double d => d.ToString("G17", CultureInfo.InvariantCulture),
            float f => f.ToString("G9", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToBase64String(bytes),
            byte or sbyte or short or ushort or int or uint or long or ulong
                => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            GeoPoint geo => string.Create(
                CultureInfo.InvariantCulture,
                $"POINT({geo.Lat:G17},{geo.Lon:G17})"),
            float[] vector => FormatVector(vector),
            _ => value.ToString() ?? string.Empty,
        };

    private static string FormatVector(float[] vector)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < vector.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(vector[i].ToString("G9", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }
}

internal sealed class NativeBulk
{
    public NativeBulk(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        Payload = payload;
    }

    public string Payload { get; }

    public string? Measurement { get; private set; }

    public string? OnError { get; private set; }

    public string? Flush { get; private set; }

    public void SetMeasurement(string? measurement)
    {
        Measurement = NormalizeOptional(measurement);
    }

    public void SetOnError(string? onError)
    {
        OnError = NormalizeOptional(onError);
    }

    public void SetFlush(string? flush)
    {
        Flush = NormalizeOptional(flush);
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

internal sealed class NativeKv : IDisposable
{
    private readonly SndbKvClient _client;
    private readonly string _keyspace;
    private readonly string _namespace;
    private bool _disposed;

    public NativeKv(NativeConnection connection, string keyspace, string? @namespace)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);

        _client = new SndbKvClient(connection.ConnectionString);
        _keyspace = keyspace;
        _namespace = @namespace ?? string.Empty;
    }

    public NativeKvEntry? Get(string key)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        var entry = _client.GetAsync(_keyspace, _namespace, key).GetAwaiter().GetResult();
        return entry is null ? null : NativeKvEntry.From(entry);
    }

    public long Set(string key, byte[] value, DateTimeOffset? expiresAtUtc)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return _client.SetAsync(_keyspace, _namespace, key, value, expiresAtUtc).GetAwaiter().GetResult();
    }

    public bool Delete(string key)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        return _client.RemoveAsync(_keyspace, _namespace, key).GetAwaiter().GetResult();
    }

    public NativeKvScan ScanPrefix(string prefix, int limit)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(prefix);
        var entries = _client.ScanPrefixAsync(
            _keyspace,
            _namespace,
            prefix,
            limit <= 0 ? null : limit).GetAwaiter().GetResult();
        return new NativeKvScan(entries.Select(NativeKvEntry.From).ToArray());
    }

    public SndbKvTtlResult GetTimeToLive(string key)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        return _client.GetTimeToLiveAsync(_keyspace, _namespace, key).GetAwaiter().GetResult();
    }

    public (long Value, long Version) Increment(string key, long delta)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        return _client.IncrementAsync(_keyspace, _namespace, key, delta).GetAwaiter().GetResult();
    }

    public SndbKvCasResult CompareAndSet(string key, long expectedVersion, byte[] value, DateTimeOffset? expiresAtUtc)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return _client.CompareAndSetAsync(_keyspace, _namespace, key, expectedVersion, value, expiresAtUtc)
            .GetAwaiter()
            .GetResult();
    }

    public bool Expire(string key, DateTimeOffset expiresAtUtc)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        return _client.ExpireAsync(_keyspace, _namespace, key, expiresAtUtc).GetAwaiter().GetResult();
    }

    public bool Persist(string key)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        return _client.PersistAsync(_keyspace, _namespace, key).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class NativeKvEntry : IDisposable
{
    private readonly Dictionary<int, IntPtr> _textPointers = new();
    private bool _disposed;

    private NativeKvEntry(string key, byte[] value, long version, DateTimeOffset? expiresAtUtc)
    {
        Key = key;
        Value = value;
        Version = version;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string Key { get; }

    public byte[] Value { get; }

    public long Version { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public static NativeKvEntry From(SndbKvEntry entry)
        => new(entry.Key, entry.Value, entry.Version, entry.ExpiresAtUtc);

    public IntPtr GetKey()
    {
        ThrowIfDisposed();
        return GetTextPointer(0, Key);
    }

    public long GetValueLength()
    {
        ThrowIfDisposed();
        return Value.LongLength;
    }

    public int CopyValue(IntPtr buffer, int bufferLength)
    {
        ThrowIfDisposed();
        return SonnetDbNativeExports.CopyBytes(Value, buffer, bufferLength);
    }

    public long GetExpiresAtUnixMilliseconds()
    {
        ThrowIfDisposed();
        return ExpiresAtUtc?.ToUnixTimeMilliseconds() ?? -1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var ptr in _textPointers.Values)
            Marshal.FreeCoTaskMem(ptr);
        _textPointers.Clear();
        _disposed = true;
    }

    private IntPtr GetTextPointer(int key, string value)
    {
        if (_textPointers.TryGetValue(key, out var ptr))
            return ptr;

        ptr = Marshal.StringToCoTaskMemUTF8(value);
        _textPointers.Add(key, ptr);
        return ptr;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class NativeKvScan : IDisposable
{
    private readonly IReadOnlyList<NativeKvEntry> _entries;
    private int _index = -1;
    private bool _disposed;

    public NativeKvScan(IReadOnlyList<NativeKvEntry> entries)
    {
        _entries = entries;
    }

    public int MoveNext()
    {
        ThrowIfDisposed();
        if (_index + 1 >= _entries.Count)
            return 0;

        _index++;
        return 1;
    }

    public NativeKvEntry Current
    {
        get
        {
            ThrowIfDisposed();
            if (_index < 0 || _index >= _entries.Count)
                throw new InvalidOperationException("KV scan is not positioned on an entry.");
            return _entries[_index];
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var entry in _entries)
            entry.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal static class SonnetDbNativeExports
{
    [ThreadStatic]
    private static string? s_lastError;

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_open", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr Open(IntPtr dataSource)
    {
        try
        {
            ClearError();
            var path = ReadUtf8(dataSource, nameof(dataSource));
            var connection = new NativeConnection(path);
            return GCHandle.ToIntPtr(GCHandle.Alloc(connection));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_close", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Close(IntPtr connection)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (connection == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(connection);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_execute", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr Execute(IntPtr connection, IntPtr sql)
    {
        try
        {
            ClearError();
            var nativeConnection = GetTarget<NativeConnection>(connection, nameof(connection));
            var text = ReadUtf8(sql, nameof(sql));
            var result = nativeConnection.Execute(text);
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_create", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr BulkCreate(IntPtr payload)
    {
        try
        {
            ClearError();
            var text = ReadUtf8(payload, nameof(payload));
            var bulk = new NativeBulk(text);
            return GCHandle.ToIntPtr(GCHandle.Alloc(bulk));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_set_measurement", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int BulkSetMeasurement(IntPtr bulk, IntPtr measurement)
    {
        try
        {
            ClearError();
            GetTarget<NativeBulk>(bulk, nameof(bulk)).SetMeasurement(ReadOptionalUtf8(measurement));
            return 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_set_onerror", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int BulkSetOnError(IntPtr bulk, IntPtr onError)
    {
        try
        {
            ClearError();
            GetTarget<NativeBulk>(bulk, nameof(bulk)).SetOnError(ReadOptionalUtf8(onError));
            return 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_set_flush", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int BulkSetFlush(IntPtr bulk, IntPtr flush)
    {
        try
        {
            ClearError();
            GetTarget<NativeBulk>(bulk, nameof(bulk)).SetFlush(ReadOptionalUtf8(flush));
            return 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_execute", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr BulkExecute(IntPtr connection, IntPtr bulk)
    {
        try
        {
            ClearError();
            var nativeConnection = GetTarget<NativeConnection>(connection, nameof(connection));
            var nativeBulk = GetTarget<NativeBulk>(bulk, nameof(bulk));
            var result = nativeConnection.ExecuteBulk(nativeBulk);
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void BulkFree(IntPtr bulk)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (bulk == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(bulk);
            hasHandle = true;
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_open", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr KvOpen(IntPtr connection, IntPtr keyspace, IntPtr @namespace)
    {
        try
        {
            ClearError();
            var nativeConnection = GetTarget<NativeConnection>(connection, nameof(connection));
            var keyspaceText = ReadUtf8(keyspace, nameof(keyspace));
            var namespaceText = ReadOptionalUtf8(@namespace);
            var kv = new NativeKv(nativeConnection, keyspaceText, namespaceText);
            return GCHandle.ToIntPtr(GCHandle.Alloc(kv));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_close", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void KvClose(IntPtr kv)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (kv == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(kv);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_get", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr KvGet(IntPtr kv, IntPtr key)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var entry = nativeKv.Get(ReadUtf8(key, nameof(key)));
            return entry is null ? IntPtr.Zero : GCHandle.ToIntPtr(GCHandle.Alloc(entry));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_set", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvSet(IntPtr kv, IntPtr key, IntPtr value, int valueLength, long expiresAtUnixMs)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var keyText = ReadUtf8(key, nameof(key));
            var valueBytes = ReadBytes(value, valueLength, nameof(value));
            return nativeKv.Set(keyText, valueBytes, FromOptionalUnixMilliseconds(expiresAtUnixMs));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_delete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvDelete(IntPtr kv, IntPtr key)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            return nativeKv.Delete(ReadUtf8(key, nameof(key))) ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_prefix", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr KvScanPrefix(IntPtr kv, IntPtr prefix, int limit)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var scan = nativeKv.ScanPrefix(ReadUtf8(prefix, nameof(prefix)), limit);
            return GCHandle.ToIntPtr(GCHandle.Alloc(scan));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_ttl", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvTimeToLive(IntPtr kv, IntPtr key, IntPtr expiresAtUnixMs)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var ttl = nativeKv.GetTimeToLive(ReadUtf8(key, nameof(key)));
            WriteInt64(expiresAtUnixMs, ttl.ExpiresAtUtc?.ToUnixTimeMilliseconds() ?? -1);
            return ttl.Milliseconds;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -3;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_expire_at", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvExpireAt(IntPtr kv, IntPtr key, long expiresAtUnixMs)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            if (expiresAtUnixMs < 0)
                throw new ArgumentOutOfRangeException(nameof(expiresAtUnixMs), "expires_at_unix_ms must be non-negative.");

            return nativeKv.Expire(ReadUtf8(key, nameof(key)), DateTimeOffset.FromUnixTimeMilliseconds(expiresAtUnixMs)) ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_persist", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvPersist(IntPtr kv, IntPtr key)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            return nativeKv.Persist(ReadUtf8(key, nameof(key))) ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_incr", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvIncrement(IntPtr kv, IntPtr key, long delta, IntPtr value, IntPtr version)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var result = nativeKv.Increment(ReadUtf8(key, nameof(key)), delta);
            WriteInt64(value, result.Value);
            WriteInt64(version, result.Version);
            return 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_cas", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvCompareAndSet(
        IntPtr kv,
        IntPtr key,
        long expectedVersion,
        IntPtr value,
        int valueLength,
        long expiresAtUnixMs,
        IntPtr currentVersion,
        IntPtr newVersion)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var result = nativeKv.CompareAndSet(
                ReadUtf8(key, nameof(key)),
                expectedVersion,
                ReadBytes(value, valueLength, nameof(value)),
                FromOptionalUnixMilliseconds(expiresAtUnixMs));
            WriteInt64(currentVersion, result.CurrentVersion);
            WriteInt64(newVersion, result.NewVersion ?? -1);
            return result.Succeeded ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void KvEntryFree(IntPtr entry)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (entry == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(entry);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_key", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr KvEntryKey(IntPtr entry)
        => InvokeKvEntry(entry, static e => e.GetKey(), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_value_length", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvEntryValueLength(IntPtr entry)
        => InvokeKvEntry(entry, static e => e.GetValueLength(), -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_copy_value", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvEntryCopyValue(IntPtr entry, IntPtr buffer, int bufferLength)
        => InvokeKvEntry(entry, e => e.CopyValue(buffer, bufferLength), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_version", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvEntryVersion(IntPtr entry)
        => InvokeKvEntry(entry, static e => e.Version, -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_expires_at_unix_ms", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvEntryExpiresAtUnixMs(IntPtr entry)
        => InvokeKvEntry(entry, static e => e.GetExpiresAtUnixMilliseconds(), -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_next", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvScanNext(IntPtr scan)
    {
        try
        {
            ClearError();
            return GetTarget<NativeKvScan>(scan, nameof(scan)).MoveNext();
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_key", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr KvScanKey(IntPtr scan)
        => InvokeKvScan(scan, static s => s.Current.GetKey(), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_value_length", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvScanValueLength(IntPtr scan)
        => InvokeKvScan(scan, static s => s.Current.GetValueLength(), -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_copy_value", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvScanCopyValue(IntPtr scan, IntPtr buffer, int bufferLength)
        => InvokeKvScan(scan, s => s.Current.CopyValue(buffer, bufferLength), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_version", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvScanVersion(IntPtr scan)
        => InvokeKvScan(scan, static s => s.Current.Version, -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_expires_at_unix_ms", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvScanExpiresAtUnixMs(IntPtr scan)
        => InvokeKvScan(scan, static s => s.Current.GetExpiresAtUnixMilliseconds(), -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void KvScanFree(IntPtr scan)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (scan == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(scan);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ResultFree(IntPtr result)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (result == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(result);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_records_affected", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ResultRecordsAffected(IntPtr result)
        => Invoke(result, static r => r.RecordsAffected, -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_column_count", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ResultColumnCount(IntPtr result)
        => Invoke(result, static r => r.ColumnCount, -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_column_name", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ResultColumnName(IntPtr result, int ordinal)
        => Invoke(result, r => r.GetColumnName(ordinal), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_next", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ResultNext(IntPtr result)
        => Invoke(result, static r => r.MoveNext(), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_value_type", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ResultValueType(IntPtr result, int ordinal)
        => Invoke(result, r => (int)r.GetValueType(ordinal), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_value_int64", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long ResultValueInt64(IntPtr result, int ordinal)
        => Invoke(result, r => r.GetInt64(ordinal), 0L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_value_double", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static double ResultValueDouble(IntPtr result, int ordinal)
        => Invoke(result, r => r.GetDouble(ordinal), 0d);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_value_bool", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ResultValueBool(IntPtr result, int ordinal)
        => Invoke(result, r => r.GetBoolean(ordinal), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_value_text", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ResultValueText(IntPtr result, int ordinal)
        => Invoke(result, r => r.GetText(ordinal), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_flush", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Flush(IntPtr connection)
    {
        try
        {
            ClearError();
            GetTarget<NativeConnection>(connection, nameof(connection)).Flush();
            return 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_version", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Version(IntPtr buffer, int bufferLength)
    {
        try
        {
            ClearError();
            var version = typeof(SndbConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(SndbConnection).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
            return CopyUtf8(version, buffer, bufferLength);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_last_error", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int LastError(IntPtr buffer, int bufferLength)
        => CopyUtf8(s_lastError ?? string.Empty, buffer, bufferLength);

    private static TReturn Invoke<TReturn>(IntPtr result, Func<NativeResult, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativeResult = GetTarget<NativeResult>(result, nameof(result));
            return action(nativeResult);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static TReturn InvokeKvEntry<TReturn>(IntPtr entry, Func<NativeKvEntry, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativeEntry = GetTarget<NativeKvEntry>(entry, nameof(entry));
            return action(nativeEntry);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static TReturn InvokeKvScan<TReturn>(IntPtr scan, Func<NativeKvScan, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativeScan = GetTarget<NativeKvScan>(scan, nameof(scan));
            return action(nativeScan);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static T GetTarget<T>(IntPtr handle, string parameterName)
        where T : class
    {
        if (handle == IntPtr.Zero)
            throw new ArgumentNullException(parameterName);

        var gcHandle = GCHandle.FromIntPtr(handle);
        return gcHandle.Target as T
            ?? throw new InvalidOperationException($"Native handle '{parameterName}' has an unexpected target type.");
    }

    private static string ReadUtf8(IntPtr pointer, string parameterName)
    {
        if (pointer == IntPtr.Zero)
            throw new ArgumentNullException(parameterName);

        return Marshal.PtrToStringUTF8(pointer)
            ?? throw new ArgumentException("UTF-8 string pointer is invalid.", parameterName);
    }

    private static string? ReadOptionalUtf8(IntPtr pointer)
        => pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);

    private static byte[] ReadBytes(IntPtr pointer, int length, string parameterName)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Byte length cannot be negative.");
        if (length == 0)
            return Array.Empty<byte>();
        if (pointer == IntPtr.Zero)
            throw new ArgumentNullException(parameterName);

        byte[] bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return bytes;
    }

    private static DateTimeOffset? FromOptionalUnixMilliseconds(long value)
        => value < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static void WriteInt64(IntPtr pointer, long value)
    {
        if (pointer != IntPtr.Zero)
            Marshal.WriteInt64(pointer, value);
    }

    private static int CopyUtf8(string value, IntPtr buffer, int bufferLength)
    {
        if (buffer == IntPtr.Zero || bufferLength <= 0)
            return Encoding.UTF8.GetByteCount(value);

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        int copyLength = Math.Min(bytes.Length, bufferLength - 1);
        if (copyLength > 0)
            Marshal.Copy(bytes, 0, buffer, copyLength);
        Marshal.WriteByte(buffer, copyLength, 0);
        return bytes.Length;
    }

    public static int CopyBytes(byte[] value, IntPtr buffer, int bufferLength)
    {
        if (bufferLength < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferLength), "Buffer length cannot be negative.");
        if (buffer == IntPtr.Zero || bufferLength == 0)
            return value.Length;

        int copyLength = Math.Min(value.Length, bufferLength);
        if (copyLength > 0)
            Marshal.Copy(value, 0, buffer, copyLength);
        return value.Length;
    }

    private static void ClearError()
    {
        s_lastError = null;
    }

    private static void SetError(Exception exception)
    {
        s_lastError = exception.Message;
    }
}
