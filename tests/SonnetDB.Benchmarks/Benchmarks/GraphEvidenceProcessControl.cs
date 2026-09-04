using System.Globalization;
using System.Text;

namespace SonnetDB.Benchmarks.Benchmarks;

internal sealed class GraphEvidenceProcessControl : IDisposable
{
    private const string DirectoryPrefix = "sonnetdb-m40-evidence-control-";
    private const string CompletionFileName = "completion.state";
    private const string EnvironmentFileName = "target-environment.state";
    private const string TerminationRequestFileName = "termination.request";
    private const string TerminationAcknowledgementFileName = "termination.ack";
    private const string TerminationRequestDisposition = "terminate-target";
    private const string TerminationAcknowledgementDisposition = "target-terminated";
    private const int EnvironmentFormatMagic = 0x31564E45;
    private const int MaximumControlStateBytes = 512;
    private const int MaximumEnvironmentPayloadBytes = 1024 * 1024;
    private const int MaximumEnvironmentVariableCount = 4_096;
    private const int MaximumEnvironmentKeyCharacters = 32 * 1024;
    private const int MaximumEnvironmentValueCharacters = 128 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _handshakeToken;
    private bool _disposed;

    private GraphEvidenceProcessControl(string directoryPath, string handshakeToken)
    {
        DirectoryPath = directoryPath;
        CompletionPath = Path.Combine(directoryPath, CompletionFileName);
        EnvironmentPath = Path.Combine(directoryPath, EnvironmentFileName);
        TerminationRequestPath = Path.Combine(directoryPath, TerminationRequestFileName);
        TerminationAcknowledgementPath = Path.Combine(
            directoryPath,
            TerminationAcknowledgementFileName);
        _handshakeToken = handshakeToken;
    }

    internal string DirectoryPath { get; }

    internal string CompletionPath { get; }

    internal string EnvironmentPath { get; }

    internal string TerminationRequestPath { get; }

    internal string TerminationAcknowledgementPath { get; }

