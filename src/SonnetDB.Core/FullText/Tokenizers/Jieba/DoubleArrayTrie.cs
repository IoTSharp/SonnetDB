using System.Buffers.Binary;
using System.Text;

namespace SonnetDB.FullText.Tokenizers.Jieba;

internal sealed class DoubleArrayTrie
{
    private static readonly byte[] _magic = "DSDAT001"u8.ToArray();

    private readonly int[] _base;
    private readonly int[] _check;
    private readonly int[] _frequency;

    public DoubleArrayTrie(int[] @base, int[] check, int[] frequency)
    {
        _base = @base;
        _check = check;
        _frequency = frequency;
    }

    public int GetFrequency(ReadOnlySpan<char> term)
    {
        int state = 0;
        foreach (Rune rune in term.EnumerateRunes())
        {
            int code = rune.Value + 1;
            int next = Transition(state, code);
            if (next < 0)
            {
                return 0;
            }
            state = next;
        }

        return state < _frequency.Length ? _frequency[state] : 0;
    }

    public static DoubleArrayTrie Read(Stream stream, out int count, out long totalFrequency, out int maxTermLength)
    {
        Span<byte> magic = stackalloc byte[_magic.Length];
        ReadExactly(stream, magic);
        if (!magic.SequenceEqual(_magic))
        {
            throw new FormatException("Invalid DAT dictionary.");
        }

        int nodeCount = ReadInt32(stream);
        count = ReadInt32(stream);
        totalFrequency = ReadInt64(stream);
        maxTermLength = ReadInt32(stream);

        int[] @base = new int[nodeCount];
        int[] check = new int[nodeCount];
        int[] frequency = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            @base[i] = ReadInt32(stream);
            check[i] = ReadInt32(stream);
            frequency[i] = ReadInt32(stream);
        }

        return new DoubleArrayTrie(@base, check, frequency);
    }

    private int Transition(int state, int code)
    {
        if (state >= _base.Length)
        {
            return -1;
        }

        int next = _base[state] + code;
        return next > 0 && next < _check.Length && _check[next] == state ? next : -1;
    }

    private static int ReadInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        ReadExactly(stream, buffer);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static long ReadInt64(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        ReadExactly(stream, buffer);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }
}
