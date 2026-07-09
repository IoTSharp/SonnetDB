using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

/// <summary>
/// <see cref="SndbConnectionStringBuilder"/> 的连接串解析测试：覆盖 PostgreSQL 风格键
/// （Host/Port/Username/Password/Ssl）与旧 Data Source/Token 格式的兼容回归。
/// </summary>
public sealed class SndbConnectionStringBuilderTests
{
    [Fact]
    public void PostgresStyle_Host_ResolvesRemoteBaseUrlAndDatabase()
    {
        var b = new SndbConnectionStringBuilder("Host=127.0.0.1;Port=5080;Database=TOLNSD;Username=tolnsd;Password=secret");

        Assert.Equal(SndbProviderMode.Remote, b.ResolveMode());
        Assert.Equal("http://127.0.0.1:5080/", b.ResolveBaseUrl());
        Assert.Equal("TOLNSD", b.ResolveDatabase());
        Assert.Equal("tolnsd", b.Username);
        Assert.Equal("secret", b.Password);
    }

    [Fact]
    public void PostgresStyle_DefaultPort_IsApplied()
    {
        var b = new SndbConnectionStringBuilder("Host=sonnetdb;Database=db;Username=u;Password=p");
        Assert.Equal(SndbConnectionStringBuilder.DefaultPort, b.Port);
        Assert.Equal("http://sonnetdb:5080/", b.ResolveBaseUrl());
    }

    [Fact]
    public void PostgresStyle_Ssl_UsesHttps()
    {
        var b = new SndbConnectionStringBuilder("Host=sonnetdb;Port=443;Database=db;Username=u;Password=p;Ssl=true");
        Assert.Equal("https://sonnetdb:443/", b.ResolveBaseUrl());
    }

    [Theory]
    [InlineData("User ID")]
    [InlineData("UserId")]
    [InlineData("Uid")]
    public void Username_Aliases_AreRecognized(string key)
    {
        var b = new SndbConnectionStringBuilder($"Host=h;Database=db;{key}=alice;Pwd=pw");
        Assert.Equal("alice", b.Username);
        Assert.Equal("pw", b.Password);
        Assert.Equal(SndbProviderMode.Remote, b.ResolveMode());
    }

    [Fact]
    public void LegacyFormat_DataSourceAndToken_StillResolves()
    {
        var b = new SndbConnectionStringBuilder("Data Source=sonnetdb+http://127.0.0.1:5080/TOLNSD;Token=abc123");

        Assert.Equal(SndbProviderMode.Remote, b.ResolveMode());
        Assert.Equal("http://127.0.0.1:5080/", b.ResolveBaseUrl());
        Assert.Equal("TOLNSD", b.ResolveDatabase());
        Assert.Equal("abc123", b.Token);
        Assert.Null(b.Username);
    }

    [Fact]
    public void LegacyFormat_PlainHttpScheme_StillResolvesRemote()
    {
        var b = new SndbConnectionStringBuilder("Data Source=http://host:5080/mydb");
        Assert.Equal(SndbProviderMode.Remote, b.ResolveMode());
        Assert.Equal("http://host:5080/", b.ResolveBaseUrl());
        Assert.Equal("mydb", b.ResolveDatabase());
    }

    [Fact]
    public void Embedded_LocalPath_ResolvesEmbedded()
    {
        var b = new SndbConnectionStringBuilder("Data Source=./data");
        Assert.Equal(SndbProviderMode.Embedded, b.ResolveMode());
    }

    [Fact]
    public void Embedded_MemoryMappedSegmentOptions_ParseAndCreateOptions()
    {
        var b = new SndbConnectionStringBuilder(
            "Data Source=sonnetdb://./data;Use Memory Mapped Segments=false;Memory Mapped Segment Threshold=32MB");

        Assert.False(b.UseMemoryMappedSegments);
        Assert.Equal(32L * 1024L * 1024L, b.MemoryMappedSegmentThresholdBytes);

        var options = b.CreateEmbeddedOptions(b.ResolveEmbeddedDataSource());
        Assert.Equal("./data", options.RootDirectory);
        Assert.False(options.SegmentReaderOptions.UseMemoryMappedFileForLargeSegments);
        Assert.Equal(32L * 1024L * 1024L, options.SegmentReaderOptions.MemoryMappedFileThresholdBytes);
    }

    [Fact]
    public void ExplicitDatabaseKey_OverridesUrlPath()
    {
        var b = new SndbConnectionStringBuilder("Data Source=sonnetdb+http://host:5080/fromurl;Database=explicit");
        Assert.Equal("explicit", b.ResolveDatabase());
    }
}
