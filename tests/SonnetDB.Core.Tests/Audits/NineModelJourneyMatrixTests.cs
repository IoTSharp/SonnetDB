using System.Reflection;
using System.Text.Json;
using Xunit;

namespace SonnetDB.Core.Tests.Audits;

/// <summary>
/// M36 #310/#311 九模型本地旅程清单的结构和引用约束。
/// </summary>
public sealed class NineModelJourneyMatrixTests
{
    private static readonly string[] ExpectedModelIds =
    [
        "timeseries", "relational", "kv", "document", "fulltext", "vector", "object", "mq", "graph"
    ];

    private static readonly string[] ExpectedSurfaceNames =
    [
        "embeddedApi", "serverApi", "sdk", "workbench", "cli"
    ];

    private static readonly string[] ExpectedJourneyStates =
    [
        "partial", "not_executed"
    ];

    private static readonly string[] ExpectedSurfaceStates =
    [
        "implemented", "partial"
    ];

    private static readonly string[] ExpectedEvidenceNames =
    [
        "source_audit", "local_core", "local_server", "local_aot_process", "browser_real_server"
    ];

    /// <summary>
    /// 清单覆盖九种模型，且每项只引用 gap catalog 中已登记的缺口和仓库内的入口或前置测试。
    /// </summary>
    [Fact]
    public void JourneyMatrix_WithNineModels_UsesKnownGapsAndExistingPrerequisites()
    {
        string repositoryRoot = FindRepositoryRoot();
        using JsonDocument matrix = ParseJson(repositoryRoot, "docs/audits/nine-model-journey-matrix-20260919.json");
        using JsonDocument catalog = ParseJson(repositoryRoot, "docs/audits/nine-model-gap-catalog-20260905.json");

        JsonElement root = matrix.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        AssertNonEmpty(root.GetProperty("claimBoundary").GetString());
        AssertNamedNonEmptyContract(root.GetProperty("journeyStateContract"), ExpectedJourneyStates);
        AssertNamedNonEmptyContract(root.GetProperty("evidenceContract"), ExpectedEvidenceNames);
        AssertNamedNonEmptyContract(
            root.GetProperty("localEvidenceContract"),
            ["testPath", "testMethod", "coveredSteps", "execution"]);
        ValidateLatestLocalExecution(root.GetProperty("latestLocalExecution"));
        string[] requiredClosureSteps = root.GetProperty("requiredClosureSteps").EnumerateArray()
            .Select(static item => item.GetString()!).ToArray();
        Assert.Contains("backup_restore", requiredClosureSteps);

        IReadOnlyDictionary<string, string[]> catalogGapsByModel = catalog.RootElement.GetProperty("models")
            .EnumerateArray()
            .ToDictionary(
                static item => item.GetProperty("id").GetString()!,
                static item => item.GetProperty("gaps").EnumerateArray()
                    .Select(static gap => gap.GetString()!).ToArray(),
                StringComparer.Ordinal);
        JsonElement[] models = root.GetProperty("models").EnumerateArray().ToArray();

        Assert.Equal(ExpectedModelIds.Length, models.Length);
        Assert.Equal(ExpectedModelIds, models.Select(static item => item.GetProperty("id").GetString() ?? string.Empty));
        Assert.All(models, model => ValidateModel(model, catalogGapsByModel, repositoryRoot, requiredClosureSteps));
        Assert.All(models.Where(static model => model.GetProperty("journeyState").GetString() == "partial"),
            model => AssertPartialJourneyHasExecutedEvidence(model, repositoryRoot, requiredClosureSteps));
        Assert.DoesNotContain(models, static model => model.GetProperty("journeyState").GetString() == "complete");
    }

    private static void ValidateModel(
        JsonElement model,
        IReadOnlyDictionary<string, string[]> catalogGapsByModel,
        string repositoryRoot,
        IReadOnlyCollection<string> requiredClosureSteps)
    {
        string modelId = model.GetProperty("id").GetString()!;
        Assert.True(catalogGapsByModel.TryGetValue(modelId, out string[]? expectedGaps), $"Unknown catalog model: {modelId}");
        AssertOneOf(model.GetProperty("journeyState").GetString(), ExpectedJourneyStates);
        AssertNonEmpty(model.GetProperty("claimBoundary").GetString());
        string[] matrixGaps = model.GetProperty("gapIds").EnumerateArray().Select(static gap => gap.GetString()!).ToArray();
        Assert.NotEmpty(matrixGaps);
        Assert.Equal(matrixGaps.Length, matrixGaps.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expectedGaps!.OrderBy(static gap => gap, StringComparer.Ordinal),
            matrixGaps.OrderBy(static gap => gap, StringComparer.Ordinal));
        AssertRepositoryFile(repositoryRoot, model.GetProperty("localPrerequisitePath").GetString()!);

        JsonElement samplePath = model.GetProperty("samplePath");
        Assert.True(samplePath.ValueKind is JsonValueKind.String or JsonValueKind.Null);
        if (samplePath.ValueKind == JsonValueKind.String)
            AssertRepositoryFile(repositoryRoot, samplePath.GetString()!);

        if (model.GetProperty("journeyState").GetString() == "partial")
            ValidateLocalEvidence(model.GetProperty("executedLocalEvidence"), repositoryRoot, requiredClosureSteps);

        JsonElement surfaces = model.GetProperty("surfaces");
        Assert.Equal(ExpectedSurfaceNames, surfaces.EnumerateObject().Select(static item => item.Name));
        foreach (string name in ExpectedSurfaceNames)
        {
            JsonElement surface = surfaces.GetProperty(name);
            AssertOneOf(surface.GetProperty("state").GetString(), ExpectedSurfaceStates);
            AssertOneOf(surface.GetProperty("evidence").GetString(), ExpectedEvidenceNames);
            AssertRepositoryFile(repositoryRoot, surface.GetProperty("sourcePath").GetString()!);
        }
    }

