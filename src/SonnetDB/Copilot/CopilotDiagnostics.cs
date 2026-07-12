using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SonnetDB.Copilot;

/// <summary>
/// Copilot 调用链的 BCL 指标与追踪入口。
/// </summary>
internal static class CopilotDiagnostics
{
    public const string MeterName = "SonnetDB.Copilot";
    public const string ActivitySourceName = "SonnetDB.Copilot";
    public const string PlanToolsActivityName = "copilot.agent.plan_tools";
    public const string RunToolActivityName = "copilot.agent.run_tool";
    public const string GenerateAnswerActivityName = "copilot.agent.generate_answer";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Copilot 请求计数。</summary>
    public static Counter<long> ChatRequests { get; } = Meter.CreateCounter<long>("copilot.chat.requests");

    /// <summary>Copilot 请求耗时。</summary>
    public static Histogram<double> ChatDuration { get; } = Meter.CreateHistogram<double>("copilot.chat.duration", "ms");

    /// <summary>Copilot token 计数，使用 direction 标签区分输入与输出。</summary>
    public static Counter<long> ChatTokens { get; } = Meter.CreateCounter<long>("copilot.chat.tokens");

    /// <summary>Copilot 本地工具调用计数。</summary>
    public static Counter<long> ToolCalls { get; } = Meter.CreateCounter<long>("copilot.tool.calls");

    /// <summary>知识召回命中次数。</summary>
    public static Counter<long> KnowledgeRecallHits { get; } = Meter.CreateCounter<long>("copilot.knowledge.recall.hits");

    /// <summary>知识召回未命中次数。</summary>
    public static Counter<long> KnowledgeRecallMisses { get; } = Meter.CreateCounter<long>("copilot.knowledge.recall.misses");

    /// <summary>Copilot ActivitySource。</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName, "1.0.0");

    /// <summary>
    /// 记录一次文档知识检索结果；每次检索只增加一个 hit 或 miss。
    /// </summary>
    /// <param name="hit">是否至少召回一条可用文档。</param>
    public static void RecordKnowledgeRecall(bool hit)
    {
        if (hit)
            KnowledgeRecallHits.Add(1);
        else
            KnowledgeRecallMisses.Add(1);
    }

    /// <summary>
    /// 把异常结果写入 Agent 子 span，同时避免记录可能包含用户数据的异常消息。
    /// </summary>
    /// <param name="activity">当前 Agent 子 span。</param>
    /// <param name="exception">导致阶段失败或回退的异常。</param>
    public static void RecordFailure(Activity? activity, Exception exception)
    {
        if (activity is null)
            return;

        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag("error.type", exception.GetType().FullName);
    }
}
