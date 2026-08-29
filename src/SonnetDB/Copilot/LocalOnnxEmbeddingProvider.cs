using SonnetDB.Configuration;

namespace SonnetDB.Copilot;

/// <summary>
/// 本地 ONNX embedding provider。
///
/// 当前版本会验证模型文件路径，但不假设某一个 tokenizer/input schema。
/// 无法安全推断模型输入时，使用内置 hash provider 作为显式本地降级路径，
/// 保证离线摄入仍可运行，同时通过 <see cref="IsFallback"/> 暴露真实边界。
/// </summary>
public sealed class LocalOnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly CopilotEmbeddingOptions _options;
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
        EnsureFallback(GetFallbackReason());
    }

    /// <summary>
    /// 是否使用了内置 hash 降级实现。
    /// </summary>
    public bool IsFallback => _fallbackProvider is not null;

    /// <summary>
    /// 最近一次进入降级路径的原因；使用真实 ONNX 时为空。
    /// </summary>
    public string? FallbackReason => _fallbackReason;

    /// <summary>
    /// 为文本生成 embedding；当前没有 model profile 时使用可观测的本地 hash fallback。
    /// </summary>
    /// <param name="text">待编码的非空文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>embedding 向量。</returns>
    public async ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Embedding input cannot be empty.", nameof(text));

        cancellationToken.ThrowIfCancellationRequested();

        // 当前配置没有显式 model profile，不能猜测输入并加载 native runtime；
        // provider 在构造时已建立可观测的本地确定性 fallback，避免无效模型触发
        // ONNX native 初始化崩溃，也不把 fallback 宣称为真实语义推理。
        return await _fallbackProvider!.EmbedAsync(text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 释放 provider 资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _fallbackProvider = null;
        _disposed = true;
    }

    private void EnsureFallback(string reason)
    {
        _fallbackReason ??= reason;
        _fallbackProvider ??= new BuiltinHashEmbeddingProvider(
            new CopilotEmbeddingOptions { Provider = "local" });
    }

    private string GetFallbackReason()
    {
        if (string.IsNullOrWhiteSpace(_options.LocalModelPath))
            return "Copilot local embedding model path is missing.";

        try
        {
            return File.Exists(Path.GetFullPath(_options.LocalModelPath))
                ? "Local ONNX model input profile is not configured."
                : "Copilot local embedding model file was not found.";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return $"Copilot local embedding model path is invalid: {exception.Message}";
        }
    }
}