    internal static GraphEvidenceProcessControl Create(
        string handshakeToken,
        IEnumerable<KeyValuePair<string, string?>>? targetEnvironment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeToken);
        string directoryPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            DirectoryPrefix + Guid.NewGuid().ToString("N")));
        if (!IsOwnedDirectory(directoryPath))
            throw new InvalidOperationException("Evidence process control 目录未通过归属校验。");

        Directory.CreateDirectory(directoryPath);
        var control = new GraphEvidenceProcessControl(directoryPath, handshakeToken);
        try
        {
            control.PublishTargetEnvironment(targetEnvironment ?? []);
            return control;
        }
        catch
        {
            control.Dispose();
            throw;
        }
    }

    internal bool TryReadCompletion(out GraphEvidenceLauncherCompletion completion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        completion = default;
        if (!TryReadAuthenticatedTextState(
                CompletionPath,
                CompletionFileName,
                expectedLineCount: 3,
                out string[] lines))
        {
            return false;
        }

        if (!int.TryParse(
                lines[1],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out int exitCode)
            || (lines[2] != "drained" && lines[2] != "drain-failed"))
        {
            throw new InvalidDataException("Evidence launcher completion 状态无效。");
        }

        completion = new GraphEvidenceLauncherCompletion(
            exitCode,
            OutputDrained: lines[2] == "drained");
        return true;
    }

    internal void PublishTerminationRequest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PublishAuthenticatedTextState(
            TerminationRequestPath,
            TerminationRequestFileName,
            _handshakeToken,
            TerminationRequestDisposition);
    }

    internal bool TryReadTerminationAcknowledgement()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryReadAuthenticatedTextState(
                TerminationAcknowledgementPath,
                TerminationAcknowledgementFileName,
                expectedLineCount: 2,
                out string[] lines))
        {
            return false;
        }

        if (!string.Equals(
                lines[1],
                TerminationAcknowledgementDisposition,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Evidence launcher termination acknowledgement 状态无效。");
        }

        return true;
    }

    internal static IReadOnlyDictionary<string, string?> ReadTargetEnvironment(
        string completionPath,
        string handshakeToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeToken);
        string environmentPath = ResolveSiblingPath(
            completionPath,
            EnvironmentFileName,
            requireDirectory: true);
        byte[] payload = ReadRequiredBoundedFile(
            environmentPath,
            MaximumEnvironmentPayloadBytes,
            "Evidence target environment payload");

        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
            if (reader.ReadInt32() != EnvironmentFormatMagic)
                throw new InvalidDataException("Evidence target environment payload magic 无效。");
            string token = ReadBoundedString(reader, MaximumControlStateBytes);
            if (!string.Equals(token, handshakeToken, StringComparison.Ordinal))
                throw new InvalidDataException("Evidence target environment payload token 无效。");

            int count = reader.ReadInt32();
            if (count is < 0 or > MaximumEnvironmentVariableCount)
                throw new InvalidDataException("Evidence target environment 变量数量无效。");

            var environment = new Dictionary<string, string?>(
                count,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            for (int index = 0; index < count; index++)
            {
                string key = ReadBoundedString(
                    reader,
                    StrictUtf8.GetMaxByteCount(MaximumEnvironmentKeyCharacters));
                int valueLength = reader.ReadInt32();
                string? value = valueLength == -1
                    ? null
                    : ReadBoundedStringBody(
                        reader,
                        valueLength,
                        StrictUtf8.GetMaxByteCount(MaximumEnvironmentValueCharacters));
                ValidateEnvironmentEntry(key, value);
                if (!environment.TryAdd(key, value))
                    throw new InvalidDataException("Evidence target environment 包含重复变量。");
            }

            if (stream.Position != stream.Length)
                throw new InvalidDataException("Evidence target environment payload 含尾随数据。");
            return environment;
        }
        catch (Exception exception) when (exception is EndOfStreamException
            or DecoderFallbackException
            or ArgumentException)
        {
            throw new InvalidDataException("Evidence target environment payload 无效。", exception);
        }
    }

    internal static void TryDeleteTargetEnvironment(string completionPath)
    {
        try
        {
            string environmentPath = ResolveSiblingPath(
                completionPath,
                EnvironmentFileName,
                requireDirectory: true);
            if (File.Exists(environmentPath))
                File.Delete(environmentPath);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            TryWriteRetainedDiagnostic(completionPath);
        }
    }

    internal static bool TryReadTerminationRequest(
        string completionPath,
        string handshakeToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeToken);
        string requestPath = ResolveSiblingPath(
            completionPath,
            TerminationRequestFileName,
            requireDirectory: true);
        if (!TryReadAuthenticatedTextState(
                requestPath,
                TerminationRequestFileName,
                handshakeToken,
                expectedLineCount: 2,
                out string[] lines))
        {
            return false;
        }

        if (!string.Equals(lines[1], TerminationRequestDisposition, StringComparison.Ordinal))
            throw new InvalidDataException("Evidence launcher termination request 状态无效。");
        return true;
    }

    internal static void PublishTerminationAcknowledgement(
        string completionPath,
        string handshakeToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeToken);
        string acknowledgementPath = ResolveSiblingPath(
            completionPath,
            TerminationAcknowledgementFileName,
            requireDirectory: true);
        PublishAuthenticatedTextState(
            acknowledgementPath,
            TerminationAcknowledgementFileName,
            handshakeToken,
            TerminationAcknowledgementDisposition);
    }

    internal static void PublishCompletion(
        string completionPath,
        string handshakeToken,
        int exitCode,
        bool outputDrained)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeToken);
        string fullCompletionPath = ValidateOwnedFilePath(
            completionPath,
            CompletionFileName,
            requireDirectory: true);
        PublishAuthenticatedTextState(
            fullCompletionPath,
            CompletionFileName,
            handshakeToken,
            exitCode.ToString(CultureInfo.InvariantCulture),
            outputDrained ? "drained" : "drain-failed");
    }

    internal static bool IsValidCompletionPath(string completionPath)
    {
        try
        {
            _ = ValidateOwnedFilePath(
                completionPath,
                CompletionFileName,
                requireDirectory: true);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void TryDeleteForAbandonedLauncher(string completionPath)
    {
        try
        {
            string fullCompletionPath = ValidateOwnedFilePath(
                completionPath,
                CompletionFileName,
                requireDirectory: false);
            string directoryPath = Path.GetDirectoryName(fullCompletionPath)!;
            if (!TryDeleteOwnedDirectory(directoryPath, out _))
                TryWriteRetainedDiagnostic(directoryPath);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            TryWriteRetainedDiagnostic(completionPath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!TryDeleteOwnedDirectory(DirectoryPath, out _))
            TryWriteRetainedDiagnostic(DirectoryPath);
    }

    private void PublishTargetEnvironment(IEnumerable<KeyValuePair<string, string?>> environment)
    {
        KeyValuePair<string, string?>[] entries = environment
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        if (entries.Length > MaximumEnvironmentVariableCount)
        {
            throw new ArgumentException(
                $"Evidence target environment 变量不能超过 {MaximumEnvironmentVariableCount} 项。",
                nameof(environment));
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true))
        {
            writer.Write(EnvironmentFormatMagic);
            WriteBoundedString(writer, _handshakeToken, MaximumControlStateBytes);
            writer.Write(entries.Length);
            foreach ((string key, string? value) in entries)
            {
                ValidateEnvironmentEntry(key, value);
                WriteBoundedString(
                    writer,
                    key,
                    StrictUtf8.GetMaxByteCount(MaximumEnvironmentKeyCharacters));
                if (value is null)
                {
                    writer.Write(-1);
                }
                else
                {
                    WriteBoundedString(
                        writer,
                        value,
                        StrictUtf8.GetMaxByteCount(MaximumEnvironmentValueCharacters));
                }

                if (stream.Length > MaximumEnvironmentPayloadBytes)
                    throw new ArgumentException("Evidence target environment payload 超出字节上限。", nameof(environment));
            }
        }

        if (stream.Length > MaximumEnvironmentPayloadBytes)
            throw new ArgumentException("Evidence target environment payload 超出字节上限。", nameof(environment));
        PublishBinaryState(EnvironmentPath, EnvironmentFileName, stream.ToArray());
    }

    private bool TryReadAuthenticatedTextState(
        string path,
        string expectedFileName,
        int expectedLineCount,
        out string[] lines)
        => TryReadAuthenticatedTextState(
            path,
            expectedFileName,
            _handshakeToken,
            expectedLineCount,
            out lines);

    private static bool TryReadAuthenticatedTextState(
        string path,
        string expectedFileName,
        string handshakeToken,
        int expectedLineCount,
        out string[] lines)
    {
        lines = [];
        string fullPath = ValidateOwnedFilePath(path, expectedFileName, requireDirectory: false);
        if (!TryReadBoundedFile(fullPath, MaximumControlStateBytes, out byte[] payload))
            return false;

        string contents;
        try
        {
            contents = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Evidence launcher control 状态不是有效 UTF-8。", exception);
        }
        lines = contents.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length != expectedLineCount
            || !string.Equals(lines[0], handshakeToken, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Evidence launcher control 状态无效。");
        }
        return true;
    }

    private static void PublishAuthenticatedTextState(
        string path,
        string expectedFileName,
        string handshakeToken,
        params string[] values)
    {
        string contents = string.Join(Environment.NewLine, [handshakeToken, .. values]);
        byte[] payload = StrictUtf8.GetBytes(contents);
        if (payload.Length is <= 0 or > MaximumControlStateBytes)
            throw new InvalidDataException("Evidence launcher control 状态长度无效。");
        PublishBinaryState(path, expectedFileName, payload);
    }

    private static void PublishBinaryState(
        string path,
        string expectedFileName,
        ReadOnlySpan<byte> payload)
    {
        string fullPath = ValidateOwnedFilePath(path, expectedFileName, requireDirectory: true);
        string directoryPath = Path.GetDirectoryName(fullPath)!;
        string temporaryPath = Path.Combine(
            directoryPath,
            $".control-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4_096,
                FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (IsOwnedTemporaryFile(temporaryPath, directoryPath) && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static byte[] ReadRequiredBoundedFile(
        string path,
        int maximumBytes,
        string description)
    {
        if (!TryReadBoundedFile(path, maximumBytes, out byte[] payload))
            throw new InvalidDataException($"{description} 不存在。");
        return payload;
    }

    private static bool TryReadBoundedFile(
        string path,
        int maximumBytes,
        out byte[] payload)
    {
        payload = [];
        try
        {
            string? directoryPath = Path.GetDirectoryName(path);
            if (directoryPath is null || !Directory.Exists(directoryPath))
                return false;
            FileAttributes directoryAttributes = File.GetAttributes(directoryPath);
            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Evidence launcher control 目录不能是 reparse point。");

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4_096,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 || stream.Length > maximumBytes)
                throw new InvalidDataException("Evidence launcher control 状态长度无效。");

            int expectedLength = checked((int)stream.Length);
            byte[] buffer = new byte[expectedLength + 1];
            int totalRead = 0;
            int maximumReadOperations = expectedLength + 1;
            for (int operation = 0;
                operation < maximumReadOperations && totalRead < buffer.Length;
                operation++)
            {
                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0)
                    break;
                totalRead += read;
            }

            if (totalRead <= 0 || totalRead > maximumBytes || stream.ReadByte() != -1)
                throw new InvalidDataException("Evidence launcher control 状态长度无效。");
            payload = buffer.AsSpan(0, totalRead).ToArray();
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void WriteBoundedString(BinaryWriter writer, string value, int maximumBytes)
    {
        int byteCount = StrictUtf8.GetByteCount(value);
        if (byteCount > maximumBytes)
            throw new ArgumentException("Evidence target environment 字符串超出字节上限。", nameof(value));
        writer.Write(byteCount);
        writer.Write(StrictUtf8.GetBytes(value));
    }

    private static string ReadBoundedString(BinaryReader reader, int maximumBytes)
        => ReadBoundedStringBody(reader, reader.ReadInt32(), maximumBytes);

    private static string ReadBoundedStringBody(
        BinaryReader reader,
        int byteCount,
        int maximumBytes)
    {
        if (byteCount is < 0 || byteCount > maximumBytes)
            throw new InvalidDataException("Evidence target environment 字符串长度无效。");
        byte[] bytes = reader.ReadBytes(byteCount);
        if (bytes.Length != byteCount)
            throw new EndOfStreamException();
        return StrictUtf8.GetString(bytes);
    }

    private static void ValidateEnvironmentEntry(string key, string? value)
    {
        if (string.IsNullOrEmpty(key)
            || key.Length > MaximumEnvironmentKeyCharacters
            || key.Contains('\0', StringComparison.Ordinal)
            || key.Contains('=', StringComparison.Ordinal)
            || (value?.Length ?? 0) > MaximumEnvironmentValueCharacters
            || (value?.Contains('\0', StringComparison.Ordinal) ?? false))
        {
            throw new ArgumentException("Evidence target environment 包含无效变量。");
        }
    }

    private static string ResolveSiblingPath(
        string completionPath,
        string siblingFileName,
        bool requireDirectory)
    {
        string fullCompletionPath = ValidateOwnedFilePath(
            completionPath,
            CompletionFileName,
            requireDirectory);
        return ValidateOwnedFilePath(
            Path.Combine(Path.GetDirectoryName(fullCompletionPath)!, siblingFileName),
            siblingFileName,
            requireDirectory);
    }

    private static string ValidateOwnedFilePath(
        string path,
        string expectedFileName,
        bool requireDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directoryPath = Path.GetDirectoryName(fullPath);
        if (directoryPath is null
            || !string.Equals(Path.GetFileName(fullPath), expectedFileName, PathComparison)
            || !IsOwnedDirectory(directoryPath)
            || (requireDirectory && !IsExistingNonReparseDirectory(directoryPath)))
        {
            throw new ArgumentException("Evidence launcher control 路径无效。", nameof(path));
        }

        return fullPath;
    }

    private static bool IsOwnedDirectory(string directoryPath)
        => GraphEvidenceOwnedDirectoryCleanup.IsOwnedDirectory(
            directoryPath,
            Path.GetTempPath(),
            DirectoryPrefix);

    private static bool IsExistingNonReparseDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return false;
        FileAttributes attributes = File.GetAttributes(directoryPath);
        return (attributes & FileAttributes.Directory) != 0
            && (attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static bool TryDeleteOwnedDirectory(string directoryPath, out string failureReason)
        => GraphEvidenceOwnedDirectoryCleanup.TryDelete(
            directoryPath,
            Path.GetTempPath(),
            DirectoryPrefix,
            out failureReason);

    private static bool IsOwnedTemporaryFile(string temporaryPath, string directoryPath)
    {
        string fullTemporaryPath = Path.GetFullPath(temporaryPath);
        return string.Equals(Path.GetDirectoryName(fullTemporaryPath), directoryPath, PathComparison)
            && Path.GetFileName(fullTemporaryPath).StartsWith(".control-", StringComparison.Ordinal)
            && Path.GetFileName(fullTemporaryPath).EndsWith(".tmp", StringComparison.Ordinal);
    }

    private static void TryWriteRetainedDiagnostic(string path)
    {
        try
        {
            Console.Error.WriteLine($"m40-process-control-temp-retained path={Path.GetFullPath(path)}");
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or ObjectDisposedException)
        {
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

internal readonly record struct GraphEvidenceLauncherCompletion(int ExitCode, bool OutputDrained);
