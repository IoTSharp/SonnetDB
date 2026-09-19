using SkiaSharp;
using SonnetDB.Configuration;
using SonnetDB.Exceptions;
using SonnetDB.SemanticSearch;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>验证真实图片解码、方向处理、模型输入与缩略图合同。</summary>
public sealed class ImageProcessingTests
{
    /// <summary>本地预处理版本自动加入 profile，防止新旧图片向量混用。</summary>
    [Fact]
    public void SigLip2Provider_ConfiguredProfile_IsolatesSkiaPreprocessing()
    {
        using var provider = new SigLip2OnnxEmbeddingProvider(new SemanticSearchOptions { Profile = "model-a" });
        Assert.Equal("model-a:skia-rgba-v1", provider.Info.Profile);
    }

    /// <summary>手工 TIFF fixture 覆盖所有旋转与镜像，且保留精确 RGB 像素。</summary>
    [Theory]
    [InlineData(1, 2, 3, "ABCDEF")]
    [InlineData(2, 2, 3, "BADCFE")]
    [InlineData(3, 2, 3, "FEDCBA")]
    [InlineData(4, 2, 3, "EFCDAB")]
    [InlineData(5, 3, 2, "ACEBDF")]
    [InlineData(6, 3, 2, "ECAFDB")]
    [InlineData(7, 3, 2, "FDBECA")]
    [InlineData(8, 3, 2, "BDFACE")]
    public void Decode_TiffOrientation_ReturnsExpectedPixels(int orientation, int width, int height, string expected)
    {
        using var decoded = SemanticImageCodec.Decode(ImageTestFixtures.CreateTiff((ushort)orientation));
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(SKColorType.Rgba8888, decoded.ColorType);
        Assert.Equal(SKAlphaType.Unpremul, decoded.AlphaType);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(ImageTestFixtures.Colors[expected[i] - 'A'], decoded.GetPixel(i % width, i / width));
    }

    /// <summary>JPEG 的 EXIF 由独立 APP1 fixture 提供，颜色比较允许有损编码误差。</summary>
    [Theory]
    [InlineData(1, 2, 3, "ABCDEF")]
    [InlineData(2, 2, 3, "BADCFE")]
    [InlineData(3, 2, 3, "FEDCBA")]
    [InlineData(4, 2, 3, "EFCDAB")]
    [InlineData(5, 3, 2, "ACEBDF")]
    [InlineData(6, 3, 2, "ECAFDB")]
    [InlineData(7, 3, 2, "FDBECA")]
    [InlineData(8, 3, 2, "BDFACE")]
    public void Decode_JpegExifOrientation_ReturnsExpectedPixels(int orientation, int columns, int rows, string expected)
    {
        using var decoded = SemanticImageCodec.Decode(ImageTestFixtures.CreateExifJpeg((ushort)orientation));
        Assert.Equal(columns * 12, decoded.Width);
        Assert.Equal(rows * 12, decoded.Height);
        for (int i = 0; i < expected.Length; i++)
        {
            SKColor actual = decoded.GetPixel(i % columns * 12 + 6, i / columns * 12 + 6);
            SKColor color = ImageTestFixtures.Colors[expected[i] - 'A'];
            Assert.InRange(Math.Abs(actual.Red - color.Red), 0, 4);
            Assert.InRange(Math.Abs(actual.Green - color.Green), 0, 4);
            Assert.InRange(Math.Abs(actual.Blue - color.Blue), 0, 4);
        }
    }

