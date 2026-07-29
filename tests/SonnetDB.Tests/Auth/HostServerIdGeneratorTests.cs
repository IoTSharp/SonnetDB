using SonnetDB.Auth;
using Xunit;

namespace SonnetDB.Tests.Auth;

/// <summary>
/// 默认服务器 ID 生成测试。
/// </summary>
public sealed class HostServerIdGeneratorTests
{
    [Fact]
    public void CreateSuggestedServerId_WithSameFingerprint_ReturnsStableNormalizedId()
    {
        var firstParts = new Dictionary<string, string?>
        {
            ["board.product"] = "Test Board 9000",
            ["cpu.name"] = "Test CPU",
            ["machine.id"] = "private-machine-identifier",
        };
        var reorderedParts = firstParts.Reverse();

        var first = HostServerIdGenerator.CreateSuggestedServerId(" Build Server_01.example ", firstParts);
        var second = HostServerIdGenerator.CreateSuggestedServerId(" Build Server_01.example ", reorderedParts);

        Assert.Equal(first, second);
        Assert.StartsWith("sndb-build-server-01-example-", first);
        Assert.Matches("^[a-z0-9-]{3,64}$", first);
    }

    [Fact]
    public void CreateSuggestedServerId_WithDifferentHardwareFingerprint_ReturnsDifferentId()
    {
        var first = HostServerIdGenerator.CreateSuggestedServerId(
            "database-host",
            [new("board.serial", "board-a")]);
        var second = HostServerIdGenerator.CreateSuggestedServerId(
            "database-host",
            [new("board.serial", "board-b")]);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateSuggestedServerId_WithSensitiveFingerprint_DoesNotExposeRawIdentifier()
    {
        const string rawIdentifier = "PRIVATE-HARDWARE-ID-123456";

        var serverId = HostServerIdGenerator.CreateSuggestedServerId(
            new string('A', 100),
            [new("system.uuid", rawIdentifier)]);

        Assert.DoesNotContain(rawIdentifier, serverId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, serverId.Length);
        Assert.Matches("^[a-z0-9-]{3,64}$", serverId);
    }
}
