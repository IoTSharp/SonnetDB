using System.Text.Json;
using Xunit;

namespace SonnetDB.Studio.Tests;

public sealed class StudioConnectionLibraryTests
{
    [Fact]
    public async Task LoadAsync_WithoutFile_ReturnsManagedLocalIdentityWithoutClaimingHealth()
    {
        using var fixture = new LibraryFixture();

        var snapshot = await fixture.Library.LoadAsync(CancellationToken.None);

        var profile = Assert.Single(snapshot.Profiles);
        Assert.Equal("managed-local", profile.Id);
        Assert.Equal("current-session", profile.TokenMode);
        Assert.Equal(new StudioConnectionIdentity("studio-desktop", "managed-local", LibraryFixture.ManagedUrl, string.Empty),
            snapshot.ActiveIdentity);
        Assert.False(File.Exists(fixture.FilePath));
    }

    [Fact]
    public async Task SaveAsync_WithDeploymentPathAndMixedCaseIdentity_RoundTripsCanonicalTarget()
    {
        using var fixture = new LibraryFixture();
        var remote = new StudioConnectionProfile("  Remote-Prod  ", "Production", "REMOTE",
            " HTTPS://SERVER.EXAMPLE:443/API/Admin/ ", "Default:MixedCase", "persisted", 1, 2);

        await fixture.Library.SaveAsync(new StudioConnectionLibrarySnapshot([remote], " remote-prod ", "Telemetry:Pump"), CancellationToken.None);
        var snapshot = await fixture.Library.LoadAsync(CancellationToken.None);

        Assert.Equal("Remote-Prod", snapshot.ActiveProfileId);
        Assert.Equal("Telemetry:Pump", snapshot.ActiveDatabase);
        var saved = Assert.Single(snapshot.Profiles, profile => profile.Id == "Remote-Prod");
        Assert.Equal("https://server.example/API/Admin", saved.BaseUrl);
        Assert.Equal("Default:MixedCase", saved.DefaultDatabase);
        Assert.Equal("current-session", saved.TokenMode);
        Assert.Equal(new StudioConnectionIdentity("studio-desktop", "Remote-Prod", saved.BaseUrl, "Telemetry:Pump"),
            snapshot.ActiveIdentity);
        Assert.Equal("Default:MixedCase", saved.Identity.Database);
    }

    [Fact]
    public async Task SaveAsync_WithUnknownActiveProfile_DropsPreviousDatabaseContext()
    {
        using var fixture = new LibraryFixture();
        var remote = RemoteProfile("remote-a", "https://server.example/admin", "RemoteDatabase");

        await fixture.Library.SaveAsync(new StudioConnectionLibrarySnapshot([remote], "deleted-profile", "OldDatabase"), CancellationToken.None);
        var snapshot = await fixture.Library.LoadAsync(CancellationToken.None);

        Assert.Equal("managed-local", snapshot.ActiveProfileId);
        Assert.Empty(snapshot.ActiveDatabase);
        Assert.Equal("managed-local", snapshot.ActiveIdentity?.ProfileId);
    }

    [Fact]
    public async Task SaveAsync_WithRelativeManagedLocalUrl_ResolvesConfiguredHostUrl()
    {
        using var fixture = new LibraryFixture();
        var local = new StudioConnectionProfile("managed-local", "Managed Local", "managed-local", "/", "LocalDatabase", "current-session", 1, 1);

        await fixture.Library.SaveAsync(new StudioConnectionLibrarySnapshot([local], local.Id, local.DefaultDatabase), CancellationToken.None);
        var snapshot = await fixture.Library.LoadAsync(CancellationToken.None);

        Assert.Equal(LibraryFixture.ManagedUrl, Assert.Single(snapshot.Profiles).BaseUrl);
        Assert.Equal("LocalDatabase", snapshot.ActiveIdentity?.Database);
    }

    [Fact]
    public async Task SaveAsync_WithSameDatabaseOnDifferentHosts_PreservesDistinctTargetIdentities()
    {
        using var fixture = new LibraryFixture();
        var first = RemoteProfile("first", "https://first.example/Admin", "MixedCase");
        var second = RemoteProfile("second", "https://second.example/Admin", "MixedCase");

        await fixture.Library.SaveAsync(new StudioConnectionLibrarySnapshot([first, second], "second", "MixedCase"), CancellationToken.None);
        var snapshot = await fixture.Library.LoadAsync(CancellationToken.None);

        Assert.NotEqual(first.Identity, second.Identity);
        Assert.Equal(second.Identity, snapshot.ActiveIdentity);
        Assert.Equal("MixedCase", snapshot.ActiveIdentity?.Database);
    }

    [Fact]
    public async Task SaveAsync_WithManagedLocalIdCaseChange_ReturnsReservedCanonicalId()
    {
        using var fixture = new LibraryFixture();
        var local = new StudioConnectionProfile("MANAGED-LOCAL", "Local", "managed-local", "/", "MixedCase", "current-session", 1, 1);

        await fixture.Library.SaveAsync(new StudioConnectionLibrarySnapshot([local], "MANAGED-LOCAL", "MixedCase"), CancellationToken.None);
        var snapshot = await fixture.Library.LoadAsync(CancellationToken.None);

        Assert.Equal("managed-local", Assert.Single(snapshot.Profiles).Id);
        Assert.Equal("managed-local", snapshot.ActiveProfileId);
        Assert.Equal("managed-local", snapshot.ActiveIdentity?.ProfileId);
    }

