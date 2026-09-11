using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SonnetDB.TriggerAdmission;

internal static class Program
{
    private static int Main(string[] args)
    {
        bool quick = args.Contains("--quick", StringComparer.OrdinalIgnoreCase);
        string output = ReadOption(args, "--output") ?? Path.Combine("artifacts", "m39-339-admission");
        Directory.CreateDirectory(output);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var context = new AdmissionContext(quick, cancellation.Token);
        try
        {
            DocumentAdmission.Run(context);
            MeasurementAdmission.Run(context);
        }
        catch (Exception exception)
        {
            errors.Add(exception.ToString());
            Console.Error.WriteLine(exception);
        }

        try
        {
            context.Dispose();
        }
        catch (Exception exception)
        {
            errors.Add("cleanup: " + exception);
            Console.Error.WriteLine(exception);
        }

        var decisions = new[]
        {
            new AdmissionDecision("document", errors.Count == 0 ? "PASS" : "BLOCKED", errors.Count == 0 ? [] : [
                "the admission runner failed before completing the native event contract checks"]),
            new AdmissionDecision("measurement", errors.Count == 0 ? "PASS" : "BLOCKED", errors.Count == 0 ? [] : [
                "the admission runner failed before completing the native batch identity checks"]),
        };
        var report = new AdmissionReport(
            "m39-339-admission-v1",
            started,
            DateTimeOffset.UtcNow,
            errors.Count == 0 ? "COLLECTED" : "PARTIAL",
            quick ? "quick" : "full",
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            Environment.MachineName,
            Environment.ProcessorCount,
            Sha256(Path.Combine(AppContext.BaseDirectory, "SonnetDB.TriggerAdmission.dll")),
            Sha256(Path.Combine(AppContext.BaseDirectory, "SonnetDB.Core.dll")),
            decisions,
            context.Samples.ToArray(),
            context.Processes.ToArray(),
            errors.ToArray(),
            [
                "This is a model-native measurement report; it does not implement SQL triggers.",
                "Document retention is fixed at seven days; expiry cause is tested with an expired TTL document.",
                "Measurement batch identity is durable and replay is reconciled against the accepted point multiset.",
                "Fixed production hardware and long-duration SLO evidence remain separate gates.",
            ],
            context.TemporaryDataRemoved);
        string reportPath = Path.GetFullPath(Path.Combine(output, "m39-339-admission.json"));
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, AdmissionJsonContext.Default.AdmissionReport));
        Console.WriteLine($"m39-339-admission={report.CollectionStatus} output={reportPath} samples={report.Samples.Length}");
        return errors.Count == 0 ? 0 : 2;
    }

    private static string? ReadOption(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            return null;
        return args[index + 1];
    }

    private static string Sha256(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return "unavailable";
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
