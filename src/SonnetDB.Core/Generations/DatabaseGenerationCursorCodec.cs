using System.Security.Cryptography;
using System.Text;

namespace SonnetDB.Generations;

internal static class DatabaseGenerationCursorCodec
{
    private const int CurrentVersion = 1;
    private const int SignatureLength = 32;
    private static readonly byte[] _signatureDomain = Encoding.ASCII.GetBytes("SonnetDB.DatabaseGenerationCursor.v1\0");
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    internal static string Encode(
        DatabaseGeneration generation,
        string queryFingerprint,
        ReadOnlySpan<byte> continuationState)
    {
        using var payloadStream = new MemoryStream();
        using (var writer = new BinaryWriter(payloadStream, _strictUtf8, leaveOpen: true))
        {
            writer.Write(CurrentVersion);
            WriteString(writer, generation.Stream);
            WriteString(writer, generation.GenerationId);
            writer.Write(generation.Revision);
            WriteString(writer, queryFingerprint);
            writer.Write(continuationState.Length);
            writer.Write(continuationState);
        }

        byte[] payload = payloadStream.ToArray();
        byte[] signed = new byte[payload.Length + SignatureLength];
        payload.CopyTo(signed, 0);
        ComputeSignature(payload).CopyTo(signed, payload.Length);
        return Base64UrlEncode(signed);
    }

    internal static byte[] Decode(
        string cursor,
        DatabaseGeneration generation,
        string queryFingerprint)
    {
        byte[] signed;
        try
        {
            signed = Base64UrlDecode(cursor);
        }
        catch (FormatException exception)
        {
            throw InvalidCursor(exception);
        }

        if (signed.Length <= SignatureLength)
            throw InvalidCursor();
        ReadOnlySpan<byte> payload = signed.AsSpan(0, signed.Length - SignatureLength);
        ReadOnlySpan<byte> actualSignature = signed.AsSpan(payload.Length, SignatureLength);
        if (!CryptographicOperations.FixedTimeEquals(actualSignature, ComputeSignature(payload)))
            throw InvalidCursor();

        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, _strictUtf8, leaveOpen: false);
            if (reader.ReadInt32() != CurrentVersion)
                throw InvalidCursor();
            string streamName = ReadString(reader);
            string generationId = ReadString(reader);
            long revision = reader.ReadInt64();
            string fingerprint = ReadString(reader);
            int stateLength = reader.ReadInt32();
            if (stateLength < 0 || stateLength > stream.Length - stream.Position)
                throw InvalidCursor();
            byte[] state = reader.ReadBytes(stateLength);
            if (state.Length != stateLength || stream.Position != stream.Length)
                throw InvalidCursor();

            if (!string.Equals(streamName, generation.Stream, StringComparison.Ordinal))
            {
                throw new DatabaseGenerationException(
                    DatabaseGenerationErrorCodes.CursorMismatch,
                    "generation cursor 不属于当前 stream。");
            }
            if (revision != generation.Revision
                || !string.Equals(generationId, generation.GenerationId, StringComparison.Ordinal))
            {
                throw new DatabaseGenerationException(
                    DatabaseGenerationErrorCodes.CursorStale,
                    "generation cursor 绑定的 revision 与当前 query lease 不一致。");
            }
            if (!string.Equals(fingerprint, queryFingerprint, StringComparison.Ordinal))
            {
                throw new DatabaseGenerationException(
                    DatabaseGenerationErrorCodes.CursorMismatch,
                    "generation cursor 不属于当前查询形状。");
            }
            return state;
        }
        catch (DatabaseGenerationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or DecoderFallbackException)
        {
            throw InvalidCursor(exception);
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = _strictUtf8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw InvalidCursor();
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw InvalidCursor();
        return _strictUtf8.GetString(bytes);
    }

    private static byte[] ComputeSignature(ReadOnlySpan<byte> payload)
    {
        byte[] input = new byte[_signatureDomain.Length + payload.Length];
        _signatureDomain.CopyTo(input, 0);
        payload.CopyTo(input.AsSpan(_signatureDomain.Length));
        return SHA256.HashData(input);
    }

    private static DatabaseGenerationException InvalidCursor(Exception? innerException = null)
        => innerException is null
            ? new DatabaseGenerationException(
                DatabaseGenerationErrorCodes.CursorInvalid,
                "generation cursor 无效。")
            : new DatabaseGenerationException(
                DatabaseGenerationErrorCodes.CursorInvalid,
                "generation cursor 无效。",
                innerException);

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        string normalized = text.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };
        return Convert.FromBase64String(normalized);
    }
}
