using SonnetDB.Sql.Ast;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlQualifiedObjectNameTests
{
    [Fact]
    public void Constructor_UnqualifiedName_BindsOnlyToMain()
    {
        var requested = new SqlQualifiedObjectName("Orders");

        Assert.False(requested.HasExplicitSchema);
        Assert.Equal("main", requested.SchemaName);
        Assert.True(requested.Matches(new TableObjectName("main", "orders")));
        Assert.False(requested.Matches(new TableObjectName("sales", "orders")));
        Assert.Equal("Orders", requested.ToString());
    }

    [Theory]
    [InlineData(false, false, "sales", "orders", true)]
    [InlineData(true, false, "sales", "orders", false)]
    [InlineData(false, true, "sales", "orders", false)]
    [InlineData(true, true, "Sales", "Orders", true)]
    public void Matches_AppliesQuoteFlagsPerPart(
        bool schemaQuoted,
        bool nameQuoted,
        string requestedSchema,
        string requestedName,
        bool expected)
    {
        var requested = new SqlQualifiedObjectName(requestedSchema, schemaQuoted, requestedName, nameQuoted);

        Assert.Equal(expected, requested.Matches(new TableObjectName("Sales", "Orders")));
    }

    [Fact]
    public void ToString_QuotedDotAndEscapedQuote_RemainIndependentParts()
    {
        var requested = new SqlQualifiedObjectName("Sales.Region", true, "Order\"Items", true);

        Assert.Equal("\"Sales.Region\".\"Order\"\"Items\"", requested.ToString());
        Assert.Equal("Sales.Region", requested.SchemaName);
        Assert.Equal("Order\"Items", requested.Name);
    }

    [Fact]
    public void Equality_RetainsExplicitSchemaAndQuoteState()
    {
        var unqualified = new SqlQualifiedObjectName("orders");
        var explicitMain = new SqlQualifiedObjectName("main", false, "orders", false);

        Assert.NotEqual(unqualified, explicitMain);
        Assert.Equal(unqualified.ObjectName, explicitMain.ObjectName);
    }
}
