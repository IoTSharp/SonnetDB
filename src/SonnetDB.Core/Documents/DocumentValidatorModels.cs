namespace SonnetDB.Documents;

/// <summary>
/// 文档集合 validator 声明。
/// </summary>
/// <param name="Rules">按声明顺序执行的字段校验规则。</param>
/// <param name="Action">校验失败时的处理动作。</param>
/// <param name="CreatedAtUtcTicks">创建时间 UTC ticks。</param>
/// <param name="UpdatedAtUtcTicks">最近更新时间 UTC ticks。</param>
public sealed record DocumentValidator(
    IReadOnlyList<DocumentValidatorRule> Rules,
    DocumentValidationAction Action,
    long CreatedAtUtcTicks,
    long UpdatedAtUtcTicks)
{
    /// <summary>是否包含至少一条有效校验规则。</summary>
    public bool HasRules => Rules.Count != 0;
}

/// <summary>
/// 创建或更新文档集合 validator 时使用的声明。
/// </summary>
/// <param name="Rules">字段校验规则。</param>
/// <param name="Action">校验失败时的处理动作。</param>
/// <param name="CreatedAtUtcTicks">创建时间 UTC ticks；为 0 时使用当前时间。</param>
/// <param name="UpdatedAtUtcTicks">更新时间 UTC ticks；为 0 时使用当前时间。</param>
public sealed record DocumentValidatorDefinition(
    IReadOnlyList<DocumentValidatorRuleDefinition> Rules,
    DocumentValidationAction Action = DocumentValidationAction.Error,
    long CreatedAtUtcTicks = 0,
    long UpdatedAtUtcTicks = 0);

/// <summary>
/// 文档字段 validator 规则。
/// </summary>
/// <param name="Path">JSON path。</param>
/// <param name="Required">字段是否必须存在且不可为 JSON null。</param>
/// <param name="Types">允许的 JSON 类型列表；为空时不限制类型。</param>
/// <param name="Minimum">数值下界；为空时不限制。</param>
/// <param name="Maximum">数值上界；为空时不限制。</param>
/// <param name="EnumValues">允许的枚举值；为空时不限制。</param>
/// <param name="Pattern">字符串正则表达式；为空时不限制。</param>
public sealed record DocumentValidatorRule(
    string Path,
    bool Required,
    IReadOnlyList<DocumentValidatorValueType> Types,
    double? Minimum,
    double? Maximum,
    IReadOnlyList<string> EnumValues,
    string? Pattern);

/// <summary>
/// 创建或更新文档字段 validator 规则时使用的声明。
/// </summary>
/// <param name="Path">JSON path。</param>
/// <param name="Required">字段是否必须存在且不可为 JSON null。</param>
/// <param name="Types">允许的 JSON 类型列表；为空时不限制类型。</param>
/// <param name="Minimum">数值下界；为空时不限制。</param>
/// <param name="Maximum">数值上界；为空时不限制。</param>
/// <param name="EnumValues">允许的枚举值；为空时不限制。</param>
/// <param name="Pattern">字符串正则表达式；为空时不限制。</param>
public sealed record DocumentValidatorRuleDefinition(
    string Path,
    bool Required = false,
    IReadOnlyList<DocumentValidatorValueType>? Types = null,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<string>? EnumValues = null,
    string? Pattern = null);

/// <summary>
/// 文档 validator 失败时的处理动作。
/// </summary>
public enum DocumentValidationAction
{
    /// <summary>拒绝写入并返回 <c>validation_failed</c>。</summary>
    Error,
    /// <summary>允许写入，并在结果中返回 warning 级 <c>validation_failed</c>。</summary>
    Warn,
}

/// <summary>
/// 文档 validator 支持的 JSON 类型。
/// </summary>
public enum DocumentValidatorValueType
{
    /// <summary>JSON string。</summary>
    String,
    /// <summary>JSON number。</summary>
    Number,
    /// <summary>JSON integer number。</summary>
    Integer,
    /// <summary>JSON boolean。</summary>
    Boolean,
    /// <summary>JSON object。</summary>
    Object,
    /// <summary>JSON array。</summary>
    Array,
    /// <summary>JSON null。</summary>
    Null,
}

/// <summary>
/// 单次文档 validator 执行结果。
/// </summary>
/// <param name="IsValid">是否通过校验。</param>
/// <param name="Failures">校验失败列表。</param>
public sealed record DocumentValidationResult(
    bool IsValid,
    IReadOnlyList<DocumentValidationFailure> Failures)
{
    /// <summary>通过校验的共享结果。</summary>
    public static DocumentValidationResult Valid { get; } = new(true, Array.Empty<DocumentValidationFailure>());
}

/// <summary>
/// 文档 validator 失败明细。
/// </summary>
/// <param name="Path">失败字段 JSON path。</param>
/// <param name="Rule">失败规则名称。</param>
/// <param name="Message">面向调用方的说明。</param>
public sealed record DocumentValidationFailure(string Path, string Rule, string Message);
