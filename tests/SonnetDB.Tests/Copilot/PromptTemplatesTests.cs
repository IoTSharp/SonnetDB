using System.Diagnostics;
using SonnetDB.Copilot;
using SonnetDB.Query.Functions;
using Xunit;

namespace SonnetDB.Tests.Copilot;

public sealed class PromptTemplatesTests
{
    [Fact]
    public void Load_SqlGen_ReturnsEmbeddedTemplate()
    {
        var text = PromptTemplates.Load("sql-gen");
        Assert.Contains("SonnetDB", text);
        Assert.Contains("CREATE MEASUREMENT", text);
        Assert.Contains("{{db}}", text);
        Assert.Contains("{{measurements}}", text);
    }

    [Fact]
    public void Load_SqlGenNoDb_ReturnsEmbeddedTemplate()
    {
        var text = PromptTemplates.Load("sql-gen-no-db");
        Assert.Contains("SonnetDB", text);
        Assert.Contains("knn(", text);
        Assert.DoesNotContain("{{db}}", text);
    }

    /// <summary>关系统计必须同时出现在注册表、实际加载的提示词和维护的 SQL 文档中。</summary>
    [Fact]
    public void RelationalStatistics_AgreeAcrossRegistryPromptsAndDocumentation()
    {
        var root = FindSqlDocumentationRoot();
        string reference = File.ReadAllText(Path.Combine(root, "docs", "sql-reference.md"));
        string cookbook = File.ReadAllText(Path.Combine(root, "docs", "sql-cookbook.md"));
        string architecture = File.ReadAllText(Path.Combine(root, "docs", "architecture.md"));
        string llms = File.ReadAllText(Path.Combine(root, "llms.txt"));
        string llmsFull = File.ReadAllText(Path.Combine(root, "llms-full.txt"));
        string[] aggregates =
        [
            "median", "stddev", "variance", "percentile", "p50", "p90", "p95", "p99",
            "spread", "mode", "histogram", "distinct_count", "tdigest_agg",
        ];

        int relationalStart = reference.IndexOf("### 关系表聚合", StringComparison.Ordinal);
        int measurementStart = reference.IndexOf("### Measurement 聚合", StringComparison.Ordinal);
        Assert.InRange(relationalStart, 0, measurementStart - 1);
        string relational = reference[relationalStart..measurementStart];
        string measurement = reference[measurementStart..];
        string[] prompts = [PromptTemplates.Load("sql-gen"), PromptTemplates.Load("sql-gen-no-db")];
        Assert.All(aggregates, name =>
        {
            Assert.True(FunctionRegistry.TryGetAggregate(name, out _), name);
            Assert.Contains("`" + name + "(", relational, StringComparison.Ordinal);
            Assert.Contains("`" + name, measurement, StringComparison.Ordinal);
            Assert.Contains(name + "(latency_ms", cookbook, StringComparison.Ordinal);
            Assert.Contains("`" + name + "`", architecture, StringComparison.Ordinal);
            Assert.Contains("`" + name + "`", llms, StringComparison.Ordinal);
            Assert.Contains("`" + name + "`", llmsFull, StringComparison.Ordinal);
            Assert.All(prompts, prompt =>
            {
                string[] models = prompt.Split("关系表 SQL：", StringSplitOptions.None);
                Assert.Equal(2, models.Length);
                Assert.Contains("`" + name, models[0], StringComparison.Ordinal);
                Assert.Contains("`" + name, models[1], StringComparison.Ordinal);
            });
        });
        Assert.True(FunctionRegistry.TryGetScalar("date_trunc", out _));
        Assert.False(FunctionRegistry.TryGetScalar("data_trunc", out _));
        Assert.All(prompts, prompt => Assert.Contains("DATE_TRUNC('day', created_at)", prompt, StringComparison.Ordinal));
    }

    private static string FindSqlDocumentationRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        long started = Stopwatch.GetTimestamp();
        for (int depth = 0; depth < 10 && current is not null
            && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2); depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "docs", "sql-reference.md")))
                return current.FullName;
        }

        throw new FileNotFoundException("未找到 SQL 参考文档。", "docs/sql-reference.md");
    }

    [Fact]
    public void Render_SubstitutesPlaceholders()
    {
        var result = PromptTemplates.Render("sql-gen", new Dictionary<string, string>
        {
            ["db"] = "metrics",
            ["measurements"] = "- cpu (time, host TAG, usage FIELD FLOAT)",
        });

        Assert.Contains("\"metrics\"", result);
        Assert.Contains("- cpu (time, host TAG, usage FIELD FLOAT)", result);
        Assert.DoesNotContain("{{db}}", result);
        Assert.DoesNotContain("{{measurements}}", result);
    }

    [Fact]
    public void Load_Unknown_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PromptTemplates.Load("does-not-exist"));
        Assert.Contains("does-not-exist", ex.Message);
    }
}
