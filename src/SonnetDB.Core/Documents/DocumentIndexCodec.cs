using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Documents;

internal static class DocumentIndexCodec
{
    private static readonly Encoding _utf8 = Encoding.UTF8;

    public static byte[] EncodeDocumentKey(string id)
    {
        byte[] idBytes = _utf8.GetBytes(id);
        var key = new byte[1 + 4 + idBytes.Length];
        key[0] = (byte)'d';
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(1, 4), idBytes.Length);
        idBytes.CopyTo(key.AsSpan(5));
        return key;
    }

    public static string DecodeIdFromDocumentKey(ReadOnlyMemory<byte> key)
    {
        var span = key.Span;
        if (span.Length < 5 || span[0] != (byte)'d')
            throw new InvalidDataException("Document key is invalid.");

        int length = BinaryPrimitives.ReadInt32BigEndian(span.Slice(1, 4));
        if (length < 0 || span.Length != 5 + length)
            throw new InvalidDataException("Document key length is invalid.");

        return _utf8.GetString(span.Slice(5, length));
    }

    public static byte[] EncodeIndexPrefix(DocumentPathIndex index, string scalar)
        => EncodeIndexPrefix(index, [DocumentIndexKeyPart.FromString(scalar)]);

    public static byte[] EncodeIndexPrefix(DocumentPathIndex index, IReadOnlyList<DocumentIndexKeyPart> values)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > GetKeyPartCount(index))
            throw new ArgumentException("索引值数量与索引 path 数量不一致。", nameof(values));

        byte[] indexNameBytes = _utf8.GetBytes(index.Name);
        if (indexNameBytes.Length > ushort.MaxValue)
            throw new InvalidOperationException($"文档索引 '{index.Name}' 名称过长。");

        int totalSize = 1 + 2 + indexNameBytes.Length;
        foreach (var value in values)
            totalSize += GetEncodedPartSize(value);

        var key = new byte[totalSize];
        int offset = 0;
        key[offset++] = (byte)'i';
        BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(offset, 2), (ushort)indexNameBytes.Length);
        offset += 2;
        indexNameBytes.CopyTo(key.AsSpan(offset));
        offset += indexNameBytes.Length;
        foreach (var value in values)
            offset += WriteEncodedPart(key.AsSpan(offset), value);
        return key;
    }

    public static byte[] EncodeIndexEntryKey(DocumentPathIndex index, string scalar, string id)
        => EncodeIndexEntryKey(index, [DocumentIndexKeyPart.FromString(scalar)], id);

    public static byte[] EncodeIndexEntryKey(DocumentPathIndex index, IReadOnlyList<DocumentIndexKeyPart> values, string id)
    {
        if (values.Count != GetKeyPartCount(index))
            throw new ArgumentException("Index entry value count must match the index path count.", nameof(values));

        byte[] prefix = EncodeIndexPrefix(index, values);
        if (index.IsUnique)
            return prefix;

        byte[] idBytes = _utf8.GetBytes(id);
        var key = new byte[prefix.Length + 4 + idBytes.Length];
        prefix.CopyTo(key);
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(prefix.Length, 4), idBytes.Length);
        idBytes.CopyTo(key.AsSpan(prefix.Length + 4));
        return key;
    }

    public static byte[] EncodeIndexEntryValue(string id)
        => _utf8.GetBytes(id);

    public static string DecodeIndexEntryValue(ReadOnlySpan<byte> value)
        => _utf8.GetString(value);

    /// <summary>
    /// 从 <c>'i'</c> 前缀索引条目 key 中解出索引名，供一致性校验按索引分组统计。
    /// </summary>
    /// <param name="key">索引条目 key，布局为 <c>'i' | u16 nameLen | name | parts...</c>。</param>
    /// <returns>索引名；key 非法时返回 null。</returns>
    public static string? TryDecodeIndexNameFromEntryKey(ReadOnlySpan<byte> key)
    {
        if (key.Length < 3 || key[0] != (byte)'i')
            return null;

        int nameLength = BinaryPrimitives.ReadUInt16BigEndian(key.Slice(1, 2));
        if (key.Length < 3 + nameLength)
            return null;

        return _utf8.GetString(key.Slice(3, nameLength));
    }

    public static int GetKeyPartCount(DocumentPathIndex index)
        => index.Kind == DocumentIndexKind.Wildcard ? 2 : index.Paths.Count;

    private static int GetEncodedPartSize(DocumentIndexKeyPart value)
        => value.Kind is DocumentIndexKeyPartKind.Missing or DocumentIndexKeyPartKind.Null
            ? 1
            : 1 + 4 + _utf8.GetByteCount(value.Scalar!);

    private static int WriteEncodedPart(Span<byte> destination, DocumentIndexKeyPart value)
    {
        destination[0] = value.Kind switch
        {
            DocumentIndexKeyPartKind.Missing => (byte)0,
            DocumentIndexKeyPartKind.Null => (byte)1,
            DocumentIndexKeyPartKind.Boolean => (byte)2,
            DocumentIndexKeyPartKind.Number => (byte)3,
            DocumentIndexKeyPartKind.String => (byte)4,
            DocumentIndexKeyPartKind.Object => (byte)5,
            DocumentIndexKeyPartKind.Array => (byte)6,
            _ => throw new InvalidOperationException($"未知文档索引值类型 {value.Kind}。"),
        };

        if (value.Kind is DocumentIndexKeyPartKind.Missing or DocumentIndexKeyPartKind.Null)
            return 1;

        byte[] bytes = _utf8.GetBytes(value.Scalar!);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(1, 4), bytes.Length);
        bytes.CopyTo(destination.Slice(5));
        return 5 + bytes.Length;
    }
}

