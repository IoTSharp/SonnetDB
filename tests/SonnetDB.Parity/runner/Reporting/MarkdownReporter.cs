using System.Text;

namespace SonnetDB.Parity.Runner.Reporting;

/// <summary>
/// 把 <see cref="ParityReport"/> 写为 <c>diff.md</c>，渲染
/// <c>| Scenario | SonnetDB | ... | Diff |</c> 风格的可读对比表。
/// </summary>
public static class MarkdownReporter
{
    /// <summary>
    /// 将报告写入 <paramref name="reportDirectory"/> 下的 <c>diff.md</c>。
    /// </summary>
    /// <param name="report">待写出的报告。</param>
    /// <param name="reportDirectory">报告目录（不存在则创建）。</param>
    /// <returns>写出的文件完整路径。</returns>
    public static async Task<string> WriteAsync(ParityReport report, string reportDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);

        Directory.CreateDirectory(reportDirectory);
        var path = Path.Combine(reportDirectory, "diff.md");

        var sb = new StringBuilder();
        sb.Append("# Parity Run ").Append(report.RunId).Append('\n').Append('\n');
        sb.Append("Started: ").Append(report.StartedAtUtc.ToString("o")).Append('\n').Append('\n');
        sb.Append("| Scenario | ");
        foreach (var backend in report.Backends)
            sb.Append(EscapeCell(backend)).Append(" | ");
        sb.Append("Diff |\n");
        sb.Append("|---|");
        foreach (var _ in report.Backends)
            sb.Append("---|");
        sb.Append("---|\n");

        foreach (var scenario in report.Scenarios)
        {
            var diff = DescribeDiff(scenario);
            sb.Append("| ").Append(scenario.Name).Append(" | ");
            foreach (var backend in report.Backends)
                sb.Append(Describe(scenario, backend)).Append(" | ");
            sb.Append(diff).Append(" |\n");

            if (scenario.Differences.Count > 0)
            {
                foreach (var d in scenario.Differences)
                {
                    sb.Append("| ").Append("&nbsp;").Append(" | ");
                    sb.Append(EscapeCell(d)).Append(" | ");
                    for (var i = 1; i < report.Backends.Count; i++)
                        sb.Append(" | ");
                    sb.Append(" |\n");
                }
            }
        }

        sb.Append('\n');
        sb.Append("## Capability gaps\n\n");
        sb.Append("| Scenario | Required | ");
        foreach (var backend in report.Backends)
            sb.Append(EscapeCell(backend)).Append(" | ");
        sb.Append("SonnetDB gap |\n");
        sb.Append("|---|---|");
        foreach (var _ in report.Backends)
            sb.Append("---|");
        sb.Append("---|\n");
        foreach (var gap in report.CapabilityGaps)
        {
            sb.Append("| ").Append(EscapeCell(gap.Scenario))
              .Append(" | ").Append(EscapeCell(gap.Required))
              .Append(" | ");
            foreach (var backend in report.Backends)
            {
                gap.BackendStatuses.TryGetValue(backend, out var status);
                sb.Append(EscapeCell(status ?? "missing")).Append(" | ");
            }
            sb.Append(EscapeCell(gap.SonnetDbGap ?? "")).Append(" |\n");
        }

        await File.WriteAllTextAsync(path, sb.ToString()).ConfigureAwait(false);
        return path;
    }

    private static string Describe(ScenarioReport scenario, string backend)
    {
        var outcome = scenario.Backends.FirstOrDefault(b =>
            string.Equals(b.Backend, backend, StringComparison.OrdinalIgnoreCase));
        if (outcome is null)
            return "—";
        return outcome.Status switch
        {
            "skipped" => $"⏭ skipped ({EscapeCell(outcome.GapReason ?? "n/a")})",
            "pass" => $"✅ pass (rows={outcome.RowCount})",
            "fail" => $"❌ fail (rows={outcome.RowCount})",
            _ => EscapeCell(outcome.Status),
        };
    }

    private static string DescribeDiff(ScenarioReport scenario) => scenario.WithinTolerance switch
    {
        null => "n/a (single backend)",
        true => "✅ within tolerance",
        false => "❌ out of tolerance",
    };

    private static string EscapeCell(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
