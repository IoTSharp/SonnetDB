using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SonnetDB.Configuration;
using SonnetDB.Json;

namespace SonnetDB.Copilot;

/// <summary>
/// 基于 OpenAI-compatible 协议的 embedding provider。
/// </summary>
public sealed class OpenAICompatibleEmbeddingProvider : IEmbeddingProvider
{
    private readonly CopilotEmbeddingOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public OpenAICompatibleEmbeddingProvider(CopilotEmbeddingOptions options, IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
    }

    public async ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        => await EmbedCoreAsync(text, governed: false, cancellationToken).ConfigureAwait(false);

    internal ValueTask<float[]> EmbedGovernedAsync(string text, CancellationToken cancellationToken)
        => EmbedCoreAsync(text, governed: true, cancellationToken);

    private async ValueTask<float[]> EmbedCoreAsync(string text, bool governed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Embedding input cannot be empty.", nameof(text));

        if (!CopilotReadiness.TryValidateAbsoluteUri(_options.Endpoint, out var endpoint) || endpoint is null)
            throw new InvalidOperationException("Copilot embedding endpoint is not configured correctly.");

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Copilot embedding API key is missing.");

        if (string.IsNullOrWhiteSpace(_options.Model))
            throw new InvalidOperationException("Copilot embedding model is missing.");

        using var client = _httpClientFactory.CreateClient(governed ? "copilot-rag-embedding" : string.Empty);
        client.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 60);
        if (governed) client.MaxResponseContentBufferSize = 1024 * 1024;

        var request = new OpenAiEmbeddingRequest(_options.Model, text);
        var json = JsonSerializer.Serialize(request, ServerJsonContext.Default.OpenAiEmbeddingRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "embeddings"))
        {
            Content = content,
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (governed)
            {
                string messageText = $"Copilot RAG embedding provider returned HTTP {(int)response.StatusCode}.";
                if ((int)response.StatusCode is 408 or 429 or >= 500)
                    throw new HttpRequestException(messageText, null, response.StatusCode);
                throw new InvalidOperationException(messageText);
            }
            throw new InvalidOperationException($"Copilot embedding provider returned {(int)response.StatusCode}: {payload}");
        }

        OpenAiEmbeddingResponse? parsed;
        try { parsed = JsonSerializer.Deserialize(payload, ServerJsonContext.Default.OpenAiEmbeddingResponse); }
        catch (JsonException) when (governed) { throw new InvalidOperationException("Copilot RAG embedding response is invalid JSON."); }
        if (governed && (parsed?.Data is not { Count: 1 } || parsed.Data[0] is null || parsed.Data[0].Index != 0
            || parsed.Model is not null && parsed.Model != _options.Model))
            throw new InvalidOperationException("Copilot RAG embedding response does not match the configured model or single-item index.");
        var embedding = parsed?.Data?.Count > 0 ? parsed.Data[0]?.Embedding : null;
        if (embedding is null || embedding.Length == 0)
            throw new InvalidOperationException("Copilot embedding provider returned an empty embedding vector.");

        return embedding;
    }
}
