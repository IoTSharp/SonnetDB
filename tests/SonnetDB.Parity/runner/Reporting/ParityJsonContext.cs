using System.Text.Json.Serialization;

namespace SonnetDB.Parity.Runner.Reporting;

/// <summary>
/// Parity 报告的源生成 JSON 序列化上下文（镜像 <c>src/SonnetDB.Cli/CliJsonContext.cs</c> 风格）。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ParityReport))]
[JsonSerializable(typeof(ScenarioReport))]
[JsonSerializable(typeof(BackendOutcome))]
internal sealed partial class ParityJsonContext : JsonSerializerContext;

/// <summary>
/// 一次 Parity run 的完整报告。
/// </summary>
/// <param name="RunId">本次 run 标识。</param>
/// <param name="StartedAtUtc">起始时间（UTC）。</param>
/// <param name="Scenarios">各场景的报告。</param>
public sealed record ParityReport(
    string RunId,
    DateTimeOffset StartedAtUtc,
    IReadOnlyList<ScenarioReport> Scenarios);

/// <summary>
/// 单个场景跨后端的报告。
/// </summary>
/// <param name="Name">场景名。</param>
/// <param name="WithinTolerance">SonnetDB 与竞品结果是否在容差内一致（无竞品参与时为 null）。</param>
/// <param name="Differences">容差判定产生的可读差异列表。</param>
/// <param name="Backends">参与该场景的各后端执行结果。</param>
public sealed record ScenarioReport(
    string Name,
    bool? WithinTolerance,
    IReadOnlyList<string> Differences,
    IReadOnlyList<BackendOutcome> Backends);

/// <summary>
/// 单个后端在某场景上的执行结果。
/// </summary>
/// <param name="Backend">后端名（如 <c>sonnetdb</c> / <c>postgres</c>）。</param>
/// <param name="Status">结果状态：<c>pass</c> / <c>fail</c> / <c>skipped</c>。</param>
/// <param name="GapReason">被 SKIP 时的原因，否则为 null。</param>
/// <param name="RowCount">该后端返回的行数。</param>
public sealed record BackendOutcome(
    string Backend,
    string Status,
    string? GapReason,
    int RowCount);
