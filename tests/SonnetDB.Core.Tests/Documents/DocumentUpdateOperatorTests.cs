using System.Text.Json;
using SonnetDB.Documents;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentUpdateOperatorTests
{
    [Fact]
    public void DocumentUpdate_LegacyConstructorAndDeconstruct_RemainAvailable()
    {
        var set = Values(("$.name", "\"pump\""));
        var update = new DocumentUpdate(
            set,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var (actualSet, unset, inc, min, max, rename, push, pull, addToSet, currentDate) = update;

        Assert.Same(set, actualSet);
        Assert.Null(unset);
        Assert.Null(inc);
        Assert.Null(min);
        Assert.Null(max);
        Assert.Null(rename);
        Assert.Null(push);
        Assert.Null(pull);
        Assert.Null(addToSet);
        Assert.Null(currentDate);
        Assert.Null(update.Mul);
        Assert.Null(update.Pop);
    }

    [Fact]
    public void Apply_MulExistingAndMissingFields_UpdatesNumbersAndCreatesZero()
    {
        var update = new DocumentUpdate(Mul: Values(
            ("$.metrics.count", "3"),
            ("$.metrics.ratio", "2.5"),
            ("$.metrics.missing", "4")));

        string result = DocumentUpdateExecutor.Apply(
            """{"metrics":{"count":4,"ratio":1.5}}""",
            update);

        using var document = JsonDocument.Parse(result);
        var metrics = document.RootElement.GetProperty("metrics");
        Assert.Equal(12, metrics.GetProperty("count").GetInt32());
        Assert.Equal(3.75d, metrics.GetProperty("ratio").GetDouble());
        Assert.Equal(0, metrics.GetProperty("missing").GetInt32());
    }

    [Fact]
    public void Apply_MulIntegerOverflow_PromotesToFiniteDouble()
    {
        var update = new DocumentUpdate(Mul: Values(("$.value", "2")));

        string result = DocumentUpdateExecutor.Apply(
            $$"""{"value":{{long.MaxValue}}}""",
            update);

        using var document = JsonDocument.Parse(result);
        var value = document.RootElement.GetProperty("value");
        Assert.False(value.TryGetInt64(out _));
        Assert.Equal((double)long.MaxValue * 2d, value.GetDouble());
    }

    [Fact]
    public void Apply_MulNonFiniteResult_Throws()
    {
        var update = new DocumentUpdate(Mul: Values(("$.value", "1e308")));

        var error = Assert.Throws<InvalidOperationException>(() =>
            DocumentUpdateExecutor.Apply("""{"value":1e308}""", update));

        Assert.Contains("有限 double", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_MulNonNumericField_Throws()
    {
        var update = new DocumentUpdate(Mul: Values(("$.value", "2")));

        var error = Assert.Throws<InvalidOperationException>(() =>
            DocumentUpdateExecutor.Apply("""{"value":"two"}""", update));

        Assert.Contains("$mul", error.Message, StringComparison.Ordinal);
        Assert.Contains("数值", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_PopFirstLastEmptyAndMissingFields_UsesMongoDirections()
    {
        var update = new DocumentUpdate(Pop: Values(
            ("$.first", "-1"),
            ("$.last", "1"),
            ("$.empty", "1"),
            ("$.missing", "-1")));

        string result = DocumentUpdateExecutor.Apply(
            """{"first":[1,2,3],"last":[1,2,3],"empty":[]}""",
            update);

        using var document = JsonDocument.Parse(result);
        Assert.Equal("[2,3]", document.RootElement.GetProperty("first").GetRawText());
        Assert.Equal("[1,2]", document.RootElement.GetProperty("last").GetRawText());
        Assert.Equal("[]", document.RootElement.GetProperty("empty").GetRawText());
        Assert.False(document.RootElement.TryGetProperty("missing", out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    public void Apply_PopInvalidDirection_Throws(string operand)
    {
        var update = new DocumentUpdate(Pop: Values(("$.items", operand)));

        var error = Assert.Throws<InvalidOperationException>(() =>
            DocumentUpdateExecutor.Apply("""{"items":[1,2]}""", update));

        Assert.Contains("-1 或 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_PopNonArrayField_Throws()
    {
        var update = new DocumentUpdate(Pop: Values(("$.items", "1")));

        var error = Assert.Throws<InvalidOperationException>(() =>
            DocumentUpdateExecutor.Apply("""{"items":"not-an-array"}""", update));

        Assert.Contains("必须是数组", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_MulAndSetSamePath_RejectsPrefixConflict()
    {
        var update = new DocumentUpdate(
            Set: Values(("$.metrics", "{}")),
            Mul: Values(("$.metrics.count", "2")));

        var error = Assert.Throws<InvalidOperationException>(() =>
            DocumentUpdateExecutor.Apply("""{"metrics":{"count":4}}""", update));

        Assert.Contains("路径冲突", error.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, JsonElement> Values(
        params (string Path, string Json)[] values)
        => values.ToDictionary(
            static value => value.Path,
            static value => ParseElement(value.Json),
            StringComparer.Ordinal);

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