    /// <summary>PNG 的透明像素保留未预乘 RGB，模型输入忽略 alpha 而不混合背景。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(255)]
    public void Preprocess_TransparentPng_PreservesRgbWithoutBackground(int alpha)
    {
        byte[] png = ImageTestFixtures.CreatePng(1, 1, new SKColor(50, 100, 150, (byte)alpha));
        using var decoded = SemanticImageCodec.Decode(png);
        Assert.Equal(new SKColor(50, 100, 150, (byte)alpha), decoded.GetPixel(0, 0));
        float[] tensor = SemanticImageCodec.Preprocess(png, 2);
        Assert.Equal(12, tensor.Length);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(50 / 127.5f - 1, tensor[i], 5);
            Assert.Equal(100 / 127.5f - 1, tensor[4 + i], 5);
            Assert.Equal(150 / 127.5f - 1, tensor[8 + i], 5);
        }
    }

    /// <summary>不同位置的颜色验证 NCHW 的平面顺序和归一化边界。</summary>
    [Fact]
    public void Preprocess_ColoredPixels_ReturnsNormalizedRgbPlanes()
    {
        using var bitmap = new SKBitmap(2, 2);
        for (int i = 0; i < 4; i++)
            bitmap.SetPixel(i % 2, i / 2, ImageTestFixtures.Colors[i]);
        float[] tensor = SemanticImageCodec.Preprocess(ImageTestFixtures.Encode(bitmap, SKEncodedImageFormat.Png), 2);
        Assert.Equal(new[] { 1f, -1f, -1f, 1f, -1f, 1f, -1f, 1f, -1f, -1f, 1f, 1f }, tensor);
    }

    /// <summary>缩略图按最大尺寸等比缩小，小图保持原始尺寸并输出单帧 WebP。</summary>
    [Theory]
    [InlineData(64, 32, 32, 32, 32, 16)]
    [InlineData(32, 64, 32, 32, 16, 32)]
    [InlineData(3, 2, 32, 32, 3, 2)]
    [InlineData(1, 100, 32, 32, 1, 32)]
    public void CreateThumbnail_ImageBounds_ReturnsExpectedWebp(int width, int height, int maxWidth,
        int maxHeight, int expectedWidth, int expectedHeight)
    {
        byte[] result = SemanticImageCodec.CreateThumbnail(
            ImageTestFixtures.CreatePng(width, height, SKColors.Red), maxWidth, maxHeight, 80);
        using var stream = new MemoryStream(result, writable: false);
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.Equal(expectedWidth, codec.Info.Width);
        Assert.Equal(expectedHeight, codec.Info.Height);
        Assert.InRange(codec.FrameCount, 0, 1);
        using var decoded = SKBitmap.Decode(result);
        Assert.NotNull(decoded);
        Assert.Equal(expectedWidth, decoded.Width);
    }

    /// <summary>生成缩略图前必须先应用 EXIF，竖横图才能使用正确的尺寸约束。</summary>
    [Fact]
    public void CreateThumbnail_RotatedTiff_AppliesOrientationBeforeSizing()
    {
        byte[] result = SemanticImageCodec.CreateThumbnail(ImageTestFixtures.CreateTiff(6), 2, 1, 100);
        using var decoded = SKBitmap.Decode(result);
        Assert.NotNull(decoded);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(1, decoded.Height);
    }

    /// <summary>GIF、BMP、ICO 和 WebP 的实际字节可以进入相同的解码入口。</summary>
    [Theory]
    [InlineData("gif", 1, 1)]
    [InlineData("bmp", 2, 1)]
    [InlineData("ico", 2, 2)]
    [InlineData("webp", 2, 2)]
    public void Decode_SupportedFormats_ReturnsExpectedSize(string format, int width, int height)
    {
        byte[] encoded;
        if (format == "webp")
        {
            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.Red);
            encoded = ImageTestFixtures.Encode(bitmap, SKEncodedImageFormat.Webp);
        }
        else
        {
            encoded = format switch
            {
                "gif" => ImageTestFixtures.CreateAnimatedGif(),
                "bmp" => ImageTestFixtures.CreateBmp(),
                _ => ImageTestFixtures.CreateIcon(),
            };
        }
        using var decoded = SemanticImageCodec.Decode(encoded);
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
    }

    /// <summary>动画输入只派生第一帧，避免后台任务意外处理全部动画帧。</summary>
    [Fact]
    public void CreateThumbnail_AnimatedGif_ReturnsFirstFrameOnly()
    {
        byte[] result = SemanticImageCodec.CreateThumbnail(ImageTestFixtures.CreateAnimatedGif(), 32, 32, 100);
        using var decoded = SKBitmap.Decode(result);
        Assert.NotNull(decoded);
        Assert.Equal(1, decoded.Width);
        Assert.Equal(1, decoded.Height);
        SKColor color = decoded.GetPixel(0, 0);
        Assert.InRange(color.Red, (byte)250, byte.MaxValue);
        Assert.InRange(color.Blue, (byte)0, (byte)5);
        using var stream = new MemoryStream(result, writable: false);
        using var codec = SKCodec.Create(stream);
        Assert.InRange(codec.FrameCount, 0, 1);
    }

    /// <summary>共享 ICO 容器的 CUR 及 Skia 可识别的额外格式仍受项目白名单约束。</summary>
    [Fact]
    public void Decode_CursorAndWbmp_ThrowsImageInputException()
    {
        Assert.Throws<ImageInputException>(() => SemanticImageCodec.Decode(ImageTestFixtures.CreateIcon(cursor: true)));
        Assert.Throws<ImageInputException>(() => SemanticImageCodec.Decode(new byte[] { 0, 0, 1, 1, 0 }));
    }

    /// <summary>空内容和非法编码统一归类为输入异常。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("010203")]
    [InlineData("47494638396101000100")]
    public void Decode_UnknownOrTruncatedInput_ThrowsImageInputException(string hex)
        => Assert.Throws<ImageInputException>(() => SemanticImageCodec.Decode(Convert.FromHexString(hex)));

    /// <summary>格式被识别但像素数据截断时同样报告输入错误。</summary>
    [Fact]
    public void Decode_TruncatedTiff_ThrowsImageInputException()
        => Assert.Throws<ImageInputException>(() => SemanticImageCodec.Decode(ImageTestFixtures.CreateTiff()[..152]));

    /// <summary>声明超限尺寸的 TIFF 在解码前被拒绝。</summary>
    [Fact]
    public void Decode_TooManyPixels_ThrowsImageInputException()
        => Assert.Throws<ImageInputException>(() => SemanticImageCodec.Decode(ImageTestFixtures.CreateOversizedTiff()));

    /// <summary>取消信号不能被转换为输入损坏异常。</summary>
    [Fact]
    public void Decode_CancelledRequest_ThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        byte[] png = ImageTestFixtures.CreatePng(2, 2, SKColors.Red);
        Assert.ThrowsAny<OperationCanceledException>(() => SemanticImageCodec.Decode(png, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SemanticImageCodec.Preprocess(png, 2, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SemanticImageCodec.CreateThumbnail(png, 2, 2, 80, cancellation.Token));
    }

    /// <summary>对象能力清单只声明可解码的图片和显式支持的文本类型。</summary>
    [Fact]
    public void ContentTypes_DeclaredFormats_ExcludesRemovedAndAmbiguousAliases()
    {
        Assert.Contains("image/tiff", SemanticImageCodec.ContentTypes);
        Assert.Contains("image/vnd.microsoft.icon", SemanticImageCodec.ContentTypes);
        Assert.DoesNotContain("text/ico", SemanticImageCodec.ContentTypes);
        Assert.DoesNotContain("image/x-exr", SemanticImageCodec.ContentTypes);
        Assert.DoesNotContain("application/x-navi-animation", SemanticImageCodec.ContentTypes);
        Assert.All(SemanticImageCodec.ContentTypes, type => Assert.StartsWith("image/", type));
    }
}
