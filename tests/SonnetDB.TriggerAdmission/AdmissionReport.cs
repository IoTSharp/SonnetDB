using System.Text.Json.Serialization;

namespace SonnetDB.TriggerAdmission;

internal sealed record AdmissionSample(
    string Model,
    string Scenario,
    int Items,
    int Cardinality,
    double ElapsedMilliseconds,
    long AllocatedBytes,
    long WalBytesDelta,
    long DataBytesDelta,
    Dictionary<string, double> Metrics,
    string Detail);

internal sealed record AdmissionDecision(string Model, string Status, string[] Blockers);

internal sealed record AdmissionProcess(
    int Pid, DateTime StartedUtc, int ParentPid, string Executable, string[] Arguments,
    bool Exited, int ExitCode);

internal sealed record AdmissionReport(
    string Schema,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string CollectionStatus,
    string Mode,
    string Runtime,
    string OperatingSystem,
    string Machine,
    int ProcessorCount,
    string RunnerSha256,
    string CoreSha256,
    AdmissionDecision[] Decisions,
    AdmissionSample[] Samples,
    AdmissionProcess[] Processes,
    string[] Errors,
    string[] Limitations,
    bool TemporaryDataRemoved);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AdmissionReport))]
internal partial class AdmissionJsonContext : JsonSerializerContext;
