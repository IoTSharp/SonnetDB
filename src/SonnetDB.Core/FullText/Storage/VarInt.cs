namespace SonnetDB.FullText.Storage;

internal static class VarInt
{
    public static void Write(Stream stream, int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        uint remaining = (uint)value;
        while (remaining >= 0x80)
        {
            stream.WriteByte((byte)(remaining | 0x80));
            remaining >>= 7;
        }
        stream.WriteByte((byte)remaining);
    }

    public static int Read(Stream stream)
    {
        int shift = 0;
        int value = 0;

        while (shift <= 28)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException();
            }

            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
            shift += 7;
        }

        throw new FormatException("VarInt is too long.");
    }
}
