using System.Text.Json.Serialization;
using SonnetDB.Contracts;

namespace SonnetDB.Copilot;

internal sealed record OpenAiEmbeddingRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input);

internal sealed record OpenAiEmbeddingResponse(
    [property: JsonPropertyName("data")] List<OpenAiEmbeddingItem> Data);

internal sealed record OpenAiEmbeddingItem(
    [property: JsonPropertyName("embedding")] float[] Embedding);

internal sealed record OpenAiChatCompletionRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<AiMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream);

internal sealed record OpenAiChatCompletionResponse(
    [property: JsonPropertyName("choices")] List<OpenAiChatCompletionChoice> Choices);

internal sealed record OpenAiChatCompletionChoice(
    [property: JsonPropertyName("message")] OpenAiChatCompletionMessage Message);

internal sealed record OpenAiChatCompletionMessage(
    [property: JsonPropertyName("content")] string? Content);

internal sealed record OpenAiModelsResponse(
    [property: JsonPropertyName("data")] List<OpenAiModelItem>? Data);

internal sealed record OpenAiModelItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string? DisplayName = null,
    [property: JsonPropertyName("group")] string? Group = null,
    [property: JsonPropertyName("is_default")] bool? IsDefault = null);
