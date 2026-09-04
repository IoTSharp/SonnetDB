using System.Diagnostics;

namespace SonnetDB.Benchmarks.Benchmarks;

internal static class GraphEvidenceOwnedDirectoryCleanup
{
    internal const int MaximumEntries = 10_000;
    internal const int MaximumAttempts = 3;
    private const int ProgressEntryInterval = 1_000;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    internal static bool IsOwnedDirectory(
        string directoryPath,
        string expectedParentDirectory,
        string expectedNamePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedParentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedNamePrefix);

        string fullDirectoryPath = NormalizeDirectoryPath(directoryPath);
        string fullParentPath = NormalizeDirectoryPath(expectedParentDirectory);
        string name = Path.GetFileName(fullDirectoryPath);
        return string.Equals(Path.GetDirectoryName(fullDirectoryPath), fullParentPath, PathComparison)
            && name.StartsWith(expectedNamePrefix, StringComparison.Ordinal)
            && name.Length == expectedNamePrefix.Length + 32
            && Guid.TryParseExact(name[expectedNamePrefix.Length..], "N", out _);
    }

    internal static bool TryValidateRoot(
        string directoryPath,
        string expectedParentDirectory,
        string expectedNamePrefix,
        out bool exists,
        out string failureReason)
    {
        exists = false;
        try
        {
            string fullDirectoryPath = NormalizeDirectoryPath(directoryPath);
            if (!IsOwnedDirectory(fullDirectoryPath, expectedParentDirectory, expectedNamePrefix))
            {
                failureReason = "ownership";
                return false;
            }

            if (!TryGetAttributes(fullDirectoryPath, out FileAttributes attributes))
            {
                failureReason = string.Empty;
                return true;
            }

            exists = true;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                failureReason = "root-reparse-point";
                return false;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                failureReason = "root-not-directory";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            failureReason = "root-validation-failed";
            return false;
        }
    }

    internal static bool TryDelete(
        string directoryPath,
        string expectedParentDirectory,
        string expectedNamePrefix,
        out string failureReason,
        CancellationToken cancellationToken = default)
    {
        string fullDirectoryPath;
        try
        {
            fullDirectoryPath = NormalizeDirectoryPath(directoryPath);
            if (!IsOwnedDirectory(fullDirectoryPath, expectedParentDirectory, expectedNamePrefix))
            {
                failureReason = "ownership";
                return false;
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            failureReason = "ownership";
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        int visitedEntries = 0;
        string? lastError = null;
        for (int attempt = 1;
            attempt <= MaximumAttempts && stopwatch.Elapsed < Timeout;
            attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                failureReason = "cancelled";
                return false;
            }

            try
            {
                if (!TryGetAttributes(fullDirectoryPath, out FileAttributes rootAttributes))
                {
                    failureReason = string.Empty;
                    return true;
                }

                if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    failureReason = "root-reparse-point";
                    return false;
                }

                if ((rootAttributes & FileAttributes.Directory) == 0)
                {
                    failureReason = "root-not-directory";
                    return false;
                }

                if (!TryDeleteOnce(
                    fullDirectoryPath,
                    stopwatch,
                    ref visitedEntries,
                    out failureReason,
                    cancellationToken))
                {
                    return false;
                }

                if (!TryGetAttributes(fullDirectoryPath, out _))
                {
                    failureReason = string.Empty;
                    return true;
                }

                lastError = "directory remained after deletion";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception.Message;
            }

            TimeSpan remaining = Timeout - stopwatch.Elapsed;
            if (attempt >= MaximumAttempts || remaining <= TimeSpan.Zero)
                break;

            TimeSpan backoff = TimeSpan.FromMilliseconds(50 * attempt);
            TimeSpan delay = backoff < remaining ? backoff : remaining;
            if (cancellationToken.WaitHandle.WaitOne(delay))
            {
                failureReason = "cancelled";
                return false;
            }
        }

        failureReason = stopwatch.Elapsed >= Timeout
            ? "cleanup-timeout"
            : $"delete-failed: {lastError ?? "unknown error"}";
        return false;
    }

    private static bool TryDeleteOnce(
        string rootDirectory,
        Stopwatch stopwatch,
        ref int visitedEntries,
        out string failureReason,
        CancellationToken cancellationToken)
    {
        var pendingDirectories = new Stack<string>();
        var directories = new List<string>();
        var leaves = new List<string>();
        pendingDirectories.Push(rootDirectory);

        while (pendingDirectories.TryPop(out string? directory))
        {
            if (!CheckBounds(stopwatch, visitedEntries, cancellationToken, out failureReason))
                return false;
            if (!TryGetAttributes(directory, out FileAttributes directoryAttributes))
                continue;

            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                if (string.Equals(directory, rootDirectory, PathComparison))
                {
                    failureReason = "root-reparse-point";
                    return false;
                }

                leaves.Add(directory);
                continue;
            }

            if ((directoryAttributes & FileAttributes.Directory) == 0)
            {
                if (string.Equals(directory, rootDirectory, PathComparison))
                {
                    failureReason = "root-not-directory";
                    return false;
                }

                leaves.Add(directory);
                continue;
            }

            directories.Add(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                directory,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                visitedEntries++;
                if (!CheckBounds(stopwatch, visitedEntries, cancellationToken, out failureReason))
                    return false;
                if (visitedEntries % ProgressEntryInterval == 0)
                {
                    Console.Error.WriteLine(FormattableString.Invariant(
                        $"m40-owned-directory-cleanup-progress path={rootDirectory} entries={visitedEntries} elapsed_seconds={stopwatch.Elapsed.TotalSeconds:F3}"));
                }
                if (!TryGetAttributes(entry, out FileAttributes attributes))
                    continue;

                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || (attributes & FileAttributes.Directory) == 0)
                {
                    leaves.Add(entry);
                }
                else
                {
                    pendingDirectories.Push(entry);
                }
            }
        }

        foreach (string leaf in leaves)
        {
            if (!CheckBounds(stopwatch, visitedEntries, cancellationToken, out failureReason))
                return false;
            DeleteEntryIfPresent(leaf);
        }

        for (int index = directories.Count - 1; index >= 0; index--)
        {
            if (!CheckBounds(stopwatch, visitedEntries, cancellationToken, out failureReason))
                return false;

            string directory = directories[index];
            if (!TryGetAttributes(directory, out FileAttributes attributes))
                continue;
            if (string.Equals(directory, rootDirectory, PathComparison)
                && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                failureReason = "root-reparse-point";
                return false;
            }
            if (string.Equals(directory, rootDirectory, PathComparison)
                && (attributes & FileAttributes.Directory) == 0)
            {
                failureReason = "root-not-directory";
                return false;
            }

            DeleteEntry(directory, attributes);
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool CheckBounds(
        Stopwatch stopwatch,
        int visitedEntries,
        CancellationToken cancellationToken,
        out string failureReason)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            failureReason = "cancelled";
            return false;
        }

        if (visitedEntries > MaximumEntries)
        {
            failureReason = "entry-bound";
            return false;
        }

        if (stopwatch.Elapsed >= Timeout)
        {
            failureReason = "cleanup-timeout";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static void DeleteEntryIfPresent(string path)
    {
        if (TryGetAttributes(path, out FileAttributes attributes))
            DeleteEntry(path, attributes);
    }

    private static void DeleteEntry(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0
            && (attributes & FileAttributes.ReadOnly) != 0)
        {
            FileAttributes writableAttributes = attributes & ~FileAttributes.ReadOnly;
            File.SetAttributes(
                path,
                writableAttributes == 0 ? FileAttributes.Normal : writableAttributes);
            attributes = writableAttributes;
        }

        if ((attributes & FileAttributes.Directory) != 0)
            Directory.Delete(path, recursive: false);
        else
            File.Delete(path);
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static string NormalizeDirectoryPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
