using System.Text.Json.Serialization;

namespace SonnetDB.Backup;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(BackupConsistency))]
[JsonSerializable(typeof(BackupModelSummary))]
[JsonSerializable(typeof(BackupMeasurementEntry))]
[JsonSerializable(typeof(BackupTableEntry))]
[JsonSerializable(typeof(BackupKeyspaceEntry))]
[JsonSerializable(typeof(BackupDocumentCollectionEntry))]
[JsonSerializable(typeof(BackupVectorIndexEntry))]
[JsonSerializable(typeof(BackupSecondaryIndexEntry))]
[JsonSerializable(typeof(BackupFullTextIndexEntry))]
[JsonSerializable(typeof(BackupFileEntry))]
[JsonSerializable(typeof(BackupIndexEntry))]
[JsonSerializable(typeof(BackupVerificationResult))]
[JsonSerializable(typeof(BackupRestoreDryRunResult))]
[JsonSerializable(typeof(BackupIndexRebuildResult))]
[JsonSerializable(typeof(BackupIndexRebuildEntry))]
[JsonSerializable(typeof(IReadOnlyList<BackupFileEntry>))]
[JsonSerializable(typeof(IReadOnlyList<BackupIndexEntry>))]
[JsonSerializable(typeof(IReadOnlyList<BackupIndexRebuildEntry>))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
