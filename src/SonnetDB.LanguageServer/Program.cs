using System.Text.Json;
using SonnetDB.LanguageServer;

using Stream input = Console.OpenStandardInput();
await using Stream output = Console.OpenStandardOutput();

while (LanguageServerFraming.ReadMessage(input) is { } payload)
{
    JsonRpcResponse response;
    try
    {
        var request = JsonSerializer.Deserialize(payload, LanguageServerJsonContext.Default.JsonRpcRequest);
        response = request is null
            ? ErrorResponse(0, -32600, "请求不能为空。")
            : HandleRequest(request);
    }
    catch (JsonException exception)
    {
        response = ErrorResponse(0, -32700, $"无效的 JSON 请求：{exception.Message}");
    }

    byte[] responsePayload = JsonSerializer.SerializeToUtf8Bytes(
        response,
        LanguageServerJsonContext.Default.JsonRpcResponse);
    await LanguageServerFraming.WriteMessageAsync(output, responsePayload);
}

static JsonRpcResponse HandleRequest(JsonRpcRequest request)
{
    int id = request.Id ?? 0;
    if (!string.Equals(request.Jsonrpc, "2.0", StringComparison.Ordinal))
        return ErrorResponse(id, -32600, "jsonrpc 必须为 2.0。");

    return request.Method switch
    {
        "initialize" => ResultResponse(id, new InitializeResult(
            new ServerCapabilities("SonnetDB SQL parser diagnostics over JSON-RPC/LSP framing.")),
            LanguageServerJsonContext.Default.InitializeResult),
        "ping" => ResultResponse(id, new LanguageValidationResult([]), LanguageServerJsonContext.Default.LanguageValidationResult),
        "sonnetdb/validate" or "validate" => Validate(id, request.Params),
        _ => ErrorResponse(id, -32601, $"不支持的方法：{request.Method}"),
    };
}

static JsonRpcResponse Validate(int id, JsonElement? parameters)
{
    if (parameters is not { ValueKind: JsonValueKind.Object } value)
        return ErrorResponse(id, -32602, "sonnetdb/validate 请求缺少 params。");

    var request = value.Deserialize(LanguageServerJsonContext.Default.LanguageValidationParams);
    if (request?.Text is null)
        return ErrorResponse(id, -32602, "sonnetdb/validate 请求缺少 text。");

    return ResultResponse(
        id,
        new LanguageValidationResult(SqlValidationService.Validate(request.Text)),
        LanguageServerJsonContext.Default.LanguageValidationResult);
}

static JsonRpcResponse ResultResponse<T>(int id, T result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
{
    return new JsonRpcResponse("2.0", id, JsonSerializer.SerializeToElement(result, typeInfo));
}

static JsonRpcResponse ErrorResponse(int id, int code, string message)
{
    return new JsonRpcResponse("2.0", id, null, new JsonRpcError(code, message));
}
