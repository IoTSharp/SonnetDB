using System.Data;
using Xunit;

namespace SonnetDB.Parity.Adapters;

public sealed class RelationalResultMaterializerTests
{
    [Fact]
    public async Task ReadAsync_WithIntegralDecimal_NormalizesToInt64()
    {
        using var table = CreateDecimalTable(120m);
        using var reader = table.CreateDataReader();

        var result = await RelationalResultMaterializer.ReadAsync(reader, CancellationToken.None);

        Assert.Equal(120L, Assert.IsType<long>(result.Rows[0].Values[0]));
    }

    [Fact]
    public async Task ReadAsync_WithFractionalDecimal_NormalizesToDouble()
    {
        using var table = CreateDecimalTable(12.5m);
        using var reader = table.CreateDataReader();

        var result = await RelationalResultMaterializer.ReadAsync(reader, CancellationToken.None);

        Assert.Equal(12.5d, Assert.IsType<double>(result.Rows[0].Values[0]));
    }

    private static DataTable CreateDecimalTable(decimal value)
    {
        var table = new DataTable();
        table.Columns.Add("value", typeof(decimal));
        table.Rows.Add(value);
        return table;
    }
}
