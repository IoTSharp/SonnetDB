using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SonnetDB.Cli;
using SonnetDB.Data.Documents;
using Xunit;

namespace SonnetDB.Core.Tests.Cli;

public sealed class DocumentImportCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-document-import-" + Guid.NewGuid().ToString("N"));

    public DocumentImportCommandTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DryRun_ExtendedJson_WritesMachineReportWithoutOpeningTarget()
    {
        string source = Path.Combine(_root, "devices.ndjson");
        string report = Path.Combine(_root, "dry-run.json");
        string target = Path.Combine(_root, "dry-run-target");
        File.WriteAllLines(source,
        [
            """{"_id":{"$oid":"64b64c2032f9a13f4c8e0001"},"site":"north","counter":{"$numberLong":"9223372036854775807"}}""",
            """{"_id":"device-2","site":"south","script":{"$code":"return 1;"}}""",
        ]);
        var app = CreateApp(out var stdout, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "devices",
            "--path", target,
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(target));
        Assert.Empty(stderr.ToString());
        Assert.Contains("written=0", stdout.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.True(json.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("documentsValidated").GetInt64());
        Assert.Equal(0, json.RootElement.GetProperty("batchesAttempted").GetInt32());
        Assert.Contains(
            json.RootElement.GetProperty("gaps").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "dry_run_target_constraints_not_checked");
        Assert.Contains(
            json.RootElement.GetProperty("gaps").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "extended_json_code_wrapper_preserved");
    }

    [Fact]
    public void DryRun_ExtendedJsonPaths_AdvisesNormalizedBusinessPath()
    {
        string source = Path.Combine(_root, "dates.ndjson");
        string report = Path.Combine(_root, "dates-report.json");
        File.WriteAllLines(source,
        [
            """{"_id":{"$oid":"64b64c2032f9a13f4c8e0001"},"occurredAt":{"$date":{"$numberLong":"0"}}}""",
            """{"_id":{"$oid":"64b64c2032f9a13f4c8e0002"},"occurredAt":{"$date":{"$numberLong":"1000"}}}""",
        ]);
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "events",
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        var suggestion = Assert.Single(
            json.RootElement.GetProperty("indexSuggestions").EnumerateArray(),
            item => item.GetProperty("paths")[0].GetString() == "$.occurredAt");
        Assert.Equal("path", suggestion.GetProperty("kind").GetString());
        Assert.DoesNotContain(
            json.RootElement.GetProperty("indexSuggestions").EnumerateArray(),
            item => item.GetProperty("paths")[0].GetString()!.Contains("$date", StringComparison.Ordinal));
    }

    [Fact]
    public void DryRun_OutOfRangeExtendedDate_ReportsItemAndContinues()
    {
        string source = Path.Combine(_root, "invalid-date.ndjson");
        string report = Path.Combine(_root, "invalid-date-report.json");
        File.WriteAllLines(source,
        [
            """{"_id":"bad","occurredAt":{"$date":{"$numberLong":"9223372036854775807"}}}""",
            """{"_id":"good","occurredAt":{"$date":{"$numberLong":"0"}}}""",
        ]);
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "events",
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(1, exitCode);
        Assert.Contains("invalid_source_document", stderr.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.Equal(2, json.RootElement.GetProperty("documentsRead").GetInt64());
        Assert.Equal(1, json.RootElement.GetProperty("documentsValidated").GetInt64());
        Assert.Contains(
            json.RootElement.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "invalid_source_document");
    }

    [Fact]
    public void DryRun_InvalidJsonArray_StillWritesMachineReport()
    {
        string source = Path.Combine(_root, "broken.json");
        string report = Path.Combine(_root, "broken-report.json");
        File.WriteAllText(source, """[{"_id":"a"},""");
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "docs",
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(1, exitCode);
        Assert.Contains("invalid_source_document", stderr.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(
            json.RootElement.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "invalid_source_document");
    }

    [Fact]
    public void DryRun_JsonArray_StreamsAllDocuments()
    {
        string source = Path.Combine(_root, "devices.json");
        string report = Path.Combine(_root, "array-report.json");
        File.WriteAllText(source, """[{"_id":"a","value":1},{"_id":"b","value":2}]""");
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "devices",
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.Equal(2, json.RootElement.GetProperty("documentsRead").GetInt64());
        Assert.Equal(2, json.RootElement.GetProperty("documentsValidated").GetInt64());
    }

    /// <summary>验证 auto 能跳过 UTF-8 BOM 并按 JSON array 逐项读取。</summary>
    [Fact]
    public void DryRun_Utf8BomJsonArray_AutoDetectsAndStreamsAllDocuments()
    {
        string source = Path.Combine(_root, "bom-devices.json");
        string report = Path.Combine(_root, "bom-array-report.json");
        File.WriteAllText(
            source,
            """[{"_id":"a","value":1},{"_id":"b","value":2}]""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "devices",
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.Equal("json-array", json.RootElement.GetProperty("sourceFormat").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("documentsRead").GetInt64());
        Assert.Equal(2, json.RootElement.GetProperty("documentsValidated").GetInt64());
    }

    [Fact]
    public void DryRun_NdjsonLineAboveBatchBudget_ReportsBoundedError()
    {
        string source = Path.Combine(_root, "oversized.ndjson");
        string report = Path.Combine(_root, "oversized-report.json");
        using (var stream = File.Create(source))
            stream.SetLength((12L * 1024 * 1024) + 1);
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "docs",
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(1, exitCode);
        Assert.Contains("document_too_large_for_bulk", stderr.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.Contains(
            json.RootElement.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "document_too_large_for_bulk");
    }

    [Fact]
    public void DryRun_JsonArrayItemAboveBatchBudget_ReportsBoundedError()
    {
        string source = Path.Combine(_root, "oversized-array.json");
        string report = Path.Combine(_root, "oversized-array-report.json");
        using (var writer = new StreamWriter(source, append: false, new UTF8Encoding(false)))
        {
            writer.Write("[{\"_id\":\"good\"},{\"_id\":\"large\",\"payload\":\"");
            string chunk = new('x', 1024 * 1024);
            for (int i = 0; i < 13; i++)
                writer.Write(chunk);
            writer.Write("\"}]");
        }
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "docs",
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(1, exitCode);
        Assert.Contains("document_too_large_for_bulk", stderr.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.Equal(2, json.RootElement.GetProperty("documentsRead").GetInt64());
        Assert.Equal(1, json.RootElement.GetProperty("documentsValidated").GetInt64());
        Assert.Contains(
            json.RootElement.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "document_too_large_for_bulk"
                && item.GetProperty("sourceOrdinal").GetInt64() == 2);
    }

    [Fact]
    public void DryRun_NdjsonInvalidUtf8_ReportsSourceReadFailure()
    {
        string source = Path.Combine(_root, "invalid-utf8.ndjson");
        string report = Path.Combine(_root, "invalid-utf8-report.json");
        File.WriteAllBytes(source, [0x7B, 0xFF, 0x7D]);
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "docs",
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(1, exitCode);
        Assert.Contains("source_read_failed", stderr.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.Contains(
            json.RootElement.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "source_read_failed");
    }

    [Fact]
    public void DryRun_ResolvesLocalAndRemoteProfilesWithoutConnecting()
    {
        string source = Path.Combine(_root, "profile.jsonl");
        File.WriteAllText(source, """{"_id":"a","value":1}""");
        var profiles = new CliProfileStore(Path.Combine(_root, "migration-profiles.json"));
        profiles.UpsertLocal(new CliLocalProfile("edge", Path.Combine(_root, "edge-data")));
        profiles.Upsert(new CliRemoteProfile("cloud", "http://127.0.0.1:59999", "app", "secret", 2));

        foreach (string profile in new[] { "edge", "cloud" })
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var app = new CliApplication(TextReader.Null, stdout, stderr, profiles);
            int exitCode = app.Run([
                "document", "import",
                "--input", source,
                "--collection", "docs",
                "--profile", profile,
                "--dry-run",
            ]);
            Assert.Equal(0, exitCode);
            Assert.Empty(stderr.ToString());
        }
        Assert.False(Directory.Exists(Path.Combine(_root, "edge-data")));
    }

    [Fact]
    public async Task ExtendedJsonDateWithSiblingProperty_PreservesOriginalObject()
    {
        string source = Path.Combine(_root, "date-object.jsonl");
        string target = Path.Combine(_root, "date-object-target");
        File.WriteAllText(
            source,
            """{"_id":"a","value":{"$date":"2025-01-01T00:00:00Z","unit":"utc"}}""");
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "docs",
            "--path", target,
        ]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        using var client = new SndbDocumentClient($"Data Source={target}");
        var row = await client.FindOneAsync("docs", "a");
        Assert.NotNull(row);
        using var json = JsonDocument.Parse(row.Json);
        JsonElement value = json.RootElement.GetProperty("value");
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal("utc", value.GetProperty("unit").GetString());
    }

    /// <summary>验证业务文档中的 id/document 同名字段不会触发隐式解包或字段丢失。</summary>
    [Fact]
    public async Task JsonImport_IdAndDocumentBusinessFields_PreservesWholeDocumentAndConfiguredId()
    {
        string source = Path.Combine(_root, "business-fields.jsonl");
        string target = Path.Combine(_root, "business-fields-target");
        File.WriteAllText(
            source,
            """{"_id":"mongo-1","id":"biz-1","document":{"value":1},"tenant":"north"}""");
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "docs",
            "--path", target,
            "--id-path", "_id",
        ]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        using var client = new SndbDocumentClient($"Data Source={target}");
        var row = await client.FindOneAsync("docs", "mongo-1");
        Assert.NotNull(row);
        Assert.Null(await client.FindOneAsync("docs", "biz-1"));
        using var json = JsonDocument.Parse(row.Json);
        Assert.Equal("mongo-1", json.RootElement.GetProperty("_id").GetString());
        Assert.Equal("biz-1", json.RootElement.GetProperty("id").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("document").GetProperty("value").GetInt32());
        Assert.Equal("north", json.RootElement.GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task ReplaceImport_RetryReplaysDeterministicBatches_AndResumeUsesCheckpoint()
    {
        string source = Path.Combine(_root, "replace.jsonl");
        string target = Path.Combine(_root, "database");
        string checkpoint = Path.Combine(_root, "checkpoint.json");
        string report = Path.Combine(_root, "report.json");
        File.WriteAllLines(source,
        [
            """{"_id":"a","value":1}""",
            """{"_id":"b","value":2}""",
        ]);
        var app = CreateApp(out var stdout, out var stderr);
        string[] command =
        [
            "document", "import",
            "--input", source,
            "--collection", "docs",
            "--path", target,
            "--mode", "replace",
            "--batch-size", "1",
            "--checkpoint", checkpoint,
            "--report", report,
        ];

        Assert.Equal(0, app.Run(command));
        Assert.Empty(stderr.ToString());
        using (var documents = new SndbDocumentClient($"Data Source={target}"))
            Assert.Equal(2, await documents.CountAsync("docs"));

        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, app.Run(command));
        using (var retry = JsonDocument.Parse(File.ReadAllBytes(report)))
            Assert.Equal(2, retry.RootElement.GetProperty("batchesReplayed").GetInt32());

        stdout.GetStringBuilder().Clear();
        string[] resume = [.. command, "--resume"];
        Assert.Equal(0, app.Run(resume));
        using var resumed = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.True(resumed.RootElement.GetProperty("resumed").GetBoolean());
        Assert.Equal(0, resumed.RootElement.GetProperty("documentsRead").GetInt64());
        Assert.Equal(0, resumed.RootElement.GetProperty("batchesAttempted").GetInt32());
    }

    [Fact]
    public void UnorderedResume_PreservesErrorBetweenCommittedBatches()
    {
        string source = Path.Combine(_root, "resume-errors.jsonl");
        string target = Path.Combine(_root, "resume-errors-target");
        string checkpoint = Path.Combine(_root, "resume-errors.checkpoint.json");
        string report = Path.Combine(_root, "resume-errors-report.json");
        File.WriteAllLines(source,
        [
            """{"_id":"good-a","value":1}""",
            """{"_id":"bad","occurredAt":{"$date":{"$numberLong":"9223372036854775807"}}}""",
            """{"_id":"good-b","value":2}""",
        ]);
        string[] command =
        [
            "document", "import",
            "--input", source,
            "--collection", "docs",
            "--path", target,
            "--batch-size", "1",
            "--checkpoint", checkpoint,
            "--report", report,
        ];
        var first = CreateApp(out _, out var firstError);

        Assert.Equal(1, first.Run(command));
        Assert.Contains("invalid_source_document", firstError.ToString());
        Assert.True(File.Exists(checkpoint));
        using (var firstReport = JsonDocument.Parse(File.ReadAllBytes(report)))
            Assert.Equal(2, firstReport.RootElement.GetProperty("documentsWritten").GetInt64());

        JsonObject legacyCheckpoint = JsonNode.Parse(File.ReadAllText(checkpoint))!.AsObject();
        Assert.True(legacyCheckpoint.Remove("errorCount"));
        File.WriteAllText(checkpoint, legacyCheckpoint.ToJsonString());

        var resumed = CreateApp(out _, out var resumedError);
        Assert.Equal(1, resumed.Run([.. command, "--resume"]));
        Assert.Contains("invalid_source_document", resumedError.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.True(json.RootElement.GetProperty("resumed").GetBoolean());
        Assert.Contains(
            json.RootElement.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "invalid_source_document");
    }

    [Fact]
    public void UnorderedCheckpoint_BoundsErrorSamplesAndPreservesTotalOnResume()
    {
        const int expectedErrors = 1005;
        string source = Path.Combine(_root, "bounded-errors.jsonl");
        string target = Path.Combine(_root, "bounded-errors-target");
        string checkpoint = Path.Combine(_root, "bounded-errors.checkpoint.json");
        string report = Path.Combine(_root, "bounded-errors-report.json");
        var lines = Enumerable.Range(0, expectedErrors)
            .Select(static index => "{\"_id\":\"bad-"
                + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\",\"occurredAt\":{\"$date\":{\"$numberLong\":\"9223372036854775807\"}}}")
            .Append("""{"_id":"good","value":1}""");
        File.WriteAllLines(source, lines);
        string[] command =
        [
            "document", "import",
            "--input", source,
            "--collection", "docs",
            "--path", target,
            "--batch-size", "1",
            "--checkpoint", checkpoint,
            "--report", report,
        ];

        var first = CreateApp(out _, out _);
        Assert.Equal(1, first.Run(command));
        using (var firstReport = JsonDocument.Parse(File.ReadAllBytes(report)))
        {
            Assert.Equal(expectedErrors, firstReport.RootElement.GetProperty("errorCount").GetInt64());
            Assert.True(firstReport.RootElement.GetProperty("errorsTruncated").GetBoolean());
            Assert.Equal(1000, firstReport.RootElement.GetProperty("errors").GetArrayLength());
        }
        using (var saved = JsonDocument.Parse(File.ReadAllBytes(checkpoint)))
        {
            Assert.Equal(expectedErrors, saved.RootElement.GetProperty("errorCount").GetInt64());
            Assert.Equal(1000, saved.RootElement.GetProperty("errors").GetArrayLength());
        }

        var resumed = CreateApp(out _, out _);
        Assert.Equal(1, resumed.Run([.. command, "--resume"]));
        using var resumedReport = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.True(resumedReport.RootElement.GetProperty("resumed").GetBoolean());
        Assert.Equal(expectedErrors, resumedReport.RootElement.GetProperty("errorCount").GetInt64());
        Assert.True(resumedReport.RootElement.GetProperty("errorsTruncated").GetBoolean());
        Assert.Equal(1000, resumedReport.RootElement.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public async Task MongodumpBson_ImportsCommonTypes_AndReportsIndexSuggestions()
    {
        string dump = Path.Combine(_root, "dump");
        string target = Path.Combine(_root, "bson-target");
        string report = Path.Combine(_root, "bson-report.json");
        Directory.CreateDirectory(dump);
        File.WriteAllBytes(Path.Combine(dump, "devices.bson"), BuildBsonDocument());
        File.WriteAllText(
            Path.Combine(dump, "devices.metadata.json"),
            """{"indexes":[{"name":"site_1","key":{"site":1}},{"name":"metadata_wildcard","key":{"metadata.$**":1}},{"name":"invalid_compound_wildcard","key":{"metadata.$**":1,"site":1}}]}""");
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", dump,
            "--collection", "devices",
            "--path", target,
            "--report", report,
        ]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        using (var documents = new SndbDocumentClient($"Data Source={target}"))
        {
            var row = await documents.FindOneAsync("devices", "64b64c2032f9a13f4c8e0001");
            Assert.NotNull(row);
            using var body = JsonDocument.Parse(row.Json);
            Assert.Equal("north", body.RootElement.GetProperty("site").GetString());
            Assert.Equal(7, body.RootElement.GetProperty("counter").GetInt32());
        }
        using var machineReport = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.Equal("bson", machineReport.RootElement.GetProperty("sourceFormat").GetString());
        Assert.Contains(
            machineReport.RootElement.GetProperty("indexSuggestions").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "wildcard"
                && item.GetProperty("paths")[0].GetString() == "$.metadata");
        Assert.Contains(
            machineReport.RootElement.GetProperty("indexSuggestions").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "invalid_compound_wildcard"
                && !item.GetProperty("supported").GetBoolean());
    }

    [Fact]
    public void MongodumpInvalidMetadata_ImportsDocumentsAndReportsGap()
    {
        string dump = Path.Combine(_root, "broken-metadata-dump");
        string report = Path.Combine(_root, "broken-metadata-report.json");
        Directory.CreateDirectory(dump);
        File.WriteAllBytes(Path.Combine(dump, "devices.bson"), BuildBsonDocument());
        File.WriteAllText(Path.Combine(dump, "devices.metadata.json"), "{");
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", dump,
            "--collection", "devices",
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.Contains(
            json.RootElement.GetProperty("gaps").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "mongodb_metadata_invalid"
                && item.GetProperty("status").GetString() == "partial");
    }

    [Fact]
    public void MongodumpBson_JavaScriptCode_IsReportedInsteadOfSilentlyConverted()
    {
        string source = Path.Combine(_root, "code.bson");
        string report = Path.Combine(_root, "code-report.json");
        File.WriteAllBytes(source, BuildBsonJavaScriptCodeDocument());
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "scripts",
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(1, exitCode);
        Assert.Contains("unsupported_bson_type", stderr.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.Equal(0, json.RootElement.GetProperty("documentsValidated").GetInt64());
        Assert.Contains(
            json.RootElement.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "unsupported_bson_type");
    }

    [Fact]
    public void MongodumpBson_TruncatedLengthHeader_WritesMachineReport()
    {
        string source = Path.Combine(_root, "truncated-header.bson");
        string report = Path.Combine(_root, "truncated-header-report.json");
        File.WriteAllBytes(source, [5, 0]);
        var app = CreateApp(out _, out var stderr);

        int exitCode = app.Run([
            "document", "import",
            "--input", source,
            "--collection", "docs",
            "--dry-run",
            "--report", report,
        ]);

        Assert.Equal(1, exitCode);
        Assert.Contains("invalid_bson", stderr.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(
            json.RootElement.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "invalid_bson");
        Assert.Contains(
            json.RootElement.GetProperty("gaps").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "invalid_bson"
                && item.GetProperty("status").GetString() == "partial");
    }

    [Fact]
    public void MongodumpBson_OldBinarySubtype_RemovesEmbeddedLength()
    {
        var gaps = new DocumentImportGapCollector();
        using var stream = new MemoryStream(BuildBsonOldBinaryDocument());

        BsonDocumentReader.Result result = Assert.Single(BsonDocumentReader.Read(stream, "binary.bson", gaps));

        Assert.Null(result.Error);
        using var json = JsonDocument.Parse(result.Json!);
        JsonElement binary = json.RootElement.GetProperty("blob").GetProperty("$binary");
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), binary.GetProperty("base64").GetString());
        Assert.Equal("02", binary.GetProperty("subType").GetString());
        Assert.Contains(gaps.ToList(), gap => gap.Code == "bson_old_binary_subtype");
    }

    private CliApplication CreateApp(out StringWriter stdout, out StringWriter stderr)
    {
        stdout = new StringWriter();
        stderr = new StringWriter();
        return new CliApplication(
            TextReader.Null,
            stdout,
            stderr,
            new CliProfileStore(Path.Combine(_root, "profiles.json")));
    }

    private static byte[] BuildBsonDocument()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0);
        writer.Write((byte)0x07);
        WriteCString(writer, "_id");
        writer.Write(Convert.FromHexString("64b64c2032f9a13f4c8e0001"));
        writer.Write((byte)0x02);
        WriteCString(writer, "site");
        byte[] site = Encoding.UTF8.GetBytes("north");
        writer.Write(site.Length + 1);
        writer.Write(site);
        writer.Write((byte)0);
        writer.Write((byte)0x10);
        WriteCString(writer, "counter");
        writer.Write(7);
        writer.Write((byte)0);
        writer.Flush();
        int length = checked((int)stream.Length);
        stream.Position = 0;
        writer.Write(length);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildBsonJavaScriptCodeDocument()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0);
        writer.Write((byte)0x07);
        WriteCString(writer, "_id");
        writer.Write(Convert.FromHexString("64b64c2032f9a13f4c8e0001"));
        writer.Write((byte)0x0D);
        WriteCString(writer, "script");
        byte[] code = Encoding.UTF8.GetBytes("return 1;");
        writer.Write(code.Length + 1);
        writer.Write(code);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Flush();
        int length = checked((int)stream.Length);
        stream.Position = 0;
        writer.Write(length);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildBsonOldBinaryDocument()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0);
        writer.Write((byte)0x05);
        WriteCString(writer, "blob");
        writer.Write(7);
        writer.Write((byte)0x02);
        writer.Write(3);
        writer.Write(new byte[] { 1, 2, 3 });
        writer.Write((byte)0);
        writer.Flush();
        int length = checked((int)stream.Length);
        stream.Position = 0;
        writer.Write(length);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
