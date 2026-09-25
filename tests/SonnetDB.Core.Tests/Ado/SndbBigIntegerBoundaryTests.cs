using System.Numerics;
using SonnetDB.Data;
using SonnetDB.Exceptions;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

/// <summary>GH-Issue #198：ADO 参数在绑定前拒绝 BigInteger。</summary>
public sealed class SndbBigIntegerBoundaryTests
{
    [Theory]
    [InlineData("-9223372036854775809")]
    [InlineData("-9223372036854775808")]
    [InlineData("0")]
    [InlineData("9223372036854775807")]
    [InlineData("9223372036854775808")]
    public void AddWithValue_BigInteger_RejectsBeforeBindingWithStableCode(string text)
    {
        var parameters = new SndbParameterCollection();
        var value = BigInteger.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var error = Assert.Throws<SndbParameterTypeException>(() => parameters.AddWithValue("@value", value));
        Assert.Equal(SndbParameterTypeException.BigIntegerUnsupportedCode, error.Code);
        Assert.Empty(parameters);
    }

    [Fact]
    public void Value_SetToBigInteger_RejectsAndPreservesPreviousValue()
    {
        var parameter = new SndbParameter("@value", 42L);
        var error = Assert.Throws<SndbParameterTypeException>(() => parameter.Value = BigInteger.One);
        Assert.Equal(SndbParameterTypeException.BigIntegerUnsupportedCode, error.Code);
        Assert.Equal(42L, parameter.Value);
    }

    [Fact]
    public void Value_ExplicitLongOrStringConversion_RetainsExactValue()
    {
        var inRange = BigInteger.Parse("9007199254740993", System.Globalization.CultureInfo.InvariantCulture);
        var outsideRange = BigInteger.Parse("9223372036854775808", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(9007199254740993L, new SndbParameter("@value", checked((long)inRange)).Value);
        Assert.Equal("9223372036854775808", new SndbParameter("@value", outsideRange.ToString(System.Globalization.CultureInfo.InvariantCulture)).Value);
    }
}
