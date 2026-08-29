using Xunit;

namespace SonnetDB.Studio.Tests;

public sealed class StudioReleaseContractTests
{
    [Fact]
    public void ReleaseScript_PublishesStudioBundleAndMsiWithExternalUserDataRoot()
    {
        var root = FindRepositoryRoot();
        var releaseScript = File.ReadAllText(Path.Combine(root, "eng", "release.ps1"));
        var readme = File.ReadAllText(Path.Combine(root, "docs", "releases", "installers.md"));

        Assert.Contains("src/SonnetDB.Studio/SonnetDB.Studio.csproj", releaseScript, StringComparison.Ordinal);
        Assert.Contains("--self-contained true", releaseScript, StringComparison.Ordinal);
        Assert.Contains("sonnetdb-studio-$Version-win-x64.msi", releaseScript, StringComparison.Ordinal);
        Assert.Contains("sonnetdb-studio-$Version-$TargetRid", releaseScript, StringComparison.Ordinal);
        Assert.Contains("server/SonnetDB.exe", releaseScript, StringComparison.Ordinal);
        Assert.Contains("%LocalAppData%\\SonnetDB\\Studio\\data", readme, StringComparison.Ordinal);
        Assert.Contains("升级或卸载不会自动删除", readme, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SonnetDB.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("SonnetDB repository root was not found from the test output directory.");
    }
}
