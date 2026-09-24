using System.Text.Json;
using SonnetDB.Data.Internal;
using SonnetDB.Data.Remote;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// GH-Issue #186：VECTOR 参数绑定和远程 JSON 数组解码的确定性合同。
/// </summary>
public sealed class SqlVectorParameterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-vector-param-" + Guid.NewGuid().ToString("N"));

    public SqlVectorParameterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Embedded_VectorParameter_BindsAndRoundTripsFloatArray()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");

        object? insert = SqlExecutor.Execute(
            database,
            databaseName: null,
            "INSERT INTO docs (time, source, embedding) VALUES (1000, @source, @embedding)",
            new SqlParameters()
                .AddNamed("source", "a")
                .AddNamed("embedding", new float[] { 1.25f, -0.5f, 3f }));

        Assert.Equal(1, Assert.IsType<InsertExecutionResult>(insert).RowsInserted);

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "SELECT embedding FROM docs"));
        var vector = Assert.IsType<float[]>(Assert.Single(selected.Rows)[0]);
        Assert.Equal(new float[] { 1.25f, -0.5f, 3f }, vector);
    }

    [Fact]
    public void VectorParameterBinder_SupportsMemoryAndRejectsInvalidVectors()
    {
        var literal = Assert.IsType<VectorLiteralExpression>(SqlParameterBinder.ToLiteral(new ReadOnlyMemory<float>([1f, 2f])));
        Assert.Equal(new double[] { 1d, 2d }, literal.Components);

        Assert.Throws<ArgumentException>(() => SqlParameterBinder.ToLiteral(Array.Empty<float>()));
        Assert.Throws<ArgumentException>(() => SqlParameterBinder.ToLiteral(new float[] { float.NaN }));
        Assert.IsType<LiteralExpression>(SqlParameterBinder.ToLiteral(null));

        Assert.Equal("[1.25, -0.5, 3]", ParameterBinder.FormatLiteral(new float[] { 1.25f, -0.5f, 3f }));
        Assert.Throws<ArgumentException>(() => ParameterBinder.FormatLiteral(new float[] { float.PositiveInfinity }));
    }

    [Fact]
    public void RemoteExecutionResult_DecodesNumericArrayAsFloatArrayAndPreservesOtherArrays()
    {
        using (var document = JsonDocument.Parse("[1.25,-0.5,3]"))
        {
            var vector = Assert.IsType<float[]>(RemoteExecutionResult.ReadScalar(document.RootElement));
            Assert.Equal(new float[] { 1.25f, -0.5f, 3f }, vector);
        }

        using (var document = JsonDocument.Parse("[1,null,3]"))
            Assert.Equal("[1,null,3]", Assert.IsType<string>(RemoteExecutionResult.ReadScalar(document.RootElement)));

        using (var document = JsonDocument.Parse("null"))
            Assert.Null(RemoteExecutionResult.ReadScalar(document.RootElement));
    }
}
