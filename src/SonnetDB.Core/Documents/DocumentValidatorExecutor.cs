using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonnetDB.Documents;

/// <summary>
/// 执行文档集合 validator 规则。
/// </summary>
public static class DocumentValidatorExecutor
{
    /// <summary>
    /// 对单个 JSON 文档执行 validator。
    /// </summary>
    /// <param name="validator">validator 声明。</param>
    /// <param name="json">规范化或原始 JSON 文本。</param>
    /// <returns>校验结果。</returns>
    public static DocumentValidationResult Validate(DocumentValidator? validator, string json)
    {
        if (validator is null || !validator.HasRules)
            return DocumentValidationResult.Valid;

        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var failures = new List<DocumentValidationFailure>();
        foreach (var rule in validator.Rules)
            ValidateRule(document.RootElement, rule, failures);

        return failures.Count == 0
            ? DocumentValidationResult.Valid
            : new DocumentValidationResult(false, failures);
    }

    internal static string FormatFailures(IReadOnlyList<DocumentValidationFailure> failures)
        => failures.Count == 0
            ? string.Empty
            : string.Join("; ", failures.Select(static f => $"{f.Path} {f.Message}"));

    /// <summary>
    /// 将 JSON 元素转换为 validator enum 比较使用的稳定文本。
    /// </summary>
    /// <param name="element">JSON 元素。</param>
    /// <returns>稳定比较文本。</returns>
    public static string ToComparableJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? string.Empty;
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt64(out long integer)
                ? integer.ToString(CultureInfo.InvariantCulture)
                : element.GetDouble().ToString("R", CultureInfo.InvariantCulture);
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return element.GetBoolean() ? "true" : "false";
        if (element.ValueKind == JsonValueKind.Null)
            return "null";

        return element.GetRawText();
    }

    private static void ValidateRule(
        JsonElement root,
        DocumentValidatorRule rule,
        List<DocumentValidationFailure> failures)
    {
        var path = JsonPath.Parse(rule.Path);
        bool exists = JsonPathEvaluator.TryResolve(root, path, out var element);
        if (!exists || element.ValueKind == JsonValueKind.Null)
        {
            if (rule.Required)
            {
                failures.Add(new DocumentValidationFailure(
                    rule.Path,
                    "required",
                    "为必填字段且不能为 null。"));
            }

            if (!exists)
                return;
        }

        if (rule.Types.Count != 0 && !MatchesAnyType(element, rule.Types))
        {
            failures.Add(new DocumentValidationFailure(
                rule.Path,
                "type",
                $"类型必须是 {string.Join("|", rule.Types.Select(static t => t.ToString().ToLowerInvariant()))}。"));
            return;
        }

        if (rule.Minimum is not null || rule.Maximum is not null)
            ValidateRange(rule, element, failures);
        if (rule.EnumValues.Count != 0)
            ValidateEnum(rule, element, failures);
        if (!string.IsNullOrEmpty(rule.Pattern))
            ValidatePattern(rule, element, failures);
    }

    private static bool MatchesAnyType(JsonElement element, IReadOnlyList<DocumentValidatorValueType> types)
    {
        foreach (var type in types)
        {
            if (MatchesType(element, type))
                return true;
        }

        return false;
    }

    private static bool MatchesType(JsonElement element, DocumentValidatorValueType type)
        => type switch
        {
            DocumentValidatorValueType.String => element.ValueKind == JsonValueKind.String,
            DocumentValidatorValueType.Number => element.ValueKind == JsonValueKind.Number,
            DocumentValidatorValueType.Integer => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
            DocumentValidatorValueType.Boolean => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
            DocumentValidatorValueType.Object => element.ValueKind == JsonValueKind.Object,
            DocumentValidatorValueType.Array => element.ValueKind == JsonValueKind.Array,
            DocumentValidatorValueType.Null => element.ValueKind == JsonValueKind.Null,
            _ => false,
        };

    private static void ValidateRange(
        DocumentValidatorRule rule,
        JsonElement element,
        List<DocumentValidationFailure> failures)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            failures.Add(new DocumentValidationFailure(
                rule.Path,
                "range",
                "range 校验要求字段为 number。"));
            return;
        }

        double value = element.GetDouble();
        if (rule.Minimum is { } minimum && value < minimum)
        {
            failures.Add(new DocumentValidationFailure(
                rule.Path,
                "minimum",
                $"必须大于等于 {minimum.ToString(CultureInfo.InvariantCulture)}。"));
        }

        if (rule.Maximum is { } maximum && value > maximum)
        {
            failures.Add(new DocumentValidationFailure(
                rule.Path,
                "maximum",
                $"必须小于等于 {maximum.ToString(CultureInfo.InvariantCulture)}。"));
        }
    }

    private static void ValidateEnum(
        DocumentValidatorRule rule,
        JsonElement element,
        List<DocumentValidationFailure> failures)
    {
        string actual = ToComparableJson(element);
        if (!rule.EnumValues.Contains(actual, StringComparer.Ordinal))
        {
            failures.Add(new DocumentValidationFailure(
                rule.Path,
                "enum",
                "值不在 enum 允许列表中。"));
        }
    }

    private static void ValidatePattern(
        DocumentValidatorRule rule,
        JsonElement element,
        List<DocumentValidationFailure> failures)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            failures.Add(new DocumentValidationFailure(
                rule.Path,
                "pattern",
                "pattern 校验要求字段为 string。"));
            return;
        }

        string value = element.GetString() ?? string.Empty;
        if (!Regex.IsMatch(value, rule.Pattern!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
        {
            failures.Add(new DocumentValidationFailure(
                rule.Path,
                "pattern",
                "字符串未匹配 pattern。"));
        }
    }

}
