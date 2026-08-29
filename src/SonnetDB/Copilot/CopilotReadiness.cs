using Microsoft.Extensions.Options;
using SonnetDB.Configuration;

namespace SonnetDB.Copilot;

/// <summary>
/// Copilot 基础就绪状态。
/// </summary>
public sealed record CopilotReadinessResult(
    bool Enabled,
    bool EmbeddingReady,
    bool ChatReady,
    bool Ready,
    string? Reason);

/// <summary>
/// 统一封装 Copilot readiness 计算逻辑。
/// </summary>
public sealed class CopilotReadiness
{
    private const int MaximumLocalTokenCount = 32_768;
    private const int MaximumLocalEmbeddingDimension = 65_536;
    private readonly ServerOptions _serverOptions;

    /// <summary>
    /// 创建 Copilot 就绪状态计算器。
    /// </summary>
    /// <param name="serverOptions">当前服务端配置。</param>
    public CopilotReadiness(IOptions<ServerOptions> serverOptions)
    {
        ArgumentNullException.ThrowIfNull(serverOptions);
        _serverOptions = serverOptions.Value;
    }

    /// <summary>
    /// 计算当前 Copilot 的 embedding、Chat 和总就绪状态。
    /// </summary>
    /// <returns>包含稳定原因码的就绪结果。</returns>
    public CopilotReadinessResult Evaluate()
    {
        var copilot = _serverOptions.Copilot;
        if (!copilot.Enabled)
        {
            return new CopilotReadinessResult(
                Enabled: false,
                EmbeddingReady: false,
                ChatReady: false,
                Ready: false,
                Reason: "disabled");
        }

        var embeddingReady = EvaluateEmbedding(copilot.Embedding, out var embeddingReason);
        var chatReady = EvaluateChat(copilot.Chat, out var chatReason);
        var ready = embeddingReady && chatReady;
        string? reason = null;

        if (!embeddingReady)
            reason = embeddingReason;
        else if (!chatReady)
            reason = chatReason;

        return new CopilotReadinessResult(
            Enabled: true,
            EmbeddingReady: embeddingReady,
            ChatReady: chatReady,
            Ready: ready,
            Reason: reason);
    }

    private static bool EvaluateEmbedding(CopilotEmbeddingOptions options, out string? reason)
    {
        if (string.Equals(options.Provider, "builtin", StringComparison.OrdinalIgnoreCase))
        {
            reason = null;
            return true;
        }

        if (string.Equals(options.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.LocalModelPath))
            {
                reason = "embedding.local_model_path_missing";
                return false;
            }

            if (!TryGetFullPath(options.LocalModelPath, out var modelPath))
            {
                reason = "embedding.local_model_path_invalid";
                return false;
            }

            if (!File.Exists(modelPath))
            {
                reason = "embedding.local_model_not_found";
                return false;
            }

            var profile = options.ModelProfile;
            if (profile is null)
            {
                reason = "embedding.local_model_profile_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(profile.TokenizerModelPath))
            {
                reason = "embedding.local_tokenizer_path_missing";
                return false;
            }

            if (!TryGetFullPath(profile.TokenizerModelPath, out var tokenizerPath))
            {
                reason = "embedding.local_tokenizer_path_invalid";
                return false;
            }

            if (!File.Exists(tokenizerPath))
            {
                reason = "embedding.local_tokenizer_not_found";
                return false;
            }

            if (profile.MaxTokens <= 0 || profile.MaxTokens > MaximumLocalTokenCount
                || profile.Dimensions <= 0 || profile.Dimensions > MaximumLocalEmbeddingDimension
                || profile.PadTokenId is < 0
                || profile.MaxTokens < profile.GetMinimumContentTokenCount())
            {
                reason = "embedding.local_model_profile_invalid";
                return false;
            }

            var tokenizerType = profile.TokenizerType?.Trim().ToLowerInvariant()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty);
            if (tokenizerType is not ("bertwordpiece" or "bert" or "wordpiece" or "sentencepiece" or "sentencepiecebpe"))
            {
                reason = "embedding.local_model_profile_invalid";
                return false;
            }

            var pooling = profile.Pooling?.Trim().ToLowerInvariant();
            if (pooling is not ("mean" or "cls" or "first" or "firsttoken" or "auto" or null or ""))
            {
                reason = "embedding.local_model_profile_invalid";
                return false;
            }

            var paddingSide = profile.PaddingSide?.Trim().ToLowerInvariant();
            if (paddingSide is not ("right" or "left" or null or ""))
            {
                reason = "embedding.local_model_profile_invalid";
                return false;
            }

            if (profile.IgnoredInputNames is null
                || profile.IgnoredInputNames.Any(string.IsNullOrWhiteSpace))
            {
                reason = "embedding.local_model_profile_invalid";
                return false;
            }

            reason = null;
            return true;
        }

        if (string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryValidateAbsoluteUri(options.Endpoint, out _))
            {
                reason = "embedding.endpoint_invalid";
                return false;
            }

            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                reason = "embedding.api_key_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(options.Model))
            {
                reason = "embedding.model_missing";
                return false;
            }

            reason = null;
            return true;
        }

        reason = "embedding.provider_unsupported";
        return false;
    }

    private static bool EvaluateChat(CopilotChatOptions options, out string? reason)
    {
        if (!string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            reason = "chat.provider_unsupported";
            return false;
        }

        if (!TryValidateAbsoluteUri(options.Endpoint, out _))
        {
            reason = "chat.endpoint_invalid";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            reason = "chat.api_key_missing";
            return false;
        }

        reason = null;
        return true;
    }

    internal static bool TryValidateAbsoluteUri(string? raw, out Uri? uri)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            uri = null;
            return false;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;
    }

    private static bool TryGetFullPath(string? raw, out string path)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            path = string.Empty;
            return false;
        }

        try
        {
            path = Path.GetFullPath(raw);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            path = string.Empty;
            return false;
        }
    }
}
