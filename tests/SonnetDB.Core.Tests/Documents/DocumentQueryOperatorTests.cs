using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentQueryOperatorTests
{
    [Fact]
    public void DocumentQuery_LegacyConstructorAndDeconstruct_DefaultsToOrdinal()
    {
        var filter = Field("$.name", DocumentFilterOperator.Equal, "pump");
        var query = new DocumentQuery(filter, null, null, 10, 2);

        var (actualFilter, projection, sort, limit, skip) = query;

        Assert.Same(filter, actualFilter);
        Assert.Null(projection);
        Assert.Empty(sort!);
        Assert.Equal(10, limit);
        Assert.Equal(2, skip);
        Assert.Equal(DocumentCollation.Ordinal, query.Collation);
    }

    [Fact]
    public void Matches_ElementMatchObjectConditions_RequiresSameArrayElement()
    {
        var row = Row("""
            {
              "results": [
                { "score": 95, "grade": "B" },
                { "score": 85, "grade": "A" }
              ]
            }
            """);
        var impossibleSameElement = new DocumentFieldFilter(
            DocumentFieldRef.JsonPath("$.results"),
            DocumentFilterOperator.ElementMatch,
            new DocumentAndFilter([
                Field("$.score", DocumentFilterOperator.GreaterThanOrEqual, 90L),
                Field("$.grade", DocumentFilterOperator.Equal, "A"),
            ]));
        var matchingSameElement = new DocumentFieldFilter(
            DocumentFieldRef.JsonPath("$.results"),
            DocumentFilterOperator.ElementMatch,
            new DocumentAndFilter([
                Field("$.score", DocumentFilterOperator.GreaterThanOrEqual, 90L),
                Field("$.grade", DocumentFilterOperator.Equal, "B"),
            ]));

        Assert.False(DocumentQueryPlanner.Matches(impossibleSameElement, row));
        Assert.True(DocumentQueryPlanner.Matches(matchingSameElement, row));
    }

    [Fact]
    public void Matches_ElementMatchScalarConditions_UsesElementDocumentValue()
    {
        var row = Row("""{"values":[4,7,12]}""");
        var filter = new DocumentFieldFilter(
            DocumentFieldRef.JsonPath("$.values"),
            DocumentFilterOperator.ElementMatch,
            new DocumentAndFilter([
                new DocumentFieldFilter(
                    DocumentFieldRef.Document,
                    DocumentFilterOperator.GreaterThan,
                    5L),
                new DocumentFieldFilter(
                    DocumentFieldRef.Document,
                    DocumentFilterOperator.LessThan,
                    10L),
            ]));

        Assert.True(DocumentQueryPlanner.Matches(filter, row));
    }

    [Fact]
    public void Matches_RegexOptionsAndFieldType_UsesSharedMatcher()
    {
        var row = Row("""{"name":"Pump-17","count":17}""");
        var regex = Field(
            "$.name",
            DocumentFilterOperator.Regex,
            new DocumentRegex("^pump-[0-9]+$", "i"));
        var numericRegex = Field("$.count", DocumentFilterOperator.Regex, "17");

        Assert.True(DocumentQueryPlanner.Matches(regex, row));
        Assert.False(DocumentQueryPlanner.Matches(numericRegex, row));
    }

    [Fact]
    public void Matches_RegexSafetyLimits_AreEnforced()
    {
        var row = Row("""{"name":"value"}""");
        var longPattern = Field(
            "$.name",
            DocumentFilterOperator.Regex,
            new string('a', RegexPatternMatcher.MaxPatternLength + 1));
        var invalidOptions = Field(
            "$.name",
            DocumentFilterOperator.Regex,
            new DocumentRegex("value", "q"));
        string longInput = new('a', RegexPatternMatcher.MaxInputLength + 1);
        var longInputRow = Row("{\"name\":\"" + longInput + "\"}");
        var simplePattern = Field("$.name", DocumentFilterOperator.Regex, "a");

        Assert.Throws<InvalidOperationException>(() => DocumentQueryPlanner.Matches(longPattern, row));
        Assert.Throws<InvalidOperationException>(() => DocumentQueryPlanner.Matches(invalidOptions, row));
        Assert.Throws<InvalidOperationException>(() => DocumentQueryPlanner.Matches(simplePattern, longInputRow));
    }

    [Fact]
    public void Matches_TypeSizeAndAll_UseJsonNativeSemantics()
    {
        var row = Row("""
            {
              "none": null,
              "enabled": true,
              "count": 3,
              "name": "Pump",
              "meta": { "zone": "A" },
              "tags": ["north", "Critical"]
            }
            """);

        Assert.True(DocumentQueryPlanner.Matches(
            Field("$.none", DocumentFilterOperator.Type, DocumentJsonType.Null), row));
        Assert.True(DocumentQueryPlanner.Matches(
            Field("$.count", DocumentFilterOperator.Type, "number"), row));
        Assert.True(DocumentQueryPlanner.Matches(
            Field("$.meta", DocumentFilterOperator.Type, new object?[] { "array", "object" }), row));
        Assert.True(DocumentQueryPlanner.Matches(
            Field("$.tags", DocumentFilterOperator.Size, 2L), row));
        Assert.False(DocumentQueryPlanner.Matches(
            Field("$.name", DocumentFilterOperator.Size, 4L), row));

        var all = Field(
            "$.tags",
            DocumentFilterOperator.All,
            new object?[] { "NORTH", "critical" });
        Assert.False(DocumentQueryPlanner.Matches(all, row, DocumentCollation.Ordinal));
        Assert.True(DocumentQueryPlanner.Matches(all, row, DocumentCollation.OrdinalIgnoreCase));
        Assert.False(DocumentQueryPlanner.Matches(
            Field("$.tags", DocumentFilterOperator.All, Array.Empty<object?>()), row));
    }

    [Fact]
    public void Matches_ComplexNot_NegatesNestedLogicalExpression()
    {
        var blockedRule = new DocumentNotFilter(new DocumentOrFilter([
            Field("$.status", DocumentFilterOperator.Equal, "stopped"),
            new DocumentAndFilter([
                Field("$.name", DocumentFilterOperator.Regex, new DocumentRegex("^pump", "i")),
                Field(
                    "$.tags",
                    DocumentFilterOperator.All,
                    new object?[] { "north", "critical" }),
            ]),
        ]));

        Assert.False(DocumentQueryPlanner.Matches(
            blockedRule,
            Row("""{"status":"active","name":"Pump-17","tags":["north","critical"]}""")));
        Assert.True(DocumentQueryPlanner.Matches(
            blockedRule,
            Row("""{"status":"active","name":"fan","tags":["north","critical"]}""")));
    }

    [Fact]
    public void JsonPath_NestedArrays_RequireExplicitIndexes()
    {
        var row = Row("""
            {
              "groups": [{ "items": [{ "name": "A" }, { "name": "B" }] }],
              "a.b": [42]
            }
            """);

        Assert.True(JsonPathEvaluator.TryEvaluate(
            row.Json,
            "$.groups[0].items[1].name",
            out object? explicitValue));
        Assert.Equal("B", explicitValue);
        Assert.True(JsonPathEvaluator.TryEvaluate(row.Json, "$['a.b'][0]", out object? quotedValue));
        Assert.Equal(42d, Assert.IsType<double>(quotedValue));
        Assert.False(JsonPathEvaluator.TryEvaluate(
            row.Json,
            "$.groups.items[0].name",
            out _));

        Assert.Throws<ArgumentException>(() => JsonPath.Parse("$.groups[-1]"));
        Assert.Throws<ArgumentException>(() => JsonPath.Parse("$.groups[*]"));
        Assert.Throws<ArgumentException>(() => JsonPath.Parse("$..name"));
    }

    [Fact]
    public void Matches_ImplicitArrayTraversal_UsesAnyForPositiveAndAllForNegativeOperators()
    {
        var row = Row("""{"items":[{"name":"pump"},{"name":"fan"}]}""");

        Assert.True(DocumentQueryPlanner.Matches(
            Field("$.items.name", DocumentFilterOperator.Equal, "fan"), row));
        Assert.False(DocumentQueryPlanner.Matches(
            Field("$.items.name", DocumentFilterOperator.Equal, "valve"), row));
        Assert.False(DocumentQueryPlanner.Matches(
            Field("$.items.name", DocumentFilterOperator.NotEqual, "fan"), row));
        Assert.True(DocumentQueryPlanner.Matches(
            Field("$.items.name", DocumentFilterOperator.NotEqual, "valve"), row));
        Assert.False(DocumentQueryPlanner.Matches(
            Field("$.items.name", DocumentFilterOperator.NotIn, new object?[] { "pump", "valve" }), row));
        Assert.True(DocumentQueryPlanner.Matches(
            Field("$.items.name", DocumentFilterOperator.NotIn, new object?[] { "motor", "valve" }), row));
    }

    [Fact]
    public void Matches_InvalidFilterShapesAndOperands_Throw()
    {
        var row = Row("""{"name":"pump","tags":["north"]}""");

        Assert.Throws<InvalidOperationException>(() =>
            DocumentQueryPlanner.Matches(new DocumentAndFilter([]), row));
        Assert.Throws<InvalidOperationException>(() =>
            DocumentQueryPlanner.Matches(
                new DocumentOrFilter(new DocumentFilter[] { null! }),
                row));
        Assert.Throws<InvalidOperationException>(() =>
            DocumentQueryPlanner.Matches(
                Field("$.name", DocumentFilterOperator.In, "pump"),
                row));
        Assert.Throws<InvalidOperationException>(() =>
            DocumentQueryPlanner.Matches(
                Field("$.tags", DocumentFilterOperator.ElementMatch, "not-a-filter"),
                row));
        Assert.Throws<InvalidOperationException>(() =>
            DocumentQueryPlanner.Matches(
                Field("$.name", DocumentFilterOperator.Type, new object?[] { "string", "unknown" }),
                row));
        Assert.Throws<InvalidOperationException>(() =>
            DocumentQueryPlanner.Matches(
                Field("$.tags", DocumentFilterOperator.Size, -1L),
                row));
        Assert.Throws<InvalidOperationException>(() =>
            DocumentQueryPlanner.Matches(new UnsupportedDocumentFilter(), row));
        Assert.Throws<InvalidOperationException>(() =>
            DocumentQueryPlanner.Matches(null, row, (DocumentCollation)99));
    }

    [Fact]
    public void Execute_OrdinalIgnoreCaseCollation_ScansOrdinalIndexAndSortsConsistently()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "sndb-document-collation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var db = Tsdb.Open(new TsdbOptions { RootDirectory = root });
            db.Documents.Create(DocumentCollectionSchema.Create(
                "docs",
                [new DocumentPathIndexDefinition("idx_name", "$.name")]));
            var store = db.Documents.Open("docs");
            store.Insert("lower", """{"name":"a"}""");
            store.Insert("upper", """{"name":"B"}""");

            var ordinal = DocumentQueryPlanner.Execute(
                store,
                store.Schema,
                new DocumentQuery(Filter: Field("$.name", DocumentFilterOperator.Equal, "a")));
            var ignoreCase = DocumentQueryPlanner.Execute(
                store,
                store.Schema,
                new DocumentQuery(
                    Filter: Field("$.name", DocumentFilterOperator.Equal, "A"),
                    Collation: DocumentCollation.OrdinalIgnoreCase));
            var ordinalSort = DocumentQueryPlanner.Execute(
                store,
                store.Schema,
                new DocumentQuery(Sort: [new DocumentSort(DocumentFieldRef.JsonPath("$.name"))]));
            var ignoreCaseSort = DocumentQueryPlanner.Execute(
                store,
                store.Schema,
                new DocumentQuery(
                    Sort: [new DocumentSort(DocumentFieldRef.JsonPath("$.name"))],
                    Collation: DocumentCollation.OrdinalIgnoreCase));

            Assert.Equal("document_index", ordinal.AccessPath);
            Assert.Equal("document_scan", ignoreCase.AccessPath);
            Assert.Equal("lower", Assert.Single(ignoreCase.Items).Id);
            Assert.Equal(["upper", "lower"], ordinalSort.Items.Select(static item => item.Id).ToArray());
            Assert.Equal(["lower", "upper"], ignoreCaseSort.Items.Select(static item => item.Id).ToArray());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static DocumentFieldFilter Field(
        string path,
        DocumentFilterOperator op,
        object? value)
        => new(DocumentFieldRef.JsonPath(path), op, value);

    private static DocumentRow Row(string json) => new("doc-1", json, 1);

    private sealed record UnsupportedDocumentFilter : DocumentFilter;
}
