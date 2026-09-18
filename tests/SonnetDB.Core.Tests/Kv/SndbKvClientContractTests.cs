using SonnetDB.Data.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class SndbKvClientContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-kv-client-contract", Guid.NewGuid().ToString("N"));

    public SndbKvClientContractTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SetManyAsync_WithDuplicateKeys_RejectsBeforeWrite()
    {
        using var client = new SndbKvClient($"Data Source={_root}");

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetManyAsync(
            "cache",
            "tests",
            [new KeyValuePair<string, byte[]>("same", [1]), new KeyValuePair<string, byte[]>("same", [2])]));

        Assert.Null(await client.GetAsync("cache", "tests", "same"));
    }

    [Fact]
    public async Task SetManyAsync_WithNonUtcExpiry_RejectsBeforeWrite()
    {
        using var client = new SndbKvClient($"Data Source={_root}");

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetManyAsync(
            "cache",
            "tests",
            [new KeyValuePair<string, byte[]>("key", [1])],
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.FromHours(8))));

        Assert.Null(await client.GetAsync("cache", "tests", "key"));
    }

    [Fact]
    public async Task ScanPrefixAsync_WithNegativeLimit_RejectsBeforeRead()
    {
        using var client = new SndbKvClient($"Data Source={_root}");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.ScanPrefixAsync("cache", "tests", "", -1));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
