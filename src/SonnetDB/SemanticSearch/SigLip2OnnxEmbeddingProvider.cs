using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SonnetDB.Configuration;

namespace SonnetDB.SemanticSearch;

/// <summary>
/// 使用独立文本/视觉 ONNX 编码器实现 SigLIP2 多模态 embedding。
/// 模型和 tokenizer 均由部署者以本地文件提供，provider 不执行网络下载。
/// </summary>
public sealed class SigLip2OnnxEmbeddingProvider : IMultimodalEmbeddingProvider, IDisposable
{
    private readonly object _initializationSync = new();
    private readonly SemanticSearchOptions _options;
    private InferenceSession? _textSession;
    private InferenceSession? _visionSession;
    private SentencePieceTokenizer? _tokenizer;
    private bool _disposed;

    /// <summary>
    /// 初始化 SigLIP2 ONNX provider。
    /// </summary>
    /// <param name="options">模型、tensor 名称和预处理配置。</param>
    public SigLip2OnnxEmbeddingProvider(SemanticSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        string? reason = ValidateConfiguration(options);
        Info = new MultimodalEmbeddingProviderInfo(
            "siglip2-onnx",
            options.Profile,
            options.Dimensions,
            reason is null,
            reason);
    }

    /// <inheritdoc />
    public MultimodalEmbeddingProviderInfo Info { get; }

    /// <inheritdoc />
    public ValueTask<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        EnsureReady();
        cancellationToken.ThrowIfCancellationRequested();

        var tokenizer = EnsureTokenizer();
        IReadOnlyList<int> tokenIds = tokenizer.EncodeToIds(
            text.ToLowerInvariant(),
            addBeginningOfSentence: false,
            addEndOfSentence: true,
            _options.MaxTextTokens,
            out _,
            out _);

        var padded = new long[_options.MaxTextTokens];
        int count = Math.Min(tokenIds.Count, padded.Length);
        for (int i = 0; i < count; i++)
            padded[i] = tokenIds[i];

