using SonnetDB.Documents;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

/// <summary>验证 JSON 数字在路径求值和装箱时保持原始精度。</summary>
public sealed class JsonPathEvaluatorTests
{
    /// <summary>Int64 边界及 double 精度边界相邻整数始终返回精确 Int64。</summary>
    [Theory]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("9223372036854775684", long.MaxValue - 123)]
    [InlineData("9223372036854775685", long.MaxValue - 122)]
    [InlineData("9007199254740991", 9007199254740991L)]
    [InlineData("9007199254740992", 9007199254740992L)]
    [InlineData("9007199254740993", 9007199254740993L)]
    [InlineData("-9007199254740993", -9007199254740993L)]
    public void Evaluate_Int64Boundaries_PreservesExactBoxedInteger(string number, long expected)
    {
        var json = "{\"value\":" + number + "}";
        var path = JsonPath.Parse("$.value");

        Assert.Equal(expected, Assert.IsType<long>(JsonPathEvaluator.Evaluate(json, "$.value")));
        Assert.Equal(expected, Assert.IsType<long>(JsonPathEvaluator.Evaluate(json, path)));
        Assert.True(JsonPathEvaluator.TryEvaluate(json, "$.value", out var value));
        Assert.Equal(expected, Assert.IsType<long>(value));
        Assert.True(JsonPathEvaluator.TryEvaluate(json, path, out var parsedValue));
        Assert.Equal(expected, Assert.IsType<long>(parsedValue));
    }

    /// <summary>小数和 Int64 范围外的数值继续按 double 返回。</summary>
    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("-1.25", -1.25)]
    [InlineData("1e30", 1e30)]
    public void Evaluate_NonInt64Number_PreservesDouble(string number, double expected)
    {
        var json = "{\"value\":" + number + "}";

        Assert.Equal(expected, Assert.IsType<double>(JsonPathEvaluator.Evaluate(json, "$.value")));
        Assert.True(JsonPathEvaluator.TryEvaluate(json, "$.value", out var value));
        Assert.Equal(expected, Assert.IsType<double>(value));
    }
}
