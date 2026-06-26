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
    private readonly ServerOptions _serverOptions;

    public CopilotReadiness(IOptions<ServerOptions> serverOptions)
    {
        ArgumentNullException.ThrowIfNull(serverOptions);
        _serverOptions = serverOptions.Value;
    }

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

            var modelPath = Path.GetFullPath(options.LocalModelPath);
            if (!File.Exists(modelPath))
            {
                reason = "embedding.local_model_not_found";
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
}
