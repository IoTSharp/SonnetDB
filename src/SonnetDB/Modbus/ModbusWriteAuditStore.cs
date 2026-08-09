using System.Text.Json;
using SonnetDB.Json;

namespace SonnetDB.Modbus;

internal interface IModbusWriteAuditStore
{
    void Append(ModbusWriteAuditEntry entry);

    IReadOnlyList<ModbusWriteAuditEntry> List(string database, int maxEntries);
}

internal sealed class FileModbusWriteAuditStore : IModbusWriteAuditStore
{
    private const string FileName = "modbus-write-audit.ndjson";
    private readonly object _sync = new();
    private readonly string _path;
    private readonly List<ModbusWriteAuditEntry> _entries = [];

    internal FileModbusWriteAuditStore(string systemDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);
        Directory.CreateDirectory(systemDirectory);
        _path = Path.Combine(systemDirectory, FileName);
        LoadExisting();
    }

    public void Append(ModbusWriteAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateEntry(entry, lineNumber: null);
        lock (_sync)
        {
            using var buffer = new MemoryStream();
            JsonSerializer.Serialize(
                buffer,
                entry,
                ServerJsonContext.Default.ModbusWriteAuditEntry);
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
                    // 原始写异常保持为调用方看到的主故障；下次启动会拒绝损坏的审计文件。
                }

                throw;
            }

            _entries.Add(entry);
        }
    }

    public IReadOnlyList<ModbusWriteAuditEntry> List(string database, int maxEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        lock (_sync)
        {
            return _entries
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
            ModbusWriteAuditEntry entry;
            try
            {
                entry = JsonSerializer.Deserialize(
                        line,
                        ServerJsonContext.Default.ModbusWriteAuditEntry)
                    ?? throw new InvalidDataException("审计记录不能为 null。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Modbus 写审计文件第 {lineNumber} 行损坏。",
                    exception);
            }

            ValidateEntry(entry, lineNumber);
            _entries.Add(entry);
        }
    }

    private static void ValidateEntry(ModbusWriteAuditEntry entry, int? lineNumber)
    {
        bool valid = entry.EventId != Guid.Empty
            && entry.OperationId != Guid.Empty
            && entry.OccurredAtUtc != default
            && !string.IsNullOrWhiteSpace(entry.Principal)
            && !string.IsNullOrWhiteSpace(entry.Database)
            && !string.IsNullOrWhiteSpace(entry.Source)
            && !string.IsNullOrWhiteSpace(entry.Table)
            && !string.IsNullOrWhiteSpace(entry.Column)
            && entry.FunctionCode is 0x05 or 0x06 or 0x10
            && !string.IsNullOrWhiteSpace(entry.EventType)
            && !string.IsNullOrWhiteSpace(entry.Result)
            && entry.CatalogRevision >= 0;
        if (valid)
            return;

        string location = lineNumber is null
            ? string.Empty
            : $"第 {lineNumber.Value} 行";
        throw new InvalidDataException($"Modbus 写审计记录{location}字段无效。");
    }
}

internal sealed record ModbusWriteAuditEntry(
    Guid EventId,
    Guid OperationId,
    DateTimeOffset OccurredAtUtc,
    string Principal,
    string Database,
    string Source,
    string Table,
    string Column,
    byte UnitId,
    byte FunctionCode,
    int DeclaredAddress,
    ushort PduAddress,
    string EventType,
    string Result,
    string? ErrorCode,
    Guid? ApprovalId,
    long CatalogRevision);
