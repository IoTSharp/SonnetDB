using System.Formats.Tar;
using System.Reflection;
using DotNet.Testcontainers.Images;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SonnetDB.Accuracy.Tests;

public sealed class DockerBuildContextTests
{
    [Fact]
    public async Task TestcontainersDockerfileArchive_WithRepositoryContext_ExcludesBuildOutputs()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dockerfileDirectory = Path.Combine(repositoryRoot, "src", "SonnetDB");
        var dockerignore = Path.Combine(dockerfileDirectory, "Dockerfile.dockerignore");

        Assert.True(File.Exists(dockerignore), $"缺少 Testcontainers 实际读取的忽略文件：{dockerignore}");

        // 直接调用当前 Testcontainers 的归档器，避免自定义匹配器掩盖其 ignore 文件定位或语义变化。
        var image = new DockerImage(
            $"localhost/sonnetdb-context-verification:{Guid.NewGuid():N}");
        var archiveType = typeof(DockerImage).Assembly.GetType(
            "DotNet.Testcontainers.Images.DockerfileArchive",
            throwOnError: true)!;
        var archive = Activator.CreateInstance(
            archiveType,
            repositoryRoot,
            dockerfileDirectory,
            "Dockerfile",
            image,
            new Dictionary<string, string>(),
            NullLogger.Instance)
            ?? throw new InvalidOperationException("无法创建 Testcontainers DockerfileArchive。");
        var tarMethod = archiveType.GetMethod(
            "Tar",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(archiveType.FullName, "Tar");

        var tarPath = await (Task<string>)tarMethod.Invoke(archive, [CancellationToken.None])!;

        try
        {
            var entries = ReadEntryNames(tarPath);

            Assert.Contains("Dockerfile", entries);
            Assert.Contains("Directory.Build.props", entries);
            Assert.Contains("src/SonnetDB/SonnetDB.csproj", entries);
            Assert.DoesNotContain(entries, IsBuildOutput);
        }
        finally
        {
            File.Delete(tarPath);
        }
    }

    private static string FindRepositoryRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SONNETDB_SOURCE_ROOT");
        if (IsRepositoryRoot(configuredRoot))
            return Path.GetFullPath(configuredRoot!);

        foreach (var startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
            {
                if (IsRepositoryRoot(directory.FullName))
                    return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "无法定位 SonnetDB 源码根目录；可通过 SONNETDB_SOURCE_ROOT 显式指定。");
    }

    private static bool IsRepositoryRoot(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && File.Exists(Path.Combine(path, "src", "SonnetDB", "Dockerfile"))
            && File.Exists(Path.Combine(path, "Directory.Build.props"));

    private static IReadOnlyList<string> ReadEntryNames(string tarPath)
    {
        var entries = new List<string>();
        using var stream = File.OpenRead(tarPath);
        using var reader = new TarReader(stream);

        while (reader.GetNextEntry() is { } entry)
            entries.Add(entry.Name.Replace('\\', '/'));

        return entries;
    }

    private static bool IsBuildOutput(string path)
    {
        var normalized = $"/{path.Replace('\\', '/').Trim('/')}/";
        string[] excludedSegments =
        [
            "/.codex-artifacts/",
            "/artifacts/",
            "/BenchmarkDotNet.Artifacts/",
            "/bin/",
            "/node_modules/",
            "/obj/",
            "/output/",
            "/test-results/",
            "/TestResults/",
        ];

        return excludedSegments.Any(segment =>
            normalized.Contains(segment, StringComparison.OrdinalIgnoreCase));
    }
}
