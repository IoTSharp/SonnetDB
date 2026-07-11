using System.Text.Json.Serialization;

namespace SonnetDB.LanguageServer;

/// <summary>
/// 表示语言服务支持的诊断级别。
/// </summary>
public static class DiagnosticSeverity
{
    /// <summary>表示会阻止 SQL 解析的错误。</summary>
    public const string Error = "error";
}

/// <summary>
/// 表示 SQL 文本中的一个语言服务诊断。
/// </summary>
/// <param name="Severity">诊断级别。</param>
/// <param name="Message">面向用户的诊断消息。</param>
/// <param name="Offset">诊断在 SQL 文本中的零基字符偏移。</param>
/// <param name="Length">诊断覆盖的字符数。</param>
public sealed record LanguageDiagnostic(string Severity, string Message, int Offset, int Length);

internal sealed record LanguageServerRequest(int Id, string Method, string? Text);

internal sealed record LanguageServerResponse(
    int Id,
    IReadOnlyList<LanguageDiagnostic> Diagnostics,
    string? Error = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LanguageServerRequest))]
[JsonSerializable(typeof(LanguageServerResponse))]
internal sealed partial class LanguageServerJsonContext : JsonSerializerContext;
