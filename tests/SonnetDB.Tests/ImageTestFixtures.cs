using System.Buffers.Binary;
using System.IO.Compression;
using SkiaSharp;

namespace SonnetDB.Tests;

/// <summary>独立构造图片协议 fixture，避免使用待测 TIFF 解码器生成自身输入。</summary>
internal static class ImageTestFixtures
{
    internal static readonly SKColor[] Colors = [SKColors.Red, SKColors.Lime, SKColors.Blue,
        SKColors.White, SKColors.Cyan, SKColors.Yellow];

    internal static byte[] CreateTiff(ushort orientation = 1)
    {
        // little-endian TIFF：2 × 3、RGB8、单 strip、无压缩；IFD 后为 BitsPerSample 与像素。
        const uint bitsOffset = 146;
        const uint pixelsOffset = bitsOffset + 6;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write(8u);
        writer.Write((ushort)11);
        WriteEntry(writer, 256, 4, 1, 2);
        WriteEntry(writer, 257, 4, 1, 3);
        WriteEntry(writer, 258, 3, 3, bitsOffset);
        WriteEntry(writer, 259, 3, 1, 1);
        WriteEntry(writer, 262, 3, 1, 2);
        WriteEntry(writer, 273, 4, 1, pixelsOffset);
        WriteEntry(writer, 274, 3, 1, orientation);
        WriteEntry(writer, 277, 3, 1, 3);
        WriteEntry(writer, 278, 4, 1, 3);
        WriteEntry(writer, 279, 4, 1, 18);
        WriteEntry(writer, 284, 3, 1, 1);
        writer.Write(0u);
        writer.Write((ushort)8);
        writer.Write((ushort)8);
        writer.Write((ushort)8);
        foreach (SKColor color in Colors)
        {
            writer.Write(color.Red);
            writer.Write(color.Green);
            writer.Write(color.Blue);
        }
        return output.ToArray();
    }

    internal static byte[] CreatePng(int width, int height, SKColor color)
    {
        // 直接写 RGBA8 PNG，完整保留 alpha=0 的隐藏 RGB；Skia 编码会将它清零。
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(output, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[checked(width * 4 + 1)];
            for (int x = 0; x < width; x++)
            {
                int offset = 1 + x * 4;
                row[offset] = color.Red;
                row[offset + 1] = color.Green;
                row[offset + 2] = color.Blue;
                row[offset + 3] = color.Alpha;
            }
            for (int y = 0; y < height; y++)
                zlib.Write(row);
        }
        WritePngChunk(output, "IDAT"u8, compressed.ToArray());
        WritePngChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    internal static byte[] CreateExifJpeg(ushort orientation)
    {
        using var bitmap = new SKBitmap(24, 36);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, Colors[y / 12 * 2 + x / 12]);
        byte[] jpeg = Encode(bitmap, SKEncodedImageFormat.Jpeg);
        using var output = new MemoryStream();
        output.Write(jpeg.AsSpan(0, 2));
        output.Write([0xff, 0xe1, 0, 34]);
        output.Write("Exif\0\0"u8);
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write(8u);
        writer.Write((ushort)1);
        WriteEntry(writer, 274, 3, 1, orientation);
        writer.Write(0u);
        output.Write(jpeg.AsSpan(2));
        return output.ToArray();
    }

    internal static byte[] CreateOversizedTiff()
    {
        byte[] encoded = CreateTiff();
        // 只修改 IFD 尺寸；解码器必须在尝试分配 4 亿像素前拒绝此输入。
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(18, 4), 20_000);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(30, 4), 20_000);
        return encoded;
    }

    internal static byte[] CreateAnimatedGif()
        => Convert.FromHexString(
            "47494638396101000100800000FF00000000FF" +
            "21F90400010000002C0000000001000100000202440100" +
            "21F90400010000002C00000000010001000002024C01003B");

    internal static byte[] CreateBmp()
        => Convert.FromHexString(
            "424D3E00000000000000360000002800000002000000010000000100180000000000" +
            "0800000000000000000000000000000000000000" +
            "0000FFFF00000000");

    internal static byte[] CreateIcon(bool cursor = false)
    {
        byte[] png = CreatePng(2, 2, SKColors.Red);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)(cursor ? 2 : 1));
        writer.Write((ushort)1);
        writer.Write((byte)2);
        writer.Write((byte)2);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(png.Length);
        writer.Write(22u);
        writer.Write(png);
        return output.ToArray();
    }

    internal static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 100);
        return encoded.ToArray();
    }

    private static void WriteEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        writer.Write(value);
    }

    private static void WritePngChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> content)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, content.Length);
        output.Write(number);
        output.Write(type);
        output.Write(content);
        uint crc = UpdateCrc(UpdateCrc(uint.MaxValue, type), content) ^ uint.MaxValue;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
        }
        return crc;
    }
}
