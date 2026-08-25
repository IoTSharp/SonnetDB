using System.Buffers.Binary;
using System.Text;

namespace SonnetDB.Generations;

internal static class DatabaseGenerationCodec
{
    private const int CurrentVersion = 1;
    private const int MaximumStringBytes = 4096;
    private const int MaximumResourceCount = 256;
    private static ReadOnlySpan<byte> Magic => "SDBGNR01"u8;
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    internal static byte[] Encode(DatabaseGeneration generation)
    {
        using var stream = new MemoryStream();
        stream.Write(Magic);
        WriteInt32(stream, CurrentVersion);
        WriteString(stream, generation.Stream);
        WriteString(stream, generation.GenerationId);
        WriteInt64(stream, generation.Revision);
        WriteInt64(stream, generation.PublishedAtUtc.UtcTicks);
        WriteInt32(stream, generation.Resources.Count);
        foreach (DatabaseGenerationResource resource in generation.Resources)
        {
            WriteString(stream, resource.Role);
            WriteInt32(stream, (int)resource.Kind);
            WriteString(stream, resource.Name);
            WriteNullableString(stream, resource.ParentName);
        }

        return stream.ToArray();
    }

    internal static DatabaseGeneration Decode(ReadOnlySpan<byte> payload)
    {
        try
        {
            int offset = 0;
            ReadOnlySpan<byte> magic = ReadBytes(payload, ref offset, Magic.Length);
            if (!magic.SequenceEqual(Magic))
                throw InvalidPayload();
            if (ReadInt32(payload, ref offset) != CurrentVersion)
                throw InvalidPayload();

            string stream = ReadString(payload, ref offset);
            string generationId = ReadString(payload, ref offset);
            long revision = ReadInt64(payload, ref offset);
            long publishedAtUtcTicks = ReadInt64(payload, ref offset);
            if (revision <= 0)
                throw InvalidPayload();

            int resourceCount = ReadInt32(payload, ref offset);
            if (resourceCount <= 0 || resourceCount > MaximumResourceCount)
                throw InvalidPayload();
            var resources = new DatabaseGenerationResource[resourceCount];
            for (int i = 0; i < resources.Length; i++)
            {
                string role = ReadString(payload, ref offset);
                int rawKind = ReadInt32(payload, ref offset);
                if (!Enum.IsDefined((DatabaseGenerationResourceKind)rawKind))
                    throw InvalidPayload();
                string name = ReadString(payload, ref offset);
                string? parentName = ReadNullableString(payload, ref offset);
                resources[i] = new DatabaseGenerationResource(
                    role,
                    (DatabaseGenerationResourceKind)rawKind,
                    name,
                    parentName);
            }

            if (offset != payload.Length)
                throw InvalidPayload();
            return new DatabaseGeneration(
                stream,
                generationId,
                revision,
                new DateTimeOffset(publishedAtUtcTicks, TimeSpan.Zero),
                resources);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or ArgumentOutOfRangeException
            or DecoderFallbackException)
        {
            throw InvalidPayload(exception);
        }
    }

    internal static byte[] EncodeRevision(long revision)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, revision);
        return bytes.ToArray();
    }

    internal static long DecodeRevision(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != sizeof(long))
            throw InvalidPayload();
        long revision = BinaryPrimitives.ReadInt64LittleEndian(payload);
        if (revision <= 0)
            throw InvalidPayload();
        return revision;
    }

    private static void WriteNullableString(Stream stream, string? value)
    {
        if (value is null)
        {
            WriteInt32(stream, -1);
            return;
        }
        WriteString(stream, value);
    }

    private static string? ReadNullableString(ReadOnlySpan<byte> payload, ref int offset)
    {
        int length = ReadInt32(payload, ref offset);
        if (length == -1)
            return null;
        return ReadStringBytes(payload, ref offset, length);
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = _strictUtf8.GetBytes(value);
        if (bytes.Length > MaximumStringBytes)
            throw new ArgumentOutOfRangeException(nameof(value));
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static string ReadString(ReadOnlySpan<byte> payload, ref int offset)
        => ReadStringBytes(payload, ref offset, ReadInt32(payload, ref offset));

    private static string ReadStringBytes(ReadOnlySpan<byte> payload, ref int offset, int length)
    {
        if (length < 0 || length > MaximumStringBytes)
            throw InvalidPayload();
        return _strictUtf8.GetString(ReadBytes(payload, ref offset, length));
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static int ReadInt32(ReadOnlySpan<byte> payload, ref int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(payload, ref offset, sizeof(int)));

    private static long ReadInt64(ReadOnlySpan<byte> payload, ref int offset)
        => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(payload, ref offset, sizeof(long)));

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> payload, ref int offset, int length)
    {
        if (length < 0 || offset < 0 || length > payload.Length - offset)
            throw InvalidPayload();
        ReadOnlySpan<byte> result = payload.Slice(offset, length);
        offset += length;
        return result;
    }

    private static InvalidDataException InvalidPayload(Exception? innerException = null)
        => new("generation catalog 记录损坏或版本不受支持。", innerException);
}
