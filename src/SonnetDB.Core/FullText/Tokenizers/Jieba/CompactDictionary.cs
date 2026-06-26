using System.Buffers.Binary;
using System.Text;

namespace SonnetDB.FullText.Tokenizers.Jieba;

internal sealed class CompactDictionary
{
    private readonly int[] _offsets;
    private readonly int[] _lengths;
    private readonly int[] _frequencies;
    private readonly byte[] _terms;

    private CompactDictionary(int[] offsets, int[] lengths, int[] frequencies, byte[] terms)
    {
        _offsets = offsets;
        _lengths = lengths;
        _frequencies = frequencies;
        _terms = terms;
    }

    public int GetFrequency(ReadOnlySpan<char> term)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(term.Length);
        Span<byte> stack = maxBytes <= 256 ? stackalloc byte[maxBytes] : [];
        byte[]? rented = null;
        Span<byte> buffer = stack;
        if (buffer.IsEmpty)
        {
            rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes);
            buffer = rented;
        }

        try
        {
            int written = Encoding.UTF8.GetBytes(term, buffer);
            ReadOnlySpan<byte> key = buffer[..written];
            int lo = 0;
            int hi = _offsets.Length - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                ReadOnlySpan<byte> current = _terms.AsSpan(_offsets[mid], _lengths[mid]);
                int cmp = current.SequenceCompareTo(key);
                if (cmp == 0)
                    return _frequencies[mid];
                if (cmp < 0)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }
            return 0;
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static CompactDictionary Read(byte[] data, out int count, out long totalFrequency, out int maxTermLength)
    {
        ReadOnlySpan<byte> span = data;
        if (!span[..8].SequenceEqual("DSDAT002"u8))
            throw new FormatException("Invalid compact dictionary.");

        int cursor = 8;
        count = ReadInt32(span, ref cursor);
        totalFrequency = ReadInt64(span, ref cursor);
        maxTermLength = ReadInt32(span, ref cursor);

        int[] offsets = new int[count];
        int[] lengths = new int[count];
        int[] frequencies = new int[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = ReadInt32(span, ref cursor);
            lengths[i] = ReadInt32(span, ref cursor);
            frequencies[i] = ReadInt32(span, ref cursor);
        }

        byte[] terms = span[cursor..].ToArray();
        return new CompactDictionary(offsets, lengths, frequencies, terms);
    }

    private static int ReadInt32(ReadOnlySpan<byte> span, ref int cursor)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(cursor, sizeof(int)));
        cursor += sizeof(int);
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> span, ref int cursor)
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(cursor, sizeof(long)));
        cursor += sizeof(long);
        return value;
    }
}
