using System.Buffers;
using System.IO.Hashing;
using SonnetDB.IO;
using SonnetDB.Storage.Format;

namespace SonnetDB.Wal;

/// <summary>
/// WAL segment 磁盘布局工具类：文件命名、路径生成、目录枚举与 legacy 升级。
/// </summary>
public static class WalSegmentLayout
{
    /// <summary>WAL segment 文件扩展名（含点）。</summary>
    public const string Extension = ".SDBWAL";

    /// <summary>旧版（PR #13）单文件 WAL 的文件名。</summary>
    public const string LegacyActiveFileName = "active.SDBWAL";

    /// <summary>WAL checkpoint LSN 元数据文件名。</summary>
    public const string CheckpointFileName = "checkpoint.SDBWCKP";

    /// <summary>Flush 段发布恢复标记文件扩展名。</summary>
    public const string FlushPublicationExtension = ".SDBFPUB";

    /// <summary>
    /// 根据 startLsn 生成 segment 文件名（不含目录）：<c>{startLsn:X16}.SDBWAL</c>。
    /// </summary>
    /// <param name="startLsn">segment 起始 LSN。</param>
    /// <returns>segment 文件名，如 <c>0000000000000001.SDBWAL</c>。</returns>
    public static string SegmentFileName(long startLsn) =>
        $"{startLsn:X16}{Extension}";

    /// <summary>
    /// 根据 walDir 和 startLsn 生成 segment 完整路径。
    /// </summary>
    /// <param name="walDir">WAL 子目录路径。</param>
    /// <param name="startLsn">segment 起始 LSN。</param>
    /// <returns>segment 文件完整路径。</returns>
    public static string SegmentPath(string walDir, long startLsn) =>
        Path.Combine(walDir, SegmentFileName(startLsn));

    /// <summary>
    /// 根据 walDir 生成 checkpoint LSN 元数据文件完整路径。
    /// </summary>
    /// <param name="walDir">WAL 子目录路径。</param>
    /// <returns>checkpoint 元数据文件完整路径。</returns>
    public static string CheckpointPath(string walDir) =>
        Path.Combine(walDir, CheckpointFileName);