    private static void AssertNamedNonEmptyContract(JsonElement contract, IEnumerable<string> expectedNames)
    {
        Assert.Equal(expectedNames, contract.EnumerateObject().Select(static item => item.Name));
        foreach (string name in expectedNames)
            AssertNonEmpty(contract.GetProperty(name).GetString());
    }

    private static void ValidateLocalEvidence(
        JsonElement evidence,
        string repositoryRoot,
        IReadOnlyCollection<string> requiredClosureSteps)
    {
        Assert.Equal(JsonValueKind.Array, evidence.ValueKind);
        JsonElement[] entries = evidence.EnumerateArray().ToArray();
        Assert.NotEmpty(entries);
        foreach (JsonElement entry in entries)
        {
            string testPath = entry.GetProperty("testPath").GetString()!;
            string testMethod = entry.GetProperty("testMethod").GetString()!;
            AssertRepositoryFile(repositoryRoot, testPath);
            AssertNonEmpty(testMethod);
            string source = File.ReadAllText(Path.Combine(repositoryRoot, testPath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains(testMethod, source, StringComparison.Ordinal);

            string[] coveredSteps = entry.GetProperty("coveredSteps").EnumerateArray()
                .Select(static step => step.GetString()!).ToArray();
            Assert.NotEmpty(coveredSteps);
            Assert.Equal(coveredSteps.Length, coveredSteps.Distinct(StringComparer.Ordinal).Count());
            Assert.All(coveredSteps, step => Assert.Contains(step, requiredClosureSteps));

            string execution = entry.GetProperty("execution").GetString()!;
            Assert.StartsWith("dotnet test ", execution, StringComparison.Ordinal);
            Assert.Contains("--filter FullyQualifiedName~", execution, StringComparison.Ordinal);
        }
    }

    private static void AssertPartialJourneyHasExecutedEvidence(
        JsonElement model,
        string repositoryRoot,
        IReadOnlyCollection<string> requiredClosureSteps)
    {
        JsonElement evidence = model.GetProperty("executedLocalEvidence");
        ValidateLocalEvidence(evidence, repositoryRoot, requiredClosureSteps);
        string[] coveredSteps = evidence.EnumerateArray()
            .SelectMany(static entry => entry.GetProperty("coveredSteps").EnumerateArray())
            .Select(static step => step.GetString()!)
            .ToArray();
        Assert.Contains("same_seed_fixture", coveredSteps);
        Assert.Contains("embedded", coveredSteps);
        Assert.Contains("result_reconciliation", coveredSteps);
    }

    private static void ValidateLatestLocalExecution(JsonElement execution)
    {
        Assert.Equal("2026-09-20", execution.GetProperty("executedOn").GetString());
        AssertNonEmpty(execution.GetProperty("claimBoundary").GetString());
        JsonElement[] runs = execution.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Equal(2, runs.Length);

        JsonElement core = Assert.Single(runs, static run => run.GetProperty("id").GetString() == "core-minimum-journeys");
        Assert.Equal("passed", core.GetProperty("status").GetString());
        Assert.Equal(23, core.GetProperty("passed").GetInt32());
        Assert.Equal(0, core.GetProperty("failed").GetInt32());

        JsonElement kv = Assert.Single(runs, static run => run.GetProperty("id").GetString() == "kv-protocol-journeys");
        Assert.Equal("failed", kv.GetProperty("status").GetString());
        Assert.Equal(8, kv.GetProperty("passed").GetInt32());
        Assert.Equal(3, kv.GetProperty("failed").GetInt32());
        Assert.Equal("frame_transport_error", kv.GetProperty("failureCode").GetString());
        Assert.Contains("HTTP/2", kv.GetProperty("failureBoundary").GetString(), StringComparison.Ordinal);
    }

    private static JsonDocument ParseJson(string repositoryRoot, string relativePath)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))));

    private static void AssertRepositoryFile(string repositoryRoot, string relativePath)
        => Assert.True(File.Exists(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))),
            $"Matrix path does not exist: {relativePath}");

    private static void AssertNonEmpty(string? value)
        => Assert.False(string.IsNullOrWhiteSpace(value));

    private static void AssertOneOf(string? value, params string[] allowed)
        => Assert.True(value is not null && allowed.Contains(value, StringComparer.Ordinal),
            $"Unexpected matrix value: {value}");

    private static string FindRepositoryRoot()
    {
        string? buildRepositoryRoot = typeof(NineModelJourneyMatrixTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(static attribute => attribute.Key == "SonnetDB.RepositoryRoot")?.Value;
        string[] candidates = [buildRepositoryRoot ?? string.Empty, AppContext.BaseDirectory, Directory.GetCurrentDirectory()];
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
                continue;

            for (DirectoryInfo? directory = new(candidate); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SonnetDB.slnx")))
                    return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the SonnetDB repository root.");
    }
}
