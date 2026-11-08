using System.Globalization;

namespace SonnetDB.Kv.Lsm;

/// <summary>
/// KV-LSM 实例目录内的文件命名。
/// </summary>
/// <remarks>
/// <code>
/// &lt;root&gt;/CURRENT                     指向当前 manifest 的单行文本
/// &lt;root&gt;/MANIFEST-&lt;number:D12&gt;      VersionEdit 日志
/// &lt;root&gt;/sst/&lt;number:D12&gt;.SDBKVSST   SST 文件
/// </code>
/// manifest 与 SST 共用同一个单调文件号空间。
/// </remarks>
internal static class KvLsmPaths
{
    /// <summary>CURRENT 文件名。</summary>
    internal const string CurrentFileName = "CURRENT";

    /// <summary>manifest 文件名前缀。</summary>
    internal const string ManifestPrefix = "MANIFEST-";

    /// <summary>SST 子目录名。</summary>
    internal const string SstDirectoryName = "sst";

    /// <summary>临时文件后缀。</summary>
    internal const string TempSuffix = ".tmp";

    internal static string CurrentPath(string root) => Path.Combine(root, CurrentFileName);

    internal static string ManifestFileName(long number)
        => ManifestPrefix + number.ToString("D12", CultureInfo.InvariantCulture);

    internal static string ManifestPath(string root, long number) => Path.Combine(root, ManifestFileName(number));

    internal static string SstDirectory(string root) => Path.Combine(root, SstDirectoryName);

    internal static string SstPath(string root, long number)
        => Path.Combine(SstDirectory(root), number.ToString("D12", CultureInfo.InvariantCulture) + KvSstFormat.FileExtension);

    /// <summary>解析 <c>MANIFEST-&lt;12 位数字&gt;</c>。</summary>
    internal static bool TryParseManifestFileName(string fileName, out long number)
    {
        number = 0;
        return fileName.Length == ManifestPrefix.Length + 12
            && fileName.StartsWith(ManifestPrefix, StringComparison.Ordinal)
            && TryParseNumber(fileName.AsSpan(ManifestPrefix.Length), out number);
    }

    /// <summary>解析 <c>&lt;12 位数字&gt;.SDBKVSST</c>。</summary>
    internal static bool TryParseSstFileName(string fileName, out long number)
    {
        number = 0;
        return fileName.Length == 12 + KvSstFormat.FileExtension.Length
            && fileName.EndsWith(KvSstFormat.FileExtension, StringComparison.Ordinal)
            && TryParseNumber(fileName.AsSpan(0, 12), out number);
    }

    private static bool TryParseNumber(ReadOnlySpan<char> digits, out long number)
    {
        number = 0;
        foreach (char c in digits)
        {
            if (c is < '0' or > '9')
                return false;
        }
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    }
}

/// <summary>
/// manifest 记录的单个 SST 文件元数据（不可变）。
/// </summary>
/// <param name="FileNumber">文件号（正数，实例内唯一）。</param>
/// <param name="Level">层号，0～<see cref="KvVersion.LevelCount"/>-1。</param>
/// <param name="FileSize">文件字节数，打开时与磁盘核对。</param>
/// <param name="SmallestKey">最小 internal key。</param>
/// <param name="LargestKey">最大 internal key。</param>
/// <param name="MinSequence">最小 sequence。</param>
/// <param name="MaxSequence">最大 sequence。</param>
internal sealed record KvLsmFileMetadata(
    long FileNumber,
    int Level,
    long FileSize,
    byte[] SmallestKey,
    byte[] LargestKey,
    long MinSequence,
    long MaxSequence)
{
    /// <summary>由 SST 属性构造元数据。</summary>
    internal static KvLsmFileMetadata FromProperties(long fileNumber, int level, long fileSize, KvSstProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return new KvLsmFileMetadata(
            fileNumber, level, fileSize,
            properties.SmallestKey, properties.LargestKey,
            properties.MinSequence, properties.MaxSequence);
    }

    /// <summary>校验字段取值。</summary>
    /// <exception cref="InvalidDataException">任一字段无效。</exception>
    internal void Validate()
    {
        if (FileNumber <= 0
            || Level < 0 || Level >= KvVersion.LevelCount
            || FileSize < KvSstFooter.Size
            || MinSequence < 0
            || MaxSequence < MinSequence
            || MaxSequence > KvInternalKey.MaxSequence
            || SmallestKey is null
            || LargestKey is null)
        {
            throw new InvalidDataException($"KV-LSM file metadata for #{FileNumber} is invalid.");
        }

        KvInternalKey.Validate(SmallestKey);
        KvInternalKey.Validate(LargestKey);
        if (KvInternalKey.Compare(SmallestKey, LargestKey) > 0)
            throw new InvalidDataException($"KV-LSM file #{FileNumber} key range is inverted.");
    }

    /// <inheritdoc />
    public bool Equals(KvLsmFileMetadata? other)
        => other is not null
            && FileNumber == other.FileNumber
            && Level == other.Level
            && FileSize == other.FileSize
            && MinSequence == other.MinSequence
            && MaxSequence == other.MaxSequence
            && SmallestKey.AsSpan().SequenceEqual(other.SmallestKey)
            && LargestKey.AsSpan().SequenceEqual(other.LargestKey);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(FileNumber, Level, FileSize);
}
