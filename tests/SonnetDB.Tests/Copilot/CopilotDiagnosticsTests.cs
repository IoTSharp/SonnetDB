using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.Tests.Copilot;

/// <summary>
/// M17 #92：Copilot 知识召回指标与 Agent 子 span 测试。
/// </summary>
public sealed class CopilotDiagnosticsTests
{
    [Fact]
    public async Task AgentRun_WithRecallMissThenHit_RecordsMetricsAndChildSpans()
    {
        var dataRoot = CreateTempDirectory("sndb-copilot-diagnostics-data-");
        var docsRoot = CreateTempDirectory("sndb-copilot-diagnostics-docs-");
        var chatProvider = new QueueChatProvider(
        [
            PlanWithListDatabases,
            EmptyPlan,
            "第一次回答",
            PlanWithListDatabases,
            EmptyPlan,
            "第二次回答",
        ]);
        var options = new ServerOptions
        {
            DataRoot = dataRoot,
            AutoLoadExistingDatabases = false,
            AllowAnonymousProbes = true,
        };
        options.Copilot.Enabled = true;
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.AutoIngestOnStartup = false;

        var measurements = new List<(string Name, long Value)>();
        var activities = new List<Activity>();
        using var meterListener = CreateMeterListener(measurements);
        using var activityListener = CreateActivityListener(activities);
        using var rootActivity = new Activity("copilot-diagnostics-test").Start();
        var traceId = rootActivity.TraceId;

        var app = TestServerHost.Build(
            options,
            services => services.AddSingleton<IChatProvider>(chatProvider));
        try
        {
            var registry = app.Services.GetRequiredService<TsdbRegistry>();
            Assert.True(registry.TryCreate("alpha", out var database));
            var agent = app.Services.GetRequiredService<CopilotAgent>();
            var context = new CopilotAgentContext("alpha", database, ["alpha"], CanWrite: false);

            await DrainAsync(agent.RunAsync(
                context,
                [new AiMessage("user", "列出当前数据库")],
                docsK: 5,
                skillsK: 0));

            File.WriteAllText(
                Path.Combine(docsRoot, "observability.md"),
                "# 可观测性\n\n知识召回指标用于区分命中与未命中。");
            await app.Services.GetRequiredService<DocsIngestor>()
                .IngestAsync([docsRoot], force: true, dryRun: false);

            await DrainAsync(agent.RunAsync(
                context,
                [new AiMessage("user", "如何查看知识召回指标")],
                docsK: 5,
                skillsK: 0));
        }
        finally
        {
            rootActivity.Stop();
            await app.DisposeAsync();
            DeleteDirectory(docsRoot);
            DeleteDirectory(dataRoot);
        }

        lock (measurements)
        {
            Assert.Contains(measurements, static item =>
                item is ("copilot.knowledge.recall.hits", 1));
            Assert.Contains(measurements, static item =>
                item is ("copilot.knowledge.recall.misses", 1));
        }

        Activity[] agentActivities;
        lock (activities)
        {
            agentActivities = activities.Where(activity => activity.TraceId == traceId).ToArray();
        }

        Assert.Contains(agentActivities, static activity =>
            activity.OperationName == CopilotDiagnostics.PlanToolsActivityName);
        Assert.Contains(agentActivities, static activity =>
            activity.OperationName == CopilotDiagnostics.GenerateAnswerActivityName);

        var toolActivities = agentActivities
            .Where(static activity => activity.OperationName == CopilotDiagnostics.RunToolActivityName)
            .ToArray();
        Assert.Equal(2, toolActivities.Length);
        Assert.All(toolActivities, static activity =>
        {
            Assert.Equal("list_databases", activity.GetTagItem("tool.name"));
            Assert.Equal(2, activity.GetTagItem("tool.arguments.length"));
            Assert.Equal(1, activity.GetTagItem("tool.result.rows"));
        });
    }

    private static MeterListener CreateMeterListener(List<(string Name, long Value)> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == CopilotDiagnostics.MeterName
                    && instrument.Name is "copilot.knowledge.recall.hits" or "copilot.knowledge.recall.misses")
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            lock (measurements)
                measurements.Add((instrument.Name, measurement));
        });
        listener.Start();
        return listener;
    }

    private static ActivityListener CreateActivityListener(List<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == CopilotDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                    activities.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static async Task DrainAsync(IAsyncEnumerable<CopilotChatEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed class QueueChatProvider(IEnumerable<string> responses) : IChatProvider
    {
        private readonly Queue<string> _responses = new(responses);

        public ValueTask<string> CompleteAsync(
            IReadOnlyList<AiMessage> messages,
            string? modelOverride = null,
            CancellationToken cancellationToken = default)
        {
            Assert.NotEmpty(messages);
            return ValueTask.FromResult(_responses.Dequeue());
        }
    }

    private const string PlanWithListDatabases = "{\"tools\":[{\"name\":\"list_databases\"}]}";
    private const string EmptyPlan = "{\"tools\":[]}";
}
