using System.Security.Cryptography;
using System.Text;

namespace SonnetDB.Copilot;

/// <summary>
/// 单个文档源文件的规范化描述。
/// </summary>
internal sealed record DocsSourceFile(
    string Source,
    string FullPath,
    string Title,
    DateTimeOffset LastWriteTimeUtc,
    string Fingerprint,
    bool IsPreferredSource);

/// <summary>
/// 扫描 docs/help 文档根目录，并做旧路径兼容与去重。
/// </summary>
internal sealed class DocsSourceScanner
{
    private static readonly string[] LegacyHelpRoots = ["web/admin/help"];
    private static readonly string[] DefaultRootMarkers = ["docs", "web/help", "src/SonnetDB/wwwroot/help"];

    public IReadOnlyList<DocsSourceFile> Scan(IEnumerable<string> configuredRoots)
        => Scan(configuredRoots, CancellationToken.None);

    internal IReadOnlyList<DocsSourceFile> Scan(IEnumerable<string> configuredRoots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuredRoots);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        CancellationToken token = deadline.Token;
        int roots = 0;
        int entries = 0;

        var filesBySource = new Dictionary<string, DocsSourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawRoot in configuredRoots)
        {
            token.ThrowIfCancellationRequested();
            if (++roots > 32) throw new InvalidOperationException("文档扫描最多支持 32 个根目录。");
            foreach (var resolvedRoot in ResolveRootCandidates(rawRoot))
            {
                if (!Directory.Exists(resolvedRoot.Path))
                    continue;

                foreach (var file in EnumerateFiles(resolvedRoot.Path, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (++entries > 10_000) throw new InvalidOperationException("文档扫描超过 10000 个文件。");
                    if (new FileInfo(file).Length > 4 * 1024 * 1024)
                        throw new InvalidOperationException("文档文件超过 4 MiB 扫描预算。");
                    var source = BuildSourceKey(resolvedRoot.Path, file);
                    var candidate = CreateSourceFile(file, source, resolvedRoot.Preference, token);
                    if (filesBySource.TryGetValue(source, out var existing))
                    {
                        if (IsBetter(candidate, existing))
                            filesBySource[source] = candidate;
                    }
                    else
                    {
                        filesBySource[source] = candidate;
                    }
                }

                break;
            }
        }

        return filesBySource.Values
            .OrderBy(static item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<(string Path, int Preference)> ResolveRootCandidates(string rawRoot)
    {
        if (string.IsNullOrWhiteSpace(rawRoot))
            yield break;

        var normalized = NormalizeLegacyRoot(rawRoot.Trim());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var baseDirectory in EnumerateBaseDirectories())
        {
            var candidate = Path.IsPathRooted(normalized)
                ? Path.GetFullPath(normalized)
                : Path.GetFullPath(normalized, baseDirectory);
            if (seen.Add(candidate))
                yield return (candidate, GetPreference(candidate));
        }
    }

    private static IEnumerable<string> EnumerateBaseDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(seed))
                continue;

            var current = Path.GetFullPath(seed);
            for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(current); depth++)
            {
                if (seen.Add(current))
                    yield return current;

                var parent = Directory.GetParent(current)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken token)
    {
        int entries = 0;
        foreach (var file in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = 16, IgnoreInaccessible = false,
            AttributesToSkip = 0,
        }))
        {
            token.ThrowIfCancellationRequested();
            if (++entries > 20_000) throw new InvalidOperationException("文档扫描超过 20000 个目录项。");
            FileAttributes attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("文档快照不接受符号链接或重解析点。");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).Length >= 16)
                    throw new InvalidOperationException("文档目录超过 16 层扫描预算。");
                continue;
            }
            var extension = Path.GetExtension(file);
            if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".htm", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static DocsSourceFile CreateSourceFile(string fullPath, string source, int preference, CancellationToken token)
    {
        var info = new FileInfo(fullPath);
        return new DocsSourceFile(
            Source: source,
            FullPath: info.FullName,
            Title: GuessTitle(source),
            LastWriteTimeUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            Fingerprint: ComputeFingerprint(info.FullName, token),
            IsPreferredSource: preference <= 1 || string.Equals(info.Extension, ".md", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSourceKey(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file)
            .Replace('\\', '/')
            .TrimStart('/');
        var extension = Path.GetExtension(relative);
        if (!string.IsNullOrEmpty(extension))
            relative = relative[..^extension.Length];

        if (relative.EndsWith("/index", StringComparison.OrdinalIgnoreCase))
            relative = relative[..^"/index".Length];

        return string.IsNullOrWhiteSpace(relative) ? "index" : relative;
    }

    private static bool IsBetter(DocsSourceFile candidate, DocsSourceFile existing)
    {
        if (candidate.IsPreferredSource != existing.IsPreferredSource)
            return candidate.IsPreferredSource;

        var candidateIsMarkdown = string.Equals(Path.GetExtension(candidate.FullPath), ".md", StringComparison.OrdinalIgnoreCase);
        var existingIsMarkdown = string.Equals(Path.GetExtension(existing.FullPath), ".md", StringComparison.OrdinalIgnoreCase);
        if (candidateIsMarkdown != existingIsMarkdown)
            return candidateIsMarkdown;

        return candidate.LastWriteTimeUtc > existing.LastWriteTimeUtc;
    }

    private static int GetPreference(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/');
        if (normalized.EndsWith("/docs", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/docs/", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (normalized.EndsWith("/web/help", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/web/help/", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (normalized.EndsWith("/src/SonnetDB/wwwroot/help", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/src/SonnetDB/wwwroot/help/", StringComparison.OrdinalIgnoreCase))
            return 2;
        return Array.Exists(DefaultRootMarkers, marker => normalized.EndsWith(marker.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) ? 3 : 4;
    }

    private static string NormalizeLegacyRoot(string root)
    {
        var normalized = root.Replace('\\', '/');
        foreach (var legacy in LegacyHelpRoots)
        {
            if (string.Equals(normalized, legacy, StringComparison.OrdinalIgnoreCase))
                return "web/help";
        }

        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string GuessTitle(string source)
    {
        var last = source.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(last) || string.Equals(last, "index", StringComparison.OrdinalIgnoreCase))
            return "Help";

        return string.Join(' ', last.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string ComputeFingerprint(string fullPath, CancellationToken token)
    {
        using var stream = File.OpenRead(fullPath);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidOperationException("文档文件超过 4 MiB 扫描预算。");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactlyAsync(bytes, token).AsTask().GetAwaiter().GetResult();
        if (stream.ReadByte() != -1) throw new IOException("扫描期间文档大小发生变化。");
        token.ThrowIfCancellationRequested();
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