internal readonly record struct DocumentIndexKeyPart(DocumentIndexKeyPartKind Kind, string? Scalar)
{
    public static DocumentIndexKeyPart Missing { get; } = new(DocumentIndexKeyPartKind.Missing, null);

    public static DocumentIndexKeyPart Null { get; } = new(DocumentIndexKeyPartKind.Null, null);

    public static DocumentIndexKeyPart FromBoolean(bool value)
        => new(DocumentIndexKeyPartKind.Boolean, value ? "1" : "0");

    public static DocumentIndexKeyPart FromNumber(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized;
        try
        {
            normalized = Convert.ToDecimal(value, CultureInfo.InvariantCulture)
                .ToString("G29", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException)
        {
            normalized = Convert.ToDouble(value, CultureInfo.InvariantCulture)
                .ToString("R", CultureInfo.InvariantCulture);
        }

        return new DocumentIndexKeyPart(DocumentIndexKeyPartKind.Number, normalized);
    }

    public static DocumentIndexKeyPart FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DocumentIndexKeyPart(DocumentIndexKeyPartKind.String, value);
    }

    public static DocumentIndexKeyPart FromObject(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new DocumentIndexKeyPart(DocumentIndexKeyPartKind.Object, json);
    }

    public static DocumentIndexKeyPart FromArray(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new DocumentIndexKeyPart(DocumentIndexKeyPartKind.Array, json);
    }

    public static DocumentIndexKeyPart FromValue(object? value)
        => value switch
        {
            null => Null,
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => Null,
            JsonElement { ValueKind: JsonValueKind.True } => FromBoolean(true),
            JsonElement { ValueKind: JsonValueKind.False } => FromBoolean(false),
            JsonElement { ValueKind: JsonValueKind.Number } element => FromNumber(element.GetRawText()),
            JsonElement { ValueKind: JsonValueKind.String } element => FromString(element.GetString() ?? string.Empty),
            JsonElement { ValueKind: JsonValueKind.Object } element => FromObject(element.GetRawText()),
            JsonElement { ValueKind: JsonValueKind.Array } element => FromArray(element.GetRawText()),
            bool boolean => FromBoolean(boolean),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                FromNumber(value),
            DateTime dateTime => FromString(dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset dateTimeOffset => FromString(dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            string text => FromString(text),
            _ => FromString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        };
}

internal enum DocumentIndexKeyPartKind
{
    Missing,
    Null,
    Boolean,
    Number,
    String,
    Object,
    Array,
}
