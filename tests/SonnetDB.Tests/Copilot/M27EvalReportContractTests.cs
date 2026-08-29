using System.Text.Json;
using Xunit;

namespace SonnetDB.Tests.Copilot;

/// <summary>
/// M27 #187：评测报告必须区分脚本 fixture、真实 provider、失败原因和 token 成本。
/// </summary>
public sealed class M27EvalReportContractTests
{
    [Fact]
    public void ExampleReport_NotReadyFixture_ContainsRequiredJourneysAndNoFakeUsage()
    {
        var path = FindFixture();
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        Assert.Equal("m27-copilot-eval-v1", root.GetProperty("schema").GetString());
        Assert.Equal("NOT_READY", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("run").GetProperty("realProvider").GetBoolean());
        Assert.Equal("NOT_READY", root.GetProperty("readiness").GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("readiness").GetProperty("reason").GetString()));

        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "anomaly_device", "slow_query", "schema", "repair_advice", "approval",
        };
        var scenarios = root.GetProperty("scenarios");
        Assert.True(scenarios.GetArrayLength() >= required.Count);
        foreach (var scenario in scenarios.EnumerateArray())
        {
            required.Remove(scenario.GetProperty("category").GetString() ?? string.Empty);
            Assert.False(string.IsNullOrWhiteSpace(scenario.GetProperty("provider").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(scenario.GetProperty("model").GetString()));
            Assert.True(scenario.GetProperty("toolCalls").GetInt32() >= 0);
            Assert.True(scenario.GetProperty("toolNames").GetArrayLength() > 0);

            var usage = scenario.GetProperty("usage");
            Assert.False(usage.GetProperty("reported").GetBoolean());
            Assert.Equal(JsonValueKind.Null, usage.GetProperty("inputTokens").ValueKind);
            Assert.Equal(JsonValueKind.Null, usage.GetProperty("outputTokens").ValueKind);
            Assert.Equal(JsonValueKind.Null, usage.GetProperty("totalTokens").ValueKind);
            Assert.Equal(JsonValueKind.Null, usage.GetProperty("costUsd").ValueKind);
        }

        Assert.Empty(required);
        var summary = root.GetProperty("summary");
        Assert.Equal(scenarios.GetArrayLength(), summary.GetProperty("scenarioCount").GetInt32());
        Assert.Equal(
            summary.GetProperty("totalInputTokens").GetInt64() + summary.GetProperty("totalOutputTokens").GetInt64(),
            summary.GetProperty("totalTokens").GetInt64());
    }

    private static string FindFixture()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && current is not null; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "docs", "benchmarks", "m27-copilot-eval-report.example.json");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("未找到 M27 eval report fixture。", "m27-copilot-eval-report.example.json");
    }
}
