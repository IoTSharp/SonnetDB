using System.Text;
using System.Text.Json;
using SonnetDB.LanguageServer;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

while (await Console.In.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
        continue;

    LanguageServerResponse response;
    try
    {
        var request = JsonSerializer.Deserialize(line, LanguageServerJsonContext.Default.LanguageServerRequest);
        response = request is null
            ? new LanguageServerResponse(0, [], "请求不能为空。")
            : HandleRequest(request);
    }
    catch (JsonException exception)
    {
        response = new LanguageServerResponse(0, [], $"无效的 JSON 请求：{exception.Message}");
    }

    string json = JsonSerializer.Serialize(response, LanguageServerJsonContext.Default.LanguageServerResponse);
    await Console.Out.WriteLineAsync(json);
    await Console.Out.FlushAsync();
}

static LanguageServerResponse HandleRequest(LanguageServerRequest request)
{
    return request.Method switch
    {
        "ping" => new LanguageServerResponse(request.Id, []),
        "validate" when request.Text is not null => new LanguageServerResponse(
            request.Id,
            SqlValidationService.Validate(request.Text)),
        "validate" => new LanguageServerResponse(request.Id, [], "validate 请求缺少 text。"),
        _ => new LanguageServerResponse(request.Id, [], $"不支持的方法：{request.Method}"),
    };
}