        var tensor = new DenseTensor<long>(padded, [1, padded.Length]);
        float[] embedding = RunEncoder(
            EnsureTextSession(),
            _options.TextInputName,
            _options.TextOutputName,
            tensor);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NormalizeAndValidate(embedding));
    }

    /// <inheritdoc />
    public ValueTask<float[]> EmbedImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (image.IsEmpty)
            throw new ArgumentException("图片内容不能为空。", nameof(image));
        if (image.Length > _options.MaxImageBytes)
            throw new ArgumentOutOfRangeException(nameof(image), $"图片不能超过 {_options.MaxImageBytes} 字节。");

        EnsureReady();
        cancellationToken.ThrowIfCancellationRequested();
        float[] pixels = PreprocessImage(image.Span);
        var tensor = new DenseTensor<float>(pixels, [1, 3, _options.ImageSize, _options.ImageSize]);
        float[] embedding = RunEncoder(
            EnsureVisionSession(),
            _options.VisionInputName,
            _options.VisionOutputName,
            tensor);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NormalizeAndValidate(embedding));
    }

    /// <summary>
    /// 释放两个 ONNX session。
    /// </summary>
    public void Dispose()
    {
        lock (_initializationSync)
        {
            if (_disposed)
                return;

            _textSession?.Dispose();
            _visionSession?.Dispose();
            _textSession = null;
            _visionSession = null;
            _tokenizer = null;
            _disposed = true;
        }
    }

    private static string? ValidateConfiguration(SemanticSearchOptions options)
    {
        if (options.Dimensions <= 0)
            return "Dimensions 必须大于 0。";
        if (options.MaxTextTokens <= 1)
            return "MaxTextTokens 必须大于 1。";
        if (options.ImageSize <= 0)
            return "ImageSize 必须大于 0。";
        if (options.MaxImageBytes <= 0)
            return "MaxImageBytes 必须大于 0。";
        if (!File.Exists(GetFullPath(options.TextModelPath)))
            return "SigLIP2 文本 ONNX 模型文件不存在。";
        if (!File.Exists(GetFullPath(options.VisionModelPath)))
            return "SigLIP2 视觉 ONNX 模型文件不存在。";
        if (!File.Exists(GetFullPath(options.TokenizerModelPath)))
            return "SigLIP2 tokenizer.model 文件不存在。";
        if (string.IsNullOrWhiteSpace(options.TextInputName)
            || string.IsNullOrWhiteSpace(options.TextOutputName)
            || string.IsNullOrWhiteSpace(options.VisionInputName)
            || string.IsNullOrWhiteSpace(options.VisionOutputName))
        {
            return "ONNX tensor 名称不能为空。";
        }

        return null;
    }

    private void EnsureReady()
    {
        if (!Info.Ready)
            throw new InvalidOperationException(Info.Reason ?? "SigLIP2 provider 未就绪。");
    }

    private SentencePieceTokenizer EnsureTokenizer()
    {
        if (_tokenizer is not null)
            return _tokenizer;

        lock (_initializationSync)
        {
            if (_tokenizer is not null)
                return _tokenizer;

            using var modelStream = File.OpenRead(GetFullPath(_options.TokenizerModelPath));
            _tokenizer = SentencePieceTokenizer.Create(
                modelStream,
                addBeginningOfSentence: false,
                addEndOfSentence: true);
            return _tokenizer;
        }
    }

    private InferenceSession EnsureTextSession()
    {
        if (_textSession is not null)
            return _textSession;

        lock (_initializationSync)
        {
            _textSession ??= CreateSession(
                GetFullPath(_options.TextModelPath),
                _options.TextInputName,
                _options.TextOutputName);
            return _textSession;
        }
    }

    private InferenceSession EnsureVisionSession()
    {
        if (_visionSession is not null)
            return _visionSession;

        lock (_initializationSync)
        {
            _visionSession ??= CreateSession(
                GetFullPath(_options.VisionModelPath),
                _options.VisionInputName,
                _options.VisionOutputName);
            return _visionSession;
        }
    }

    private static InferenceSession CreateSession(string modelPath, string inputName, string outputName)
    {
        // 默认 session 只使用 ONNX Runtime CPU execution provider，避免把 CUDA/DirectML
        // 等平台专用 provider 变成部署前提；ORT 本身仍是随包发布的原生 AOT 兼容边界。
        using var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            // ORT CPU provider 的 MLAS 会按运行 CPU 使用 AVX/AVX2/AVX-512/NEON；
            // 全图优化会在首次加载时完成常量折叠、算子融合和内存复用规划。
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = true,
            EnableMemoryPattern = true,
        };
        var session = new InferenceSession(modelPath, sessionOptions);
        if (!session.InputMetadata.ContainsKey(inputName))
        {
            string availableInputs = FormatTensorNames(session.InputMetadata.Keys);
            session.Dispose();
            throw new InvalidDataException(
                $"ONNX 模型缺少输入 tensor '{inputName}'；可用输入：{availableInputs}。");
        }
        if (!session.OutputMetadata.ContainsKey(outputName))
        {
            string availableOutputs = FormatTensorNames(session.OutputMetadata.Keys);
            session.Dispose();
            throw new InvalidDataException(
                $"ONNX 模型缺少输出 tensor '{outputName}'；可用输出：{availableOutputs}。");
        }

        return session;
    }

    private static string FormatTensorNames(IEnumerable<string> names)
        => string.Join(", ", names.OrderBy(static name => name, StringComparer.Ordinal));

    private static float[] RunEncoder<T>(
        InferenceSession session,
        string inputName,
        string outputName,
        DenseTensor<T> tensor)
    {
        var input = NamedOnnxValue.CreateFromTensor(inputName, tensor);
        using var outputs = session.Run([input], [outputName]);
        var output = outputs.FirstOrDefault(value => string.Equals(value.Name, outputName, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"ONNX 推理结果缺少输出 tensor '{outputName}'。");
        return output.AsEnumerable<float>().ToArray();
    }

    private float[] PreprocessImage(ReadOnlySpan<byte> encodedImage)
    {
        using Image<Rgb24> image = Image.Load<Rgb24>(encodedImage);
        image.Mutate(operation => operation
            .AutoOrient()
            .Resize(new ResizeOptions
            {
                Size = new Size(_options.ImageSize, _options.ImageSize),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Triangle,
            }));

        int planeSize = checked(_options.ImageSize * _options.ImageSize);
        var pixels = new float[checked(planeSize * 3)];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    int offset = y * _options.ImageSize + x;
                    Rgb24 pixel = row[x];
                    // SigLIP2 配置 mean/std 均为 0.5，等价于把 [0,255] 映射到 [-1,1]。
                    pixels[offset] = pixel.R / 127.5f - 1f;
                    pixels[planeSize + offset] = pixel.G / 127.5f - 1f;
                    pixels[planeSize * 2 + offset] = pixel.B / 127.5f - 1f;
                }
            }
        });
        return pixels;
    }

    private float[] NormalizeAndValidate(float[] embedding)
    {
        if (embedding.Length != _options.Dimensions)
        {
            throw new InvalidDataException(
                $"SigLIP2 输出维度为 {embedding.Length}，配置要求 {_options.Dimensions}。请检查模型与 Dimensions。" );
        }

        double sumSquares = 0;
        for (int i = 0; i < embedding.Length; i++)
            sumSquares += embedding[i] * embedding[i];
        if (sumSquares <= double.Epsilon || double.IsNaN(sumSquares))
            throw new InvalidDataException("SigLIP2 返回了零向量或非法向量。");

        float reciprocalNorm = (float)(1d / Math.Sqrt(sumSquares));
        for (int i = 0; i < embedding.Length; i++)
            embedding[i] *= reciprocalNorm;
        return embedding;
    }

    private static string GetFullPath(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
}
