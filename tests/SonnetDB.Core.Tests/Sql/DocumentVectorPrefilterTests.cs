using System.Text;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class DocumentVectorPrefilterTests : IDisposable
{
    private const int DocumentCount = 128;
    private readonly string _root = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(), "sndb-vector-prefilter", Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData("site = 'north'")]
    [InlineData("NOT (site = 'north')")]
    [InlineData("site = 'north' OR site IS NULL")]
    [InlineData("json_value(document, '$.site') = 'north'")]
    [InlineData("site = NULL")]
    [InlineData("id = 'doc-000'")]
    [InlineData("(site = 'north' AND rank >= 2) OR site IS NULL")]
    public void VectorSearch_MetadataPredicate_MatchesResidualOracleWithFewerDistances(string predicate)
    {
        using var db = OpenFixture();
        var expected = RunCounted(db, $"vector_distance() >= 0 AND ({predicate})");
        var actual = RunCounted(db, predicate);

        AssertRowsEqual(expected.Result, actual.Result);
        Assert.Equal(DocumentCount, expected.Distances);
        Assert.InRange(actual.Distances, 1, DocumentCount - 1);
    }

    [Theory]
    [InlineData("ORDER BY distance DESC LIMIT 7 OFFSET 1")]
    [InlineData("ORDER BY rank DESC LIMIT 7 OFFSET 1")]
    [InlineData("LIMIT 7 OFFSET 1")]
    public void VectorSearch_MetadataPredicate_PreservesOrderingKAndPagination(string suffix)
    {
        using var db = OpenFixture();
        var expected = RunCounted(db, "vector_distance() >= 0 AND site = 'north'", suffix);
        var actual = RunCounted(db, "site = 'north'", suffix);

        AssertRowsEqual(expected.Result, actual.Result);
        Assert.Equal(7, actual.Result.Rows.Count);
        Assert.Equal(32, actual.Distances);
    }

    [Theory]
    [InlineData("site = 'north' AND vector_distance() < 2")]
    [InlineData("site = 'north' OR vector_score() > 0.7")]
    [InlineData("vector_distance < 2")]
    [InlineData("vector_score > 0.5")]
    [InlineData("lower(site) = 'north'")]
    public void VectorSearch_DistanceScoreOrGeneralFunction_KeepsResidualEvaluation(string predicate)
    {
        using var db = OpenFixture();
        var expected = RunCounted(db, $"vector_distance() >= 0 AND ({predicate})");
        var actual = RunCounted(db, predicate);

        AssertRowsEqual(expected.Result, actual.Result);
        Assert.Equal(DocumentCount, actual.Distances);
    }

    [Fact]
    public void VectorSearch_MetadataRejectsAll_ComputesNoDistances()
    {
        using var db = OpenFixture();

        var actual = RunCounted(db, "site = 'absent'");

        Assert.Empty(actual.Result.Rows);
        Assert.Equal(0, actual.Distances);
    }

    [Fact]
    public void VectorSearch_FilteredInvalidVector_StillRejectsDimensionMismatch()
    {
        using var db = OpenFixture();
        db.Documents.Open("vectors").Insert("invalid", """{"site":"south","embedding":[1,0]}""");

        var expected = Assert.Throws<InvalidOperationException>(() =>
            RunCounted(db, "vector_distance() >= 0 AND site = 'north'"));
        var actual = Assert.Throws<InvalidOperationException>(() => RunCounted(db, "site = 'north'"));

        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public void VectorSearch_MissingAndNullVectors_RemainExcludedBeforePredicateEvaluation()
    {
        using var db = OpenFixture();
        DocumentCollectionStore store = db.Documents.Open("vectors");
        store.Insert("missing", """{"site":"north"}""");
        store.Insert("null", """{"site":"north","embedding":null}""");

        var expected = RunCounted(db, "vector_distance() >= 0 AND site = 'north'");
        var actual = RunCounted(db, "site = 'north'");

        AssertRowsEqual(expected.Result, actual.Result);
        Assert.Equal(32, actual.Distances);
    }

    [Fact]
    public void VectorSearch_CancelledDuringDistanceLoop_StopsBeforeNextDistance()
    {
        using var db = OpenFixture();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int distances = 0;
        Action? previous = DocumentVectorSearchExecutor.DistanceComputedTestHook;
        DocumentVectorSearchExecutor.DistanceComputedTestHook = () =>
        {
            if (++distances == 2)
                cancellation.Cancel();
        };
        try
        {
            Assert.Throws<OperationCanceledException>(() => SqlExecutor.ExecuteStatement(
                db,
                databaseName: null,
                SqlParser.Parse(Query("site = 'north'", "")),
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { CancellationToken = cancellation.Token }));
            Assert.Equal(2, distances);
        }
        finally
        {
            DocumentVectorSearchExecutor.DistanceComputedTestHook = previous;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Tsdb OpenFixture()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        try
        {
            SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION vectors");
            var random = new Random(618);
            var documents = new DocumentWriteRequest[DocumentCount];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            for (int i = 0; i < documents.Length; i++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                documents[i] = new DocumentWriteRequest($"doc-{i:D3}", CreateDocument(i, random));
            }
            db.Documents.Open("vectors").InsertMany(documents);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static string CreateDocument(int index, Random random)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            if (index % 4 == 0)
                writer.WriteString("site", "north");
            else if (index % 4 == 1)
                writer.WriteString("site", "south");
            else if (index % 4 == 2)
                writer.WriteNull("site");
            writer.WriteNumber("rank", random.Next(5));
            writer.WriteStartArray("embedding");
            writer.WriteNumberValue(index % 8 == 0 ? 1f : (float)(random.NextDouble() * 4 - 2));
            writer.WriteNumberValue(index % 8 == 0 ? 0f : (float)(random.NextDouble() * 4 - 2));
            writer.WriteNumberValue(index % 8 == 0 ? 0f : (float)(random.NextDouble() * 4 - 2));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static (SelectExecutionResult Result, int Distances) RunCounted(
        Tsdb db,
        string predicate,
        string suffix = "ORDER BY distance LIMIT 5 OFFSET 2")
    {
        int distances = 0;
        Action? previous = DocumentVectorSearchExecutor.DistanceComputedTestHook;
        DocumentVectorSearchExecutor.DistanceComputedTestHook = () => distances++;
        try
        {
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, Query(predicate, suffix)));
            return (result, distances);
        }
        finally
        {
            DocumentVectorSearchExecutor.DistanceComputedTestHook = previous;
        }
    }

    private static string Query(string predicate, string suffix)
        => $"SELECT id, site, vector_distance() AS distance, vector_score() AS score "
            + "FROM vector_search(source => vectors, vector_field => '$.embedding', "
            + $"vector => [1, 0, 0], k => 17, metric => 'l2') WHERE {predicate} {suffix}";

    private static void AssertRowsEqual(SelectExecutionResult expected, SelectExecutionResult actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (int i = 0; i < expected.Rows.Count; i++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            Assert.Equal(expected.Rows[i], actual.Rows[i]);
        }
    }
}
