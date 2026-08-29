using Microsoft.ML.OnnxRuntime;
using SonnetDB.Configuration;

namespace SonnetDB.Copilot;

/// <summary>
/// 本地 ONNX embedding provider。
///
/// 当前版本会验证并加载模型文件，但不假设某一个 tokenizer/input schema。
/// 无法安全推断模型输入时，使用内置 hash provider 作为显式本地降级路径，
/// 保证离线摄入仍可运行，同时通过 <see cref="IsFallback"/> 暴露真实边界。
/// </summary>
public sealed class LocalOnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly CopilotEmbeddingOptions _options;
    private InferenceSession? _session;
    private BuiltinHashEmbeddingProvider? _fallbackProvider;
    private string? _fallbackReason;
    private bool _disposed;

    /// <summary>
    /// 构造本地 ONNX embedding provider。
    /// </summary>
    /// <param name="options">本地 embedding 配置。</param>
    public LocalOnnxEmbeddingProvider(CopilotEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// 是否使用了内置 hash 降级实现。
    /// </summary>
    public bool IsFallback => _fallbackProvider is not null;

    /// <summary>
    /// 最近一次进入降级路径的原因；使用真实 ONNX 时为空。
    /// </summary>
    public string? FallbackReason => _fallbackReason;

    public async ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Embedding input cannot be empty.", nameof(text));

        cancellationToken.ThrowIfCancellationRequested();

        if (_fallbackProvider is not null)
            return await _fallbackProvider.EmbedAsync(text, cancellationToken).ConfigureAwait(false);

        try
        {
            _ = EnsureSession();

            // 不同本地模型的 tokenizer、输入名和 pooling 规则并不统一。
            // 在没有显式模型 profile 时不能猜测并宣称 ONNX 结果有效，
            // 因此把这一边界转为可运行的本地确定性 fallback。
            throw new NotSupportedException("Local ONNX model input profile is not configured.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _fallbackReason ??= exception.Message;
            _fallbackProvider ??= new BuiltinHashEmbeddingProvider(
                new CopilotEmbeddingOptions { Provider = "local" });
            return await _fallbackProvider.EmbedAsync(text, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _session?.Dispose();
        _session = null;
        _fallbackProvider = null;
        _disposed = true;
    }

    private InferenceSession EnsureSession()
    {
        if (_session is not null)
            return _session;

        if (string.IsNullOrWhiteSpace(_options.LocalModelPath))
            throw new InvalidOperationException("Copilot local embedding model path is missing.");

        var modelPath = Path.GetFullPath(_options.LocalModelPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Copilot local embedding model file was not found.", modelPath);

        _session = new InferenceSession(modelPath);
        return _session;
    }
}