    /// <summary>
    /// 根据 walDir 和 SegmentId 生成 Flush 段发布恢复标记完整路径。
    /// 每个待发布段独占一个标记，避免失败段的 marker 被后续 Flush 覆盖。
    /// </summary>
    /// <param name="walDir">WAL 子目录路径。</param>
    /// <param name="segmentId">待发布段标识。</param>
    /// <returns>Flush 段发布恢复标记文件完整路径。</returns>
    public static string FlushPublicationPath(string walDir, long segmentId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentId);
        return Path.Combine(walDir, $"{segmentId:X16}{FlushPublicationExtension}");
    }

    /// <summary>
    /// 枚举 WAL 目录中全部已原子发布的 Flush 段发布标记。临时文件（<c>.tmp</c>）不在此列。
    /// </summary>
    /// <param name="walDir">WAL 子目录路径。</param>
    /// <returns>按 SegmentId 升序排列的 marker 路径与解析出的 SegmentId。</returns>
    /// <exception cref="InvalidDataException">最终 marker 文件名非法时抛出。</exception>
    public static IReadOnlyList<(long SegmentId, string Path)> EnumerateFlushPublicationPaths(string walDir)
    {
        ArgumentNullException.ThrowIfNull(walDir);
        if (!Directory.Exists(walDir))
            return [];

        var markers = new List<(long SegmentId, string Path)>();
        foreach (string path in Directory.EnumerateFiles(walDir, "*", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(path);
            if (!fileName.EndsWith(FlushPublicationExtension, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryParseFlushPublicationSegmentId(fileName, out long segmentId))
            {
                throw new InvalidDataException(
                    $"Flush publication marker '{path}' has an invalid file name.");
            }

            markers.Add((segmentId, path));
        }

        markers.Sort(static (left, right) => left.SegmentId.CompareTo(right.SegmentId));
        return markers;
    }

    /// <summary>
    /// 尝试从文件名中解析 segment 的 startLsn。
    /// 合法格式：16 位大写/小写十六进制 + <see cref="Extension"/>。
    /// </summary>
    /// <param name="fileName">仅文件名部分（不含目录），如 <c>0000000000000A31.SDBWAL</c>。</param>
    /// <param name="startLsn">解析成功时的 startLsn；失败时为 0。</param>
    /// <returns>解析成功返回 <c>true</c>，否则 <c>false</c>。</returns>
    public static bool TryParseStartLsn(string fileName, out long startLsn)
    {
        startLsn = 0;
        if (!fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            return false;

        string hex = Path.GetFileNameWithoutExtension(fileName);
        if (hex.Length != 16)
            return false;

        return long.TryParse(hex,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out startLsn);
    }

    private static bool TryParseFlushPublicationSegmentId(string fileName, out long segmentId)
    {
        segmentId = 0;
        if (!fileName.EndsWith(FlushPublicationExtension, StringComparison.OrdinalIgnoreCase))
            return false;

        string hex = Path.GetFileNameWithoutExtension(fileName);
        return hex.Length == 16
            && long.TryParse(
                hex,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out segmentId)
            && segmentId > 0;
    }

    /// <summary>
    /// 按 startLsn 升序枚举一个 wal/ 目录下的全部合法 segment（自动跳过 .tmp、legacy active.SDBWAL 及未知文件）。
    /// </summary>
    /// <param name="walDir">WAL 子目录路径。</param>
    /// <returns>按 <see cref="WalSegmentInfo.StartLsn"/> 升序排列的 segment 信息列表。</returns>
    public static IReadOnlyList<WalSegmentInfo> Enumerate(string walDir)
    {
        if (!Directory.Exists(walDir))
            return Array.Empty<WalSegmentInfo>();

        var list = new List<WalSegmentInfo>();
        foreach (string file in Directory.EnumerateFiles(walDir, $"*{Extension}"))
        {
            string name = Path.GetFileName(file);
            if (TryParseStartLsn(name, out long lsn))
            {
                long len;
                try
                {
                    len = new FileInfo(file).Length;
                }
                catch (FileNotFoundException)
                {
                    // 段在 Enumerate 与 stat 之间被删除（如并发轮转），安全跳过。
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }
                // 其余 I/O 错误（锁定/权限）不可吞掉：把长度当 0 会让恢复误判段为空，可能丢数据。
                var info = new WalSegmentInfo(lsn, file, len);
                if (TryReadLastLsnFooter(file, out long lastLsn))
                    info = info with { HasLastLsn = true, LastLsn = lastLsn };
                list.Add(info);
            }
        }

        list.Sort(static (a, b) => a.StartLsn.CompareTo(b.StartLsn));
        return list.AsReadOnly();
    }

    /// <summary>
    /// 若 <c>wal/active.SDBWAL</c>（旧版单文件）存在，则读取其 <c>WalFileHeader.FirstLsn</c>，
    /// 将文件重命名为 <c>{firstLsn:X16}.SDBWAL</c>，完成 legacy → segmented 格式升级。
    /// 文件不存在时静默返回。
    /// </summary>
    /// <param name="walDir">WAL 子目录路径。</param>
    public static void UpgradeLegacyIfPresent(string walDir)
    {
        string legacyPath = Path.Combine(walDir, LegacyActiveFileName);
        if (!File.Exists(legacyPath))
            return;

        long firstLsn = ReadFirstLsnFromWalFile(legacyPath);
        string newPath = SegmentPath(walDir, firstLsn);

        // 若目标名已存在（重复升级），直接删除 legacy 文件
        if (File.Exists(newPath))
        {
            File.Delete(legacyPath);
            // 旧文件消失同样是恢复边界的一部分；目录项落盘后才能让后续 Open
            // 将升级完成视为持久状态，避免掉电时新旧名称都不可见。
            DirectoryFsync.FlushRequired(walDir);
            return;
        }

        File.Move(legacyPath, newPath);
        // legacy -> segmented 名称迁移会改变 WAL 枚举集合。没有目录 fsync 时，掉电可能
        // 留下目录项未持久化的 rename，下一次启动会把已有记录误判为空 WAL。
        DirectoryFsync.FlushRequired(walDir);
    }

    // ── 私有辅助 ─────────────────────────────────────────────────────────────

    private static long ReadFirstLsnFromWalFile(string path)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(FormatSizes.WalFileHeaderSize);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int read = 0;
            while (read < FormatSizes.WalFileHeaderSize)
            {
                int n = fs.Read(buf, read, FormatSizes.WalFileHeaderSize - read);
                if (n == 0) break;
                read += n;
            }

            if (read < FormatSizes.WalFileHeaderSize)
                return 1L; // 文件头不完整，回退到 LSN=1

            var reader = new SpanReader(buf.AsSpan(0, FormatSizes.WalFileHeaderSize));
            var header = reader.ReadStruct<WalFileHeader>();
            return header.IsValid() ? header.FirstLsn : 1L;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static bool TryReadLastLsnFooter(string path, out long lastLsn)
    {
        lastLsn = 0;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < FormatSizes.WalFileHeaderSize + FormatSizes.WalLastLsnFooterSize)
                return false;

            Span<byte> fileHeaderBuffer = stackalloc byte[FormatSizes.WalFileHeaderSize];
            if (ReadExact(fs, fileHeaderBuffer) < FormatSizes.WalFileHeaderSize)
                return false;

            var fileHeaderReader = new SpanReader(fileHeaderBuffer);
            var fileHeader = fileHeaderReader.ReadStruct<WalFileHeader>();
            if (!fileHeader.IsValid())
                return false;

            Span<byte> footerBuffer = stackalloc byte[FormatSizes.WalLastLsnFooterSize];
            fs.Position = fs.Length - FormatSizes.WalLastLsnFooterSize;
            if (ReadExact(fs, footerBuffer) < FormatSizes.WalLastLsnFooterSize)
                return false;

            var footerReader = new SpanReader(footerBuffer);
            var footer = footerReader.ReadStruct<WalLastLsnFooter>();
            if (!footer.IsShapeValid())
                return false;

            if (footer.RecordsEndOffset != fs.Length - FormatSizes.WalLastLsnFooterSize)
                return false;

            if (footer.RecordsEndOffset < FormatSizes.WalFileHeaderSize)
                return false;

            if (footer.LastLsn < fileHeader.FirstLsn - 1 || footer.LastLsn == long.MaxValue)
                return false;

            uint expectedCrc = Crc32.HashToUInt32(footerBuffer[..WalLastLsnFooter.CrcCoveredLength]);
            if (footer.Crc32 != expectedCrc)
                return false;

            lastLsn = footer.LastLsn;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static int ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }
}
