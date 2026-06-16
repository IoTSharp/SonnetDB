namespace SonnetDB.IoTSharpCompat.Tests;

using Xunit;

/// <summary>
/// 静态兼容矩阵（<see cref="IoTSharpCompatMatrix"/>）的内容一致性测试——
/// 仅验证文档表自身条目齐全，不会运行 SonnetDB、不触达任何后端、
/// 也不证明 SonnetDB 与所列后端真正兼容。计入"通过测试数"时应排除。
/// 通过 <c>Category=Documentation</c> trait 过滤：<c>dotnet test --filter "Category!=Documentation"</c>。
/// </summary>
[Trait("Category", "Documentation")]
public sealed class CompatibilityMatrixBaselineTests
{
    [Fact]
    public void Matrix_WithRequiredDomains_CoversIoTSharpDataSurfaces()
    {
        var domains = IoTSharpCompatMatrix.Domains.Select(static x => x.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Relational", domains);
        Assert.Contains("TimeSeries", domains);
        Assert.Contains("Cache", domains);
        Assert.Contains("ObjectBucket", domains);
        Assert.Contains("VectorSearch", domains);
        Assert.Contains("FullTextSearch", domains);
    }

    [Theory]
    [InlineData("Relational", "PostgreSQL")]
    [InlineData("Relational", "MySQL")]
    [InlineData("Relational", "SQLServer")]
    [InlineData("Relational", "SQLite")]
    [InlineData("Relational", "Oracle")]
    [InlineData("Relational", "Cassandra")]
    [InlineData("Relational", "ClickHouse")]
    [InlineData("TimeSeries", "InfluxDB")]
    [InlineData("TimeSeries", "TimescaleDB")]
    [InlineData("TimeSeries", "Taos")]
    [InlineData("TimeSeries", "IoTDB")]
    [InlineData("TimeSeries", "SonnetDB")]
    [InlineData("Cache", "Redis")]
    [InlineData("Cache", "LiteDB")]
    [InlineData("Cache", "InMemory")]
    [InlineData("ObjectBucket", "BlobStorage")]
    [InlineData("ObjectBucket", "S3")]
    [InlineData("ObjectBucket", "SonnetDB")]
    [InlineData("VectorSearch", "KNN")]
    [InlineData("VectorSearch", "VectorIndex")]
    [InlineData("FullTextSearch", "FullTextIndex")]
    [InlineData("FullTextSearch", "BM25")]
    public void Matrix_WithRequiredBackend_CoversBackend(string domainName, string backendName)
    {
        var domain = Assert.Single(IoTSharpCompatMatrix.Domains, x => x.Name == domainName);

        Assert.Contains(domain.Backends, x => x.Name == backendName);
    }

    [Theory]
    [InlineData("Relational", "schema migration")]
    [InlineData("Relational", "transaction rollback")]
    [InlineData("TimeSeries", "latest telemetry")]
    [InlineData("TimeSeries", "range aggregation")]
    [InlineData("Cache", "ttl expiration")]
    [InlineData("Cache", "concurrent key write")]
    [InlineData("ObjectBucket", "object upload download")]
    [InlineData("ObjectBucket", "multipart upload")]
    [InlineData("ObjectBucket", "bucket lifecycle")]
    [InlineData("ObjectBucket", "object audit")]
    [InlineData("VectorSearch", "topK distance ordering")]
    [InlineData("VectorSearch", "index rebuild")]
    [InlineData("FullTextSearch", "match query")]
    [InlineData("FullTextSearch", "bm25 ordering")]
    public void AcceptanceCases_WithRequiredScenario_CoversScenario(string domainName, string scenario)
    {
        var domain = Assert.Single(IoTSharpCompatMatrix.Domains, x => x.Name == domainName);

        Assert.Contains(domain.AcceptanceCases, x => x.Contains(scenario, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("relational migrate")]
    [InlineData("relational rollback")]
    [InlineData("timeseries dual-write")]
    [InlineData("timeseries rollback")]
    [InlineData("cache cold-start")]
    [InlineData("object metadata verify")]
    [InlineData("object rollback")]
    [InlineData("search index rebuild")]
    public void MigrationChecklist_WithRequiredItem_CoversItem(string item)
    {
        Assert.Contains(
            IoTSharpCompatMatrix.MigrationAndRollbackChecklist,
            x => x.Contains(item, StringComparison.OrdinalIgnoreCase));
    }
}
