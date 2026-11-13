using System.Text.Json.Serialization;
using SonnetDB.Exceptions;
using SonnetDB.FullText;

namespace SonnetDB.Core.Tests.Documents;

public sealed class FullTextSearchBudgetTests
{
    [Fact]
    public void SortUsesTheRemainingBudgetAndDoesNotReturnAPartialArray()
    {
        using var budget = new FullTextSearchBudget(new FullTextSearchBudgetOptions { MaxWorkUnits = 8 });
        budget.ChargeWork(3);
        Assert.Throws<FullTextSearchBudgetExceededException>(() => budget.Sort(new[] { 4, 3, 2, 1 }, static (left, right) => left.CompareTo(right)));
        Assert.InRange(budget.WorkUnits, 3, 8);
    }

    [Fact]
    public void SortHasStableTieOrderingAndRejectsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new FullTextSearchBudget(cancellationToken: cancellation.Token);
        var actual = budget.Sort(new[] { (Key: 2, Value: "a"), (Key: 1, Value: "b"), (Key: 2, Value: "c") },
            static (left, right) => left.Key.CompareTo(right.Key));
        Assert.Equal(new[] { "b", "a", "c" }, actual.Select(static item => item.Value));
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => budget.Sort(new[] { 2, 1 }, static (left, right) => left.CompareTo(right)));
    }

    [Fact]
    public void SortObservesCancellationRaisedInsideTheComparisonLoop()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new FullTextSearchBudget(cancellationToken: cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => budget.Sort(
            Enumerable.Range(0, 64).Reverse().ToArray(),
            (left, right) =>
            {
                cancellation.Cancel();
                return left.CompareTo(right);
            }));
    }

    [Fact]
    public void SerializeEnforcesTotalUtf8BytesIncludingEscapesAndMetadata()
    {
        const string value = "汉字\"\\\n";
        using var normal = new FullTextSearchBudget();
        byte[] expected = normal.Serialize(value, FullTextBudgetTestJsonContext.Default.String);
        Assert.Equal(expected.Length, normal.ResponseBytes);
        Assert.True(expected.Length > value.Length);
        using var exact = new FullTextSearchBudget(new FullTextSearchBudgetOptions { MaxResponseBytes = expected.Length });
        Assert.Equal(expected, exact.Serialize(value, FullTextBudgetTestJsonContext.Default.String));
        using var below = new FullTextSearchBudget(new FullTextSearchBudgetOptions { MaxResponseBytes = expected.Length - 1 });
        Assert.Throws<FullTextSearchBudgetExceededException>(() => below.Serialize(value, FullTextBudgetTestJsonContext.Default.String));
    }

    [Fact]
    public async Task DeadlineCoversTimeBetweenSearchAndResponseWork()
    {
        using var budget = new FullTextSearchBudget(new FullTextSearchBudgetOptions { TimeoutMilliseconds = 10 });
        budget.ChargeWork();
        await Task.Delay(30);
        Assert.Throws<FullTextSearchBudgetExceededException>(() => budget.Serialize("result", FullTextBudgetTestJsonContext.Default.String));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidLimitsFailBeforeQueryWork(int limit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new FullTextSearchBudget(new FullTextSearchBudgetOptions { MaxWorkUnits = limit }));

    [Fact]
    public void RequestedLimitsCanOnlyLowerTheHardCeilingAndCountersCannotOverflow()
    {
        using var budget = new FullTextSearchBudget(new FullTextSearchBudgetOptions
        {
            MaxResponseBytes = long.MaxValue,
            MaxWorkUnits = long.MaxValue,
            MaxPostingVisits = long.MaxValue,
        });
        Assert.Equal(FullTextSearchBudgetOptions.DefaultMaxResponseBytes, budget.MaxResponseBytes);
        Assert.Equal(FullTextSearchBudgetOptions.DefaultMaxWorkUnits, budget.MaxWorkUnits);
        Assert.Equal(FullTextSearchBudgetOptions.DefaultMaxPostingVisits, budget.MaxPostingVisits);
        budget.ChargeWork();
        Assert.Throws<FullTextSearchBudgetExceededException>(() => budget.ChargeWork(long.MaxValue));
        budget.ChargeResponseBytes(1);
        Assert.Throws<FullTextSearchBudgetExceededException>(() => budget.ChargeResponseBytes(long.MaxValue));
    }

    [Theory]
    [InlineData("汉字\"\\\n")]
    [InlineData("<>&'\u0000\u0001")]
    [InlineData("😀")]
    [InlineData("")]
    public void OutputStringPreflightUsesTheSameEscapedByteBoundary(string value)
    {
        using var normal = new FullTextSearchBudget();
        int encodedLength = normal.Serialize(value, FullTextBudgetTestJsonContext.Default.String).Length;
        using var exact = new FullTextSearchBudget(new FullTextSearchBudgetOptions { MaxResponseBytes = encodedLength });
        exact.CheckOutputString(value);
        Assert.Equal(encodedLength, exact.Serialize(value, FullTextBudgetTestJsonContext.Default.String).Length);
        using var below = new FullTextSearchBudget(new FullTextSearchBudgetOptions { MaxResponseBytes = encodedLength - 1 });
        Assert.Throws<FullTextSearchBudgetExceededException>(() => below.CheckOutputString(value));
    }

    [Fact]
    public void OutputStringPreflightRejectsOneHugeValueAndCumulativeValuesBeforeSerialization()
    {
        using var single = new FullTextSearchBudget(new FullTextSearchBudgetOptions { MaxResponseBytes = 128 });
        Assert.Throws<FullTextSearchBudgetExceededException>(() => single.CheckOutputString(new string('x', 4_096)));
        using var cumulative = new FullTextSearchBudget(new FullTextSearchBudgetOptions { MaxResponseBytes = 32 });
        cumulative.CheckOutputString(new string('a', 20));
        Assert.Throws<FullTextSearchBudgetExceededException>(() => cumulative.CheckOutputString(new string('b', 20)));
    }

    [Fact]
    public void SerializationObservesCancellationRaisedDuringSourceGeneratedProjection()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new FullTextSearchBudget(cancellationToken: cancellation.Token);
        var projection = new CancellingProjection { ReadValue = cancellation.Cancel };
        Assert.Throws<OperationCanceledException>(() => budget.Serialize(projection, FullTextBudgetTestJsonContext.Default.CancellingProjection));
        using var recovered = new FullTextSearchBudget();
        Assert.NotEmpty(recovered.Serialize("recovered", FullTextBudgetTestJsonContext.Default.String));
    }
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(CancellingProjection))]
internal sealed partial class FullTextBudgetTestJsonContext : JsonSerializerContext;

internal sealed class CancellingProjection
{
    [JsonIgnore]
    public Action? ReadValue { get; init; }

    public string Value
    {
        get
        {
            ReadValue?.Invoke();
            return "payload";
        }
    }
}
