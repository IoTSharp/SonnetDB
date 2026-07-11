using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SonnetDB.Copilot;

/// <summary>
/// Copilot 调用链的 BCL 指标与追踪入口。
/// </summary>
internal static class CopilotDiagnostics
{
    private static readonly Meter Meter = new("SonnetDB.Copilot", "1.0.0");

    /// <summary>Copilot 请求计数。</summary>
    public static Counter<long> ChatRequests { get; } = Meter.CreateCounter<long>("copilot.chat.requests");

    /// <summary>Copilot 请求耗时。</summary>
    public static Histogram<double> ChatDuration { get; } = Meter.CreateHistogram<double>("copilot.chat.duration", "ms");

    /// <summary>Copilot token 计数，使用 direction 标签区分输入与输出。</summary>
    public static Counter<long> ChatTokens { get; } = Meter.CreateCounter<long>("copilot.chat.tokens");

    /// <summary>Copilot 本地工具调用计数。</summary>
    public static Counter<long> ToolCalls { get; } = Meter.CreateCounter<long>("copilot.tool.calls");

    /// <summary>Copilot ActivitySource。</summary>
    public static ActivitySource ActivitySource { get; } = new("SonnetDB.Copilot", "1.0.0");
}
