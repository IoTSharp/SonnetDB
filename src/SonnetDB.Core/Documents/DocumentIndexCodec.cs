using System.Buffers.Binary;
using System.Text;

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
        => EncodeIndexPrefix(index, [DocumentIndexKeyPart.FromScalar(scalar)]);

    public static byte[] EncodeIndexPrefix(DocumentPathIndex index, IReadOnlyList<DocumentIndexKeyPart> values)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > index.Paths.Count)
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
        => EncodeIndexEntryKey(index, [DocumentIndexKeyPart.FromScalar(scalar)], id);

    public static byte[] EncodeIndexEntryKey(DocumentPathIndex index, IReadOnlyList<DocumentIndexKeyPart> values, string id)
    {
        if (values.Count != index.Paths.Count)
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

    private static int GetEncodedPartSize(DocumentIndexKeyPart value)
        => value.Kind == DocumentIndexKeyPartKind.Scalar
            ? 1 + 4 + _utf8.GetByteCount(value.Scalar!)
            : 1;

    private static int WriteEncodedPart(Span<byte> destination, DocumentIndexKeyPart value)
    {
        destination[0] = value.Kind switch
        {
            DocumentIndexKeyPartKind.Missing => (byte)0,
            DocumentIndexKeyPartKind.Null => (byte)1,
            DocumentIndexKeyPartKind.Scalar => (byte)2,
            _ => throw new InvalidOperationException($"未知文档索引值类型 {value.Kind}。"),
        };

        if (value.Kind != DocumentIndexKeyPartKind.Scalar)
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

    public static DocumentIndexKeyPart FromScalar(string scalar)
    {
        ArgumentNullException.ThrowIfNull(scalar);
        return new DocumentIndexKeyPart(DocumentIndexKeyPartKind.Scalar, scalar);
    }
}

internal enum DocumentIndexKeyPartKind
{
    Missing,
    Null,
    Scalar,
}
