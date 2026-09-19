using SkiaSharp;
using SonnetDB.Exceptions;
using TiffLibrary;
using TiffLibrary.PixelFormats;

namespace SonnetDB.SemanticSearch;

/// <summary>Server 图片处理边界：明确格式、首帧、方向、像素预算和预处理版本。</summary>
internal static class SemanticImageCodec
{
    // 两个 RGBA8 缓冲区可能同时存在（解码器输出和 Skia 位图）。16 MP 将这段
    // 临时峰值限制在约 128 MiB，避免小尺寸压缩炸弹把 Server 进程推入 OOM。
    internal const int MaxDecodedPixels = 16_000_000;
    internal const string PreprocessingVersion = "skia-rgba-v1";

    // 能力由 SonnetDB 维护，不能随第三方 codec 的注册清单自动扩张。
    internal static IReadOnlyList<string> ContentTypes { get; } = Array.AsReadOnly(new[]
    {
        "image/png", "image/apng", "image/jpeg", "image/pjpeg", "image/webp", "image/gif",
        "image/bmp", "image/x-windows-bmp", "image/x-win-bitmap",
        "image/vnd.microsoft.icon", "image/x-icon", "image/ico", "image/icon",
        "image/tiff", "image/tiff-fx",
    });

    internal static bool IsSupportedContentType(string contentType)
        => ContentTypes.Contains(contentType.Split(';', 2)[0].Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>解码第一帧/第一页，检查像素预算，输出已归向的非预乘 RGBA8。</summary>
    internal static SKBitmap Decode(ReadOnlyMemory<byte> encodedImage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<byte> header = encodedImage.Span;
        if (header.IsEmpty)
            throw new ImageInputException("图片内容不能为空。");
        if (header.Length >= 4 && ((header[0] == 'I' && header[1] == 'I' && header[3] == 0 && header[2] is 42 or 43)
            || (header[0] == 'M' && header[1] == 'M' && header[2] == 0 && header[3] is 42 or 43)))
            return DecodeTiff(encodedImage, cancellationToken);

        // CUR 与 ICO 共用部分解码实现，但 CUR 不属于对外声明的七类格式。
        if (header.Length >= 4 && header[0] == 0 && header[1] == 0 && header[2] == 2 && header[3] == 0)
            throw new ImageInputException("不支持 CUR 图片；支持 PNG、JPEG、WebP、GIF、BMP、ICO、TIFF。");

        try
        {
            using var data = SKData.CreateCopy(header);
            using var codec = SKCodec.Create(data)
                ?? throw new ImageInputException("无法识别图片格式或图片头已损坏。");
            if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg
                or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Gif or SKEncodedImageFormat.Bmp or SKEncodedImageFormat.Ico))
                throw new ImageInputException("支持的图片格式为 PNG、JPEG、WebP、GIF、BMP、ICO、TIFF。");

            ValidateDimensions(codec.Info.Width, codec.Info.Height);
            using var colorSpace = SKColorSpace.CreateSrgb();
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, colorSpace);
            var bitmap = new SKBitmap(info);
            try
            {
                var result = codec.GetPixels(info, bitmap.GetPixels(), bitmap.RowBytes, SKCodecOptions.Default);
                if (result != SKCodecResult.Success)
                    throw new ImageInputException($"图片解码失败：{result}。");
                cancellationToken.ThrowIfCancellationRequested();
                if (codec.EncodedOrigin is SKEncodedOrigin.TopLeft)
                    return bitmap;
                var oriented = Orient(bitmap, codec.EncodedOrigin, cancellationToken);
                bitmap.Dispose();
                return oriented;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new ImageInputException("图片内容损坏或编码方式不受支持。", exception);
        }
    }

    /// <summary>按版本化的 sRGB、双线性 Stretch、RGB NCHW 合同生成模型输入。</summary>
    internal static float[] Preprocess(ReadOnlyMemory<byte> encodedImage, int imageSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDimensions(imageSize, imageSize);
        using var decoded = Decode(encodedImage, cancellationToken);
        // 模型只接收 RGB；忽略 alpha，避免缩放时隐式预乘将透明像素变成背景色。
        Span<byte> decodedPixels = decoded.GetPixelSpan();
        for (int y = 0; y < decoded.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < decoded.Width; x++)
                decodedPixels[y * decoded.RowBytes + x * 4 + 3] = 255;
        }
        using var resized = decoded.Resize(new SKSizeI(imageSize, imageSize),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidOperationException("图片预处理缩放失败。");
        int planeSize = checked(imageSize * imageSize);
        var result = new float[checked(planeSize * 3)];
        ReadOnlySpan<byte> pixels = resized.GetPixelSpan();
        for (int y = 0; y < imageSize; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < imageSize; x++)
            {
                int offset = y * imageSize + x;
                int pixel = y * resized.RowBytes + x * 4;
                result[offset] = pixels[pixel] / 127.5f - 1f;
                result[planeSize + offset] = pixels[pixel + 1] / 127.5f - 1f;
                result[planeSize * 2 + offset] = pixels[pixel + 2] / 127.5f - 1f;
            }
        }
        return result;
    }

    /// <summary>生成单帧 WebP 缩略图，等比缩小且不放大小图。</summary>
    internal static byte[] CreateThumbnail(ReadOnlyMemory<byte> encodedImage, int maxWidth, int maxHeight, int quality,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        using var decoded = Decode(encodedImage, cancellationToken);
        double scale = Math.Min(1d, Math.Min((double)maxWidth / decoded.Width, (double)maxHeight / decoded.Height));
        int width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
        int height = Math.Max(1, (int)Math.Round(decoded.Height * scale));
        using var resized = scale < 1d
            ? decoded.Resize(new SKSizeI(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell))
                ?? throw new InvalidOperationException("缩略图缩放失败。")
            : null;
        cancellationToken.ThrowIfCancellationRequested();
        using var image = SKImage.FromBitmap(resized ?? decoded);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, quality)
            ?? throw new InvalidOperationException("WebP 编码失败。");
        cancellationToken.ThrowIfCancellationRequested();
        return encoded.ToArray();
    }

    private static SKBitmap DecodeTiff(ReadOnlyMemory<byte> encodedImage, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = TiffFileReader.Open(encodedImage);
            var directory = reader.ReadImageFileDirectory();
            using var fields = reader.CreateFieldReader();
            var tags = new TiffTagReader(fields, directory);
            // 在构建解码管道和分配像素数组前检查原始尺寸；只读取第一页。
            ulong width = tags.ReadLong8Field(TiffTag.ImageWidth, sizeLimit: 1).GetFirstOrDefault();
            ulong height = tags.ReadLong8Field(TiffTag.ImageLength, sizeLimit: 1).GetFirstOrDefault();
            if (width > MaxDecodedPixels || height > MaxDecodedPixels)
                throw new ImageInputException($"图片像素数不能超过 {MaxDecodedPixels}。");
            ValidateDimensions((long)width, (long)height);
            cancellationToken.ThrowIfCancellationRequested();
            var decoder = reader.CreateImageDecoder(directory, new TiffImageDecoderOptions
            {
                IgnoreOrientation = false,
                UndoColorPreMultiplying = true,
            });
            ValidateDimensions(decoder.Width, decoder.Height);
            var pixels = new TiffRgba32[checked(decoder.Width * decoder.Height)];
            // 托管 TIFF 解码器可在处理 strip/tile 时观察取消；同步 Server 边界不丢弃该能力。
            decoder.DecodeAsync(new TiffMemoryPixelBuffer<TiffRgba32>(pixels, decoder.Width, decoder.Height, writable: true),
                cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            using var colorSpace = SKColorSpace.CreateSrgb();
            var bitmap = new SKBitmap(new SKImageInfo(decoder.Width, decoder.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, colorSpace));
            try
            {
                Span<byte> target = bitmap.GetPixelSpan();
                for (int y = 0; y < bitmap.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        var pixel = pixels[y * bitmap.Width + x];
                        int offset = y * bitmap.RowBytes + x * 4;
                        target[offset] = pixel.R;
                        target[offset + 1] = pixel.G;
                        target[offset + 2] = pixel.B;
                        target[offset + 3] = pixel.A;
                    }
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException
            or NotSupportedException or ArgumentException or OverflowException)
        {
            // 此边界仅访问已读取的内存，不能将外部对象存储的 IOException 当作坏图片吞掉。
            throw new ImageInputException("TIFF 图片内容损坏或编码方式不受支持。", exception);
        }
    }

    private static void ValidateDimensions(long width, long height)
    {
        if (width <= 0 || height <= 0 || width > MaxDecodedPixels || height > MaxDecodedPixels
            || width * height > MaxDecodedPixels)
            throw new ImageInputException($"图片像素数必须在 1 到 {MaxDecodedPixels} 之间。");
    }

    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin, CancellationToken cancellationToken)
    {
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var target = new SKBitmap(source.Info.WithSize(swap ? source.Height : source.Width, swap ? source.Width : source.Height));
        try
        {
            ReadOnlySpan<byte> input = source.GetPixelSpan();
            Span<byte> output = target.GetPixelSpan();
            for (int y = 0; y < source.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < source.Width; x++)
                {
                    (int targetX, int targetY) = origin switch
                    {
                        SKEncodedOrigin.TopRight => (source.Width - 1 - x, y),
                        SKEncodedOrigin.BottomRight => (source.Width - 1 - x, source.Height - 1 - y),
                        SKEncodedOrigin.BottomLeft => (x, source.Height - 1 - y),
                        SKEncodedOrigin.LeftTop => (y, x),
                        SKEncodedOrigin.RightTop => (source.Height - 1 - y, x),
                        SKEncodedOrigin.RightBottom => (source.Height - 1 - y, source.Width - 1 - x),
                        SKEncodedOrigin.LeftBottom => (y, source.Width - 1 - x),
                        _ => (x, y),
                    };
                    input.Slice(y * source.RowBytes + x * 4, 4).CopyTo(output.Slice(targetY * target.RowBytes + targetX * 4, 4));
                }
            }
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }
}
