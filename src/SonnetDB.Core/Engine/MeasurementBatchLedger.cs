using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Engine;

/// <summary>
/// Measurement 批次幂等账本。批次 ID 与确定性 payload fingerprint 一起持久化，
/// 使客户端在网络结果不确定或进程重开后安全重放同一批次。
/// </summary>
internal sealed class MeasurementBatchLedger
{
    private const int MaxEntries = 1_000_000;
    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    internal readonly record struct Entry(string Fingerprint, bool Committed);

    private MeasurementBatchLedger(string path) => _path = path;

    public static MeasurementBatchLedger Load(string path)
    {
        var ledger = new MeasurementBatchLedger(path);
        if (!File.Exists(path))
            return ledger;

        int count = 0;
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (++count > MaxEntries)
                throw new InvalidDataException("Measurement batch ledger exceeds the 1,000,000 entry limit.");
            string[] parts = line.Split('\t');
            if (parts.Length == 2)
                parts = ["C", parts[0], parts[1]]; // pre-ledger format
            if (parts.Length != 3)
                throw new InvalidDataException("Measurement batch ledger contains an invalid record.");
            string id = parts[1];
            string fingerprint = parts[2];
            if (parts[0] is not ("P" or "C") || !IsValidId(id) || fingerprint.Length != 64 || !IsHex(fingerprint))
                throw new InvalidDataException("Measurement batch ledger contains an invalid identity.");
            ledger._entries[id] = new Entry(fingerprint, parts[0] == "C");
        }
        return ledger;
    }

    public bool TryGet(string batchId, out string? fingerprint)
    {
        bool found = _entries.TryGetValue(batchId, out Entry entry);
        fingerprint = found ? entry.Fingerprint : null;
        return found;
    }

    public bool IsCommitted(string batchId) => _entries.TryGetValue(batchId, out Entry entry) && entry.Committed;

    public void Prepare(string batchId, string fingerprint)
    {
        if (_entries.TryGetValue(batchId, out Entry existing))
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException($"Measurement batch '{batchId}' was already committed with a different payload.");
            return;
        }
        Append("P", batchId, fingerprint);
        _entries.Add(batchId, new Entry(fingerprint, false));
    }

    public void Commit(string batchId, string fingerprint)
    {
        if (_entries.TryGetValue(batchId, out Entry existing))
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException($"Measurement batch '{batchId}' was already committed with a different payload.");
            if (existing.Committed)
                return;
            Append("C", batchId, fingerprint);
            _entries[batchId] = existing with { Committed = true };
            return;
        }
        Append("C", batchId, fingerprint);
        _entries.Add(batchId, new Entry(fingerprint, true));
    }

    private void Append(string state, string batchId, string fingerprint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
        writer.Write(state); writer.Write('\t'); writer.Write(batchId); writer.Write('\t'); writer.Write(fingerprint); writer.Write('\n');
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    public static string Fingerprint(ReadOnlySpan<Point> points)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[8];
        foreach (Point point in points)
        {
            if (point is null)
            {
                Add(hash, "#null");
                continue;
            }
            Add(hash, point.Measurement);
            Add(hash, point.Timestamp.ToString(CultureInfo.InvariantCulture));
            foreach (var tag in point.Tags.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                Add(hash, tag.Key);
                Add(hash, tag.Value);
            }
            Add(hash, "#fields");
            foreach (var field in point.Fields.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                Add(hash, field.Key);
                Add(hash, field.Value.Type.ToString());
                Add(hash, FormatFieldValue(field.Value));
            }
            BitConverter.TryWriteBytes(number, 0x1f); // record separator
            hash.AppendData(number[..1]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Add(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData("\0"u8);
    }

    private static string FormatFieldValue(FieldValue value) => value.Type switch
    {
        FieldType.Float64 => value.AsDouble().ToString("R", CultureInfo.InvariantCulture),
        FieldType.Int64 => value.AsLong().ToString(CultureInfo.InvariantCulture),
        FieldType.Boolean => value.AsBool() ? "true" : "false",
        FieldType.String => value.AsString(),
        FieldType.Vector => string.Join(",", value.AsVector().Span.ToArray().Select(static item => item.ToString("R", CultureInfo.InvariantCulture))),
        FieldType.GeoPoint => value.AsGeoPoint().Lat.ToString("R", CultureInfo.InvariantCulture) + "," + value.AsGeoPoint().Lon.ToString("R", CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static bool IsValidId(string id) => id.Length is > 0 and <= 256 && id.All(static c => c >= 0x21 && c <= 0x7e && c != '\t');
    private static bool IsHex(string value) => value.All(static c => char.IsAsciiHexDigit(c));
}
