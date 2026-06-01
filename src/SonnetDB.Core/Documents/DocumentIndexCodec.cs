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
    {
        byte[] indexNameBytes = _utf8.GetBytes(index.Name);
        byte[] scalarBytes = _utf8.GetBytes(scalar);
        if (indexNameBytes.Length > ushort.MaxValue)
            throw new InvalidOperationException($"文档索引 '{index.Name}' 名称过长。");

        var key = new byte[1 + 2 + indexNameBytes.Length + 4 + scalarBytes.Length];
        int offset = 0;
        key[offset++] = (byte)'i';
        BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(offset, 2), (ushort)indexNameBytes.Length);
        offset += 2;
        indexNameBytes.CopyTo(key.AsSpan(offset));
        offset += indexNameBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(offset, 4), scalarBytes.Length);
        offset += 4;
        scalarBytes.CopyTo(key.AsSpan(offset));
        return key;
    }

    public static byte[] EncodeIndexEntryKey(DocumentPathIndex index, string scalar, string id)
    {
        byte[] prefix = EncodeIndexPrefix(index, scalar);
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
}
