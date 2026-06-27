using SonnetDB.Documents;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentValidatorTests : IDisposable
{
    private readonly string _root;

    public DocumentValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-document-validator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void DocumentValidatorExecutor_WithRequiredTypeRangeEnumAndPattern_ReportsFailures()
    {
        var validator = DocumentCollectionSchema.Create(
            "devices",
            validator: new DocumentValidatorDefinition([
                new DocumentValidatorRuleDefinition("$.site", Required: true, Types: [DocumentValidatorValueType.String], EnumValues: ["north", "south"]),
                new DocumentValidatorRuleDefinition("$.score", Types: [DocumentValidatorValueType.Number], Minimum: 0, Maximum: 10),
                new DocumentValidatorRuleDefinition("$.code", Types: [DocumentValidatorValueType.String], Pattern: "^[A-Z]{2}-[0-9]+$"),
            ])).Validator!;

        var ok = DocumentValidatorExecutor.Validate(validator, """{"site":"north","score":5,"code":"AB-1"}""");
        Assert.True(ok.IsValid);

        var failed = DocumentValidatorExecutor.Validate(validator, """{"site":"west","score":99,"code":"oops"}""");
        Assert.False(failed.IsValid);
        Assert.Equal(["$.site", "$.score", "$.code"], failed.Failures.Select(static failure => failure.Path).ToArray());
    }

    [Fact]
    public void DocumentCollectionSchemaCodec_WithValidator_RoundTrips()
    {
        string path = Path.Combine(_root, DocumentCollectionSchemaCodec.FileName);
        var schema = DocumentCollectionSchema.Create(
            "devices",
            indexes: [new DocumentPathIndexDefinition("idx_site", "$.site")],
            fullTextIndexes: [new DocumentFullTextIndexDefinition("ft_doc", ["document"])],
            validator: new DocumentValidatorDefinition([
                new DocumentValidatorRuleDefinition("$.site", Required: true, Types: [DocumentValidatorValueType.String]),
                new DocumentValidatorRuleDefinition("$.score", Types: [DocumentValidatorValueType.Number], Minimum: 0, Maximum: 10),
            ], DocumentValidationAction.Warn));

        DocumentCollectionSchemaCodec.Save(path, [schema]);
        var loaded = Assert.Single(DocumentCollectionSchemaCodec.Load(path));

        Assert.Equal("devices", loaded.Name);
        Assert.Equal("idx_site", Assert.Single(loaded.Indexes).Name);
        Assert.Equal("ft_doc", Assert.Single(loaded.FullTextIndexes).Name);
        Assert.NotNull(loaded.Validator);
        Assert.Equal(DocumentValidationAction.Warn, loaded.Validator!.Action);
        Assert.Equal(["$.site", "$.score"], loaded.Validator.Rules.Select(static rule => rule.Path).ToArray());
        Assert.Equal(10, loaded.Validator.Rules[1].Maximum);
    }
}
