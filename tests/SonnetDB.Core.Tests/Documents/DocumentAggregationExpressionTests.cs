using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentAggregationExpressionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-document-m32-aggregation-" + Guid.NewGuid().ToString("N"));

    public DocumentAggregationExpressionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ProjectAndGroup_EvaluateExpressionsPushAndAddToSet()
    {
        using var db = Open();
        var store = Create(db);
        store.Upsert("a", """{"category":"pump","qty":2,"price":5,"region":"north"}""");
        store.Upsert("b", """{"category":"pump","qty":3,"price":4,"region":"north"}""");
        store.Upsert("c", """{"qty":1,"price":7,"region":"south"}""");

        var projected = store.Aggregate(new DocumentAggregationPipeline(
        [
            new DocumentProjectStage(
                new DocumentProjection([new DocumentProjectionField("id", DocumentFieldRef.Id)]),
                [
                    new DocumentAggregationComputedField(
                        "total",
                        new DocumentAggregationMultiplyExpression(Field("$.qty"), Field("$.price"))),
                    new DocumentAggregationComputedField(
                        "label",
                        new DocumentAggregationConcatExpression(
                        [
                            new DocumentAggregationIfNullExpression(
                                Field("$.category"),
                                new DocumentAggregationLiteralExpression("unknown")),
                            new DocumentAggregationLiteralExpression("-item"),
                        ])),
                ]),
        ]));

        using (var first = JsonDocument.Parse(projected.Documents[0]))
        {
            Assert.Equal(10, first.RootElement.GetProperty("total").GetInt32());
            Assert.Equal("pump-item", first.RootElement.GetProperty("label").GetString());
        }

        var grouped = store.Aggregate(new DocumentAggregationPipeline(
        [
            new DocumentGroupStage(
                [
                    DocumentAggregationGroupKey.FromExpression(
                        "category",
                        new DocumentAggregationIfNullExpression(
                            Field("$.category"),
                            new DocumentAggregationLiteralExpression("unknown"))),
                ],
                [
                    DocumentAggregationAccumulator.FromExpression(
                        "revenue",
                        DocumentAggregationAccumulatorOperator.Sum,
                        new DocumentAggregationMultiplyExpression(Field("$.qty"), Field("$.price"))),
                    DocumentAggregationAccumulator.FromExpression(
                        "regions",
                        DocumentAggregationAccumulatorOperator.AddToSet,
                        Field("$.region")),
                    DocumentAggregationAccumulator.FromExpression(
                        "quantities",
                        DocumentAggregationAccumulatorOperator.Push,
                        Field("$.qty")),
                ]),
            new DocumentSortStage([new DocumentSort(DocumentFieldRef.JsonPath("$.category"))]),
        ]));

        var groups = grouped.Documents
            .Select(static json => JsonDocument.Parse(json))
            .ToDictionary(
                static document => document.RootElement.GetProperty("category").GetString()!,
                StringComparer.Ordinal);
        try
        {
            Assert.Equal(22, groups["pump"].RootElement.GetProperty("revenue").GetInt32());
            Assert.Equal(["north"], groups["pump"].RootElement.GetProperty("regions").EnumerateArray().Select(static item => item.GetString()!).ToArray());
            Assert.Equal([2, 3], groups["pump"].RootElement.GetProperty("quantities").EnumerateArray().Select(static item => item.GetInt32()).ToArray());
            Assert.Equal(7, groups["unknown"].RootElement.GetProperty("revenue").GetInt32());
        }
        finally
        {
            foreach (var document in groups.Values)
                document.Dispose();
        }
    }

    [Fact]
    public void FieldConstructors_PreserveLegacyNonNullableShapes()
    {
        var key = new DocumentAggregationGroupKey("key", DocumentFieldRef.Id);
        var accumulator = new DocumentAggregationAccumulator(
            "count",
            DocumentAggregationAccumulatorOperator.Count,
            null!);
        key.Deconstruct(out string name, out DocumentFieldRef field);

        Assert.Equal("key", name);
        Assert.Equal(DocumentFieldRef.Id, field);
        Assert.Equal(DocumentFieldRef.Id, key.Field);
        Assert.Null(key.Expression);
        Assert.Null(accumulator.Field);
        Assert.Null(accumulator.Expression);
    }

    [Fact]
    public void ComputedAndCollectedValues_PreserveJsonLookingStringsAndStructuredValues()
    {
        using var db = Open();
        var store = Create(db);
        store.Upsert("a", """{"text":"{\"kind\":\"string\"}","object":{"kind":"object"}}""");

        var projected = store.Aggregate(new DocumentAggregationPipeline(
        [
            new DocumentProjectStage(
                new DocumentProjection([]),
                [
                    new DocumentAggregationComputedField("text", Field("$.text")),
                    new DocumentAggregationComputedField("object", Field("$.object")),
                    new DocumentAggregationComputedField(
                        "literal",
                        new DocumentAggregationLiteralExpression("{\"kind\":\"literal\"}")),
                ]),
        ]));

        using (var document = JsonDocument.Parse(Assert.Single(projected.Documents)))
        {
            Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("text").ValueKind);
            Assert.Equal("{\"kind\":\"string\"}", document.RootElement.GetProperty("text").GetString());
            Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("object").ValueKind);
            Assert.Equal("object", document.RootElement.GetProperty("object").GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("literal").ValueKind);
        }

        var grouped = store.Aggregate(new DocumentAggregationPipeline(
        [
            new DocumentGroupStage(
                [],
                [
                    DocumentAggregationAccumulator.FromExpression(
                        "texts",
                        DocumentAggregationAccumulatorOperator.Push,
                        Field("$.text")),
                    DocumentAggregationAccumulator.FromExpression(
                        "objects",
                        DocumentAggregationAccumulatorOperator.AddToSet,
                        Field("$.object")),
                ]),
        ]));

        using var group = JsonDocument.Parse(Assert.Single(grouped.Documents));
        Assert.Equal(JsonValueKind.String, group.RootElement.GetProperty("texts")[0].ValueKind);
        Assert.Equal(JsonValueKind.Object, group.RootElement.GetProperty("objects")[0].ValueKind);
    }

    [Fact]
    public void GroupKey_LengthPrefixPreventsSeparatorCollisions()
    {
        using var db = Open();
        var store = Create(db);
        store.Upsert("a", """{"left":"a\u001fb","right":"c"}""");
        store.Upsert("b", """{"left":"a","right":"b\u001fc"}""");

        var result = store.Aggregate(new DocumentAggregationPipeline(
        [
            new DocumentGroupStage(
                [
                    new DocumentAggregationGroupKey("left", DocumentFieldRef.JsonPath("$.left")),
                    new DocumentAggregationGroupKey("right", DocumentFieldRef.JsonPath("$.right")),
                ],
                [new DocumentAggregationAccumulator("count", DocumentAggregationAccumulatorOperator.Count)]),
        ]));

        Assert.Equal(2, result.Documents.Count);
    }

    [Fact]
    public void Unwind_DefinesArrayNullMissingEmptyAndScalarRowsWithIndexes()
    {
        using var db = Open();
        var store = Create(db);
        store.Upsert("array", """{"items":["a","b"]}""");
        store.Upsert("missing", """{"name":"missing"}""");
        store.Upsert("null", """{"items":null}""");
        store.Upsert("empty", """{"items":[]}""");
        store.Upsert("scalar", """{"items":"x"}""");

        var result = store.Aggregate(new DocumentAggregationPipeline(
        [
            new DocumentUnwindStage(
                DocumentFieldRef.JsonPath("$.items"),
                PreserveNullAndEmptyArrays: true,
                IncludeArrayIndex: "itemIndex"),
        ]));

        Assert.Equal(6, result.Documents.Count);
        var documents = result.Documents.Select(static json => JsonDocument.Parse(json)).ToArray();
        try
        {
            Assert.Equal([0, 1], documents
                .Where(static document => document.RootElement.GetProperty("itemIndex").ValueKind == JsonValueKind.Number)
                .Select(static document => document.RootElement.GetProperty("itemIndex").GetInt32())
                .Order()
                .ToArray());
            Assert.Equal(4, documents.Count(static document =>
                document.RootElement.GetProperty("itemIndex").ValueKind == JsonValueKind.Null));
        }
        finally
        {
            foreach (var document in documents)
                document.Dispose();
        }
    }

    private static DocumentAggregationFieldExpression Field(string path)
        => new(DocumentFieldRef.JsonPath(path));

    private static DocumentCollectionStore Create(Tsdb db)
    {
        db.Documents.Create(DocumentCollectionSchema.Create("docs"));
        return db.Documents.Open("docs");
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
