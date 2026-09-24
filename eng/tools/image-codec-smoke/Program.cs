using SkiaSharp;
using SonnetDB.Exceptions;
using SonnetDB.SemanticSearch;
using SonnetDB.Tests;

// 此探针直接编译 Server 使用的同一实现，验证发布物实际加载原生库并处理七类图片。
using var bitmap = new SKBitmap(new SKImageInfo(8, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
bitmap.Erase(SKColors.Red);
foreach (var format in new[] { SKEncodedImageFormat.Png, SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Webp })
    CheckImage(format.ToString(), ImageTestFixtures.Encode(bitmap, format), 8, 4);
CheckImage("GIF", ImageTestFixtures.CreateAnimatedGif(), 1, 1);
CheckImage("BMP", ImageTestFixtures.CreateBmp(), 2, 1);
CheckImage("ICO", ImageTestFixtures.CreateIcon(), 2, 2);
for (ushort orientation = 1; orientation <= 8; orientation++)
{
    bool swap = orientation >= 5;
    CheckImage($"TIFF orientation {orientation}", ImageTestFixtures.CreateTiff(orientation), swap ? 3 : 2, swap ? 2 : 3);
    CheckImage($"JPEG orientation {orientation}", ImageTestFixtures.CreateExifJpeg(orientation), swap ? 36 : 24, swap ? 24 : 36);
}
foreach (byte[] invalid in new[] { "invalid"u8.ToArray(), ImageTestFixtures.CreateOversizedTiff(), ImageTestFixtures.CreateIcon(cursor: true) })
{
    try
    {
        using var unexpected = SemanticImageCodec.Decode(invalid);
        throw new InvalidOperationException("非法或超限图片未被拒绝。");
    }
    catch (ImageInputException)
    {
        Console.WriteLine("PASS invalid/unsupported/oversized input rejected");
    }
}
Console.WriteLine($"PASS image codec smoke ({SemanticImageCodec.PreprocessingVersion})");

static void CheckImage(string name, byte[] encoded, int width, int height)
{
    using var decoded = SemanticImageCodec.Decode(encoded);
    if (decoded.Width != width || decoded.Height != height)
        throw new InvalidOperationException($"{name} 尺寸不符：{decoded.Width}x{decoded.Height}。");
    float[] tensor = SemanticImageCodec.Preprocess(encoded, 8);
    if (tensor.Length != 3 * 8 * 8 || tensor.Any(static pixel => !float.IsFinite(pixel) || pixel < -1 || pixel > 1))
        throw new InvalidOperationException($"{name} RGB NCHW 输入不合法。");
    byte[] thumbnail = SemanticImageCodec.CreateThumbnail(encoded, 4, 4, 80);
    using var data = SKData.CreateCopy(thumbnail);
    using var codec = SKCodec.Create(data);
    if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Webp
        || codec.Info.Width > Math.Min(width, 4) || codec.Info.Height > Math.Min(height, 4))
        throw new InvalidOperationException($"{name} WebP 缩略图不合法。");
    using var roundTrip = SemanticImageCodec.Decode(thumbnail);
    Console.WriteLine($"PASS {name}: {width}x{height}, tensor={tensor.Length}, WebP={roundTrip.Width}x{roundTrip.Height}");
}