    [Fact]
    public async Task SaveAsync_WithRemoteUsingManagedLocalId_RejectsReservedProfileReplacement()
    {
        using var fixture = new LibraryFixture();
        var remote = RemoteProfile("managed-local", "https://server.example", "RemoteDatabase");

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Library.SaveAsync(
            new StudioConnectionLibrarySnapshot([remote], "managed-local", "RemoteDatabase"), CancellationToken.None));

        Assert.False(File.Exists(fixture.FilePath));
    }

    [Theory]
    [InlineData("file:///C:/data")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://server.example")]
    [InlineData("/admin")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("https://user:secret@server.example/admin")]
    [InlineData("https://server.example/admin?token=secret")]
    [InlineData("https://server.example/admin#token=secret")]
    public async Task SaveAsync_WithInvalidRemoteUrl_RejectsWithoutOverwritingLibrary(string url)
    {
        using var fixture = new LibraryFixture();
        var original = new StudioConnectionLibrarySnapshot([RemoteProfile("existing", "https://server.example/admin", "Original")], "existing", "Original");
        await fixture.Library.SaveAsync(original, CancellationToken.None);
        var before = await File.ReadAllTextAsync(fixture.FilePath);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Library.SaveAsync(
            new StudioConnectionLibrarySnapshot([RemoteProfile("invalid", url, "Other")], "invalid", "Other"), CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(fixture.FilePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_WithUnknownCredentialFields_PersistsOnlySessionCredentialPolicy()
    {
        using var fixture = new LibraryFixture();
        const string payload = """
            {"profiles":[{"id":"remote","name":"Remote","kind":"remote","baseUrl":"https://server.example/admin","defaultDatabase":"MixedCase","tokenMode":"disk","createdAt":1,"updatedAt":1,"token":"private-secret","password":"private-password","identity":{"host":"forged-host","database":"ForgedDatabase"}}],"activeProfileId":"remote","activeDatabase":"MixedCase","token":"snapshot-secret","activeIdentity":{"host":"forged-host"}}
            """;
        var request = JsonSerializer.Deserialize(payload, StudioBridgeJsonContext.Default.StudioConnectionLibrarySnapshot)!;

        await fixture.Library.SaveAsync(request, CancellationToken.None);
        var savedJson = await File.ReadAllTextAsync(fixture.FilePath);
        var snapshot = await fixture.Library.LoadAsync(CancellationToken.None);

        Assert.DoesNotContain("private-secret", savedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-password", savedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot-secret", savedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("forged-host", savedJson, StringComparison.Ordinal);
        Assert.Equal("current-session", Assert.Single(snapshot.Profiles, profile => profile.Id == "remote").TokenMode);
        Assert.Equal("studio-desktop", snapshot.ActiveIdentity?.Host);
        Assert.Equal("MixedCase", snapshot.ActiveIdentity?.Database);
    }

    [Fact]
    public async Task LoadAsync_WithInvalidPersistedUrl_ReturnsSafeDefaultWithoutRewritingFile()
    {
        using var fixture = new LibraryFixture();
        const string payload = """
            {"profiles":[{"id":"remote","name":"Remote","kind":"remote","baseUrl":"https://user:secret@server.example","defaultDatabase":"Private","tokenMode":"current-session","createdAt":1,"updatedAt":1}],"activeProfileId":"remote","activeDatabase":"Private"}
            """;
        await File.WriteAllTextAsync(fixture.FilePath, payload);

        var snapshot = await fixture.Library.LoadAsync(CancellationToken.None);

        Assert.Equal("managed-local", Assert.Single(snapshot.Profiles).Id);
        Assert.Empty(snapshot.ActiveDatabase);
        Assert.Equal(payload, await File.ReadAllTextAsync(fixture.FilePath));
    }

    [Fact]
    public async Task SaveAsync_WhenAtomicMoveFails_RemovesOnlyItsTemporaryFile()
    {
        using var fixture = new LibraryFixture();
        Directory.CreateDirectory(fixture.FilePath);
        var unrelatedPath = Path.Combine(fixture.DirectoryPath, "unrelated.tmp");
        await File.WriteAllTextAsync(unrelatedPath, "keep");
        var snapshot = new StudioConnectionLibrarySnapshot([RemoteProfile("remote", "https://server.example/admin", "Db")], "remote", "Db");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Library.SaveAsync(snapshot, CancellationToken.None));

        Assert.Equal([unrelatedPath], Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelatedPath));
    }

    private static StudioConnectionProfile RemoteProfile(string id, string baseUrl, string database)
        => new(id, "Remote", "remote", baseUrl, database, "current-session", 1, 1);

    private sealed class LibraryFixture : IDisposable
    {
        public const string ManagedUrl = "http://127.0.0.1:5080";

        public LibraryFixture()
        {
            DirectoryPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sonnetdb-studio-connection-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(DirectoryPath);
            FilePath = Path.Combine(DirectoryPath, "connections.json");
            Library = new StudioConnectionLibrary(FilePath, ManagedUrl);
        }

        public string DirectoryPath { get; }

        public string FilePath { get; }

        public StudioConnectionLibrary Library { get; }

        public void Dispose()
        {
            var expectedParent = Path.GetFullPath(Path.GetTempPath());
            Assert.Equal(Path.TrimEndingDirectorySeparator(expectedParent), Path.GetDirectoryName(DirectoryPath), StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith("sonnetdb-studio-connection-", Path.GetFileName(DirectoryPath), StringComparison.Ordinal);
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
