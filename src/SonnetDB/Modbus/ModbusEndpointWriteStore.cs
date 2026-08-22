using System.Text.Json;
using SonnetDB.Json;

namespace SonnetDB.Modbus;

internal interface IModbusEndpointWriteStore
{
    void Append(ModbusEndpointWriteEvent entry);

    ModbusEndpointWriteEvent? TryGetLatest(Guid requestId);

    IReadOnlyList<ModbusEndpointWriteEvent> ListLatest(string database, int maxEntries);

    IReadOnlyList<ModbusEndpointWriteEvent> ListEvents(string database, int maxEntries);
}

/// <summary>
/// 以 append-only NDJSON 事件流持久化 endpoint 外部写请求和审批结果。
/// 每次追加都执行 durable flush；文件损坏时拒绝加载，保证写治理失败关闭。
/// </summary>
internal sealed class FileModbusEndpointWriteStore : IModbusEndpointWriteStore
{
    private const string FileName = "modbus-endpoint-writes.ndjson";
    private readonly object _sync = new();
    private readonly string _path;
    private readonly List<ModbusEndpointWriteEvent> _events = [];
    private readonly Dictionary<Guid, ModbusEndpointWriteEvent> _latest = [];

    internal FileModbusEndpointWriteStore(string systemDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);
        Directory.CreateDirectory(systemDirectory);
        _path = Path.Combine(systemDirectory, FileName);
        LoadExisting();
    }

    public void Append(ModbusEndpointWriteEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateEntry(entry, lineNumber: null);
        lock (_sync)
        {
            using var buffer = new MemoryStream();
            JsonSerializer.Serialize(
                buffer,
                entry,
                ServerJsonContext.Default.ModbusEndpointWriteEvent);
            buffer.WriteByte((byte)'\n');

            using var stream = new FileStream(
                _path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            long originalLength = stream.Length;
            stream.Position = originalLength;
            try
            {
                buffer.Position = 0;
                buffer.CopyTo(stream);
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                try
                {
                    stream.SetLength(originalLength);
                    stream.Flush(flushToDisk: true);
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    // 保留原始写异常；下次启动会拒绝任何未成功回滚的损坏记录。
                }

                throw;
            }

            _events.Add(entry);
            _latest[entry.RequestId] = entry;
        }
    }

    public ModbusEndpointWriteEvent? TryGetLatest(Guid requestId)
    {
        lock (_sync)
            return _latest.GetValueOrDefault(requestId);
    }

    public IReadOnlyList<ModbusEndpointWriteEvent> ListLatest(string database, int maxEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        lock (_sync)
        {
            return _latest.Values
                .Where(entry => string.Equals(entry.Database, database, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static entry => entry.OccurredAtUtc)
                .Take(maxEntries)
                .ToArray();
        }
    }

    public IReadOnlyList<ModbusEndpointWriteEvent> ListEvents(string database, int maxEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        lock (_sync)
        {
            return _events
                .Where(entry => string.Equals(entry.Database, database, StringComparison.OrdinalIgnoreCase))
                .TakeLast(maxEntries)
                .ToArray();
        }
    }

    private void LoadExisting()
    {
        if (!File.Exists(_path))
            return;

        int lineNumber = 0;
        foreach (string line in File.ReadLines(_path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            ModbusEndpointWriteEvent entry;
            try
            {
                entry = JsonSerializer.Deserialize(
                        line,
                        ServerJsonContext.Default.ModbusEndpointWriteEvent)
                    ?? throw new InvalidDataException("Endpoint 写治理事件不能为 null。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Modbus endpoint 写治理文件第 {lineNumber} 行损坏。",
                    exception);
            }

            ValidateEntry(entry, lineNumber);
            _events.Add(entry);
            _latest[entry.RequestId] = entry;
        }
    }

    private static void ValidateEntry(ModbusEndpointWriteEvent entry, int? lineNumber)
    {
        bool isWriteFunction = entry.FunctionCode is 0x05 or 0x06 or 0x0F or 0x10;
        bool valid = entry.EventId != Guid.Empty
            && entry.RequestId != Guid.Empty
            && entry.OccurredAtUtc != default
            && !string.IsNullOrWhiteSpace(entry.EventType)
            && !string.IsNullOrWhiteSpace(entry.State)
            && !string.IsNullOrWhiteSpace(entry.Principal)
            && !string.IsNullOrWhiteSpace(entry.Database)
            && !string.IsNullOrWhiteSpace(entry.Endpoint)
            && !string.IsNullOrWhiteSpace(entry.RemoteEndpoint)
            && isWriteFunction
            && entry.CatalogRevision >= 0;
        if (valid && string.Equals(entry.EventType, "staged", StringComparison.Ordinal))
        {
            valid = entry.ExpiresAtUtc is not null
                && entry.RawValues.Length > 0
                && entry.DecodedValue is not null
                && !string.IsNullOrWhiteSpace(entry.Table)
                && !string.IsNullOrWhiteSpace(entry.Column)
                && entry.RowKey is not null
                && !string.IsNullOrWhiteSpace(entry.RowFingerprint)
                && !string.IsNullOrWhiteSpace(entry.AppliedRowFingerprint);
        }

        if (valid)
            return;

        string location = lineNumber is null ? string.Empty : $"第 {lineNumber.Value} 行";
        throw new InvalidDataException($"Modbus endpoint 写治理记录{location}字段无效。");
    }
}

/// <summary>
/// Endpoint 外部写的一条持久审计事件。首个 <c>staged</c> 事件包含完整请求快照，
/// 后续事件重复关键绑定，确保每一行都能独立审计。
/// </summary>
internal sealed record ModbusEndpointWriteEvent(
    Guid EventId,
    Guid RequestId,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    string State,
    string Principal,
    string Database,
    string Endpoint,
    string RemoteEndpoint,
    byte UnitId,
    ushort TransactionId,
    byte FunctionCode,
    ModbusRegisterArea Area,
    int DeclaredAddress,
    ushort PduAddress,
    ushort[] RawValues,
    string? DecodedValue,
    string? Table,
    string? Column,
    long? RowKey,
    string? RowFingerprint,
    string? AppliedRowFingerprint,
    long CatalogRevision,
    ModbusApprovedWriteAction ApprovedAction,
    DateTimeOffset? ExpiresAtUtc,
    string? ErrorCode,
    string? Reason);
