using SonnetDB.Configuration;

namespace SonnetDB.Copilot;

/// <summary>
/// 把面向用户的 <see cref="AiOptions"/>（sonnetdb.com Cloud Token）
/// 同步到底层 <see cref="CopilotChatOptions"/> / <see cref="CopilotEmbeddingOptions"/>
/// 单例，使 <see cref="CopilotReadiness"/> 与 <see cref="OpenAICompatibleChatProvider"/>
/// 在 Web 端保存配置后立即就绪，避免 <c>503 chat.endpoint_invalid</c>。
/// </summary>
internal static class AiCopilotBridge
{
    private const string OfficialGatewayBaseUrl = "https://ai.sonnetdb.com";
    private const string OfficialEndpoint = OfficialGatewayBaseUrl + "/v1/";

    /// <summary>
    /// 按 Copilot 运行模式同步持久化 Cloud 配置。
    /// 内部模式的运行地址和凭据只来自 appsettings，历史 Cloud Token 不得覆盖它们。
    /// </summary>
    /// <param name="ai">持久化的 AI 配置。</param>
    /// <param name="copilot">当前实例的 Copilot 配置。</param>
    public static void Apply(AiOptions ai, CopilotOptions copilot)
    {
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(copilot);

        // 现场内部模式必须以部署配置为唯一来源，保留旧文件仅用于兼容回退到非内部模式的实例。
        if (copilot.InternalOnly)
            return;

        Apply(ai, copilot.Chat, copilot.Embedding);
    }

    /// <summary>
    /// 把 <paramref name="ai"/> 的官方 Gateway / Cloud Token 同步进 Copilot 的 chat 选项。
    /// 若 <paramref name="ai"/> 未配置 <see cref="AiOptions.CloudAccessToken"/>，则不修改任何选项，
    /// 保留运维或测试中已显式配置的 Provider / Endpoint / ApiKey。
    /// </summary>
    public static void Apply(AiOptions ai, CopilotChatOptions chat, CopilotEmbeddingOptions embedding)
    {
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(embedding);

        // 未配置 Cloud Token 时不覆盖现有配置，避免清空运维或测试中显式设置的 Endpoint / ApiKey。
        if (string.IsNullOrWhiteSpace(ai.CloudAccessToken))
            return;

        chat.Provider = "openai";
        chat.Endpoint = OfficialEndpoint;
        chat.ApiKey = ai.CloudAccessToken;
        chat.Model = null;
        if (ai.TimeoutSeconds > 0)
            chat.TimeoutSeconds = ai.TimeoutSeconds;

        // 仅当 embedding 也是 openai 且自身字段为空时回填，避免覆盖运维显式配置的独立 embedding 服务。
        if (string.Equals(embedding.Provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(embedding.Endpoint))
                embedding.Endpoint = OfficialEndpoint;
            if (string.IsNullOrWhiteSpace(embedding.ApiKey))
                embedding.ApiKey = ai.CloudAccessToken;
        }
    }
}
