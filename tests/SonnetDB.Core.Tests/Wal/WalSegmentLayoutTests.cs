using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Wal;

/// <summary>
/// <see cref="WalSegmentLayout"/> 单元测试。
/// </summary>
public sealed class WalSegmentLayoutTests : IDisposable
{
    private readonly string _tempDir;

    public WalSegmentLayoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SegmentFileName_ReturnsCorrectFormat()
    {
        Assert.Equal("0000000000000001.SDBWAL", WalSegmentLayout.SegmentFileName(1));
        Assert.Equal("0000000000000A31.SDBWAL", WalSegmentLayout.SegmentFileName(0xA31));
        Assert.Equal("FFFFFFFFFFFFFFFF.SDBWAL", WalSegmentLayout.SegmentFileName(unchecked((long)0xFFFFFFFFFFFFFFFF)));
        Assert.Equal("0000000000000000.SDBWAL", WalSegmentLayout.SegmentFileName(0));
    }

    [Fact]
    public void SegmentPath_CombinesDirectoryAndFileName()
    {
        string dir = "/tmp/wal";
        string path = WalSegmentLayout.SegmentPath(dir, 1L);
        Assert.Equal(Path.Combine(dir, "0000000000000001.SDBWAL"), path);
    }

    [Fact]
    public void TryParseStartLsn_ValidName_ParsesCorrectly()
    {
        Assert.True(WalSegmentLayout.TryParseStartLsn("0000000000000001.SDBWAL", out long lsn1));
        Assert.Equal(1L, lsn1);

        Assert.True(WalSegmentLayout.TryParseStartLsn("0000000000000A31.SDBWAL", out long lsnA31));
        Assert.Equal(0xA31L, lsnA31);

        Assert.True(WalSegmentLayout.TryParseStartLsn("00000000000015C2.SDBWAL", out long lsn15c2));
        Assert.Equal(0x15C2L, lsn15c2);
    }

    [Fact]
    public void TryParseStartLsn_LegacyFileName_ReturnsFalse()
    {
        Assert.False(WalSegmentLayout.TryParseStartLsn(WalSegmentLayout.LegacyActiveFileName, out _));
        Assert.False(WalSegmentLayout.TryParseStartLsn("active.SDBWAL", out _));
    }

    [Fact]
    public void TryParseStartLsn_TmpFile_ReturnsFalse()
    {
        Assert.False(WalSegmentLayout.TryParseStartLsn("0000000000000001.SDBWAL.tmp", out _));
        Assert.False(WalSegmentLayout.TryParseStartLsn("0000000000000001.tmp", out _));
    }

    [Fact]
    public void TryParseStartLsn_TooShortHex_ReturnsFalse()
    {
        Assert.False(WalSegmentLayout.TryParseStartLsn("00000001.SDBWAL", out _));
        Assert.False(WalSegmentLayout.TryParseStartLsn("000000000000001.SDBWAL", out _)); // 15 chars
    }

    [Fact]
    public void TryParseStartLsn_WrongExtension_ReturnsFalse()
    {
        Assert.False(WalSegmentLayout.TryParseStartLsn("0000000000000001.SDBSEG", out _));
        Assert.False(WalSegmentLayout.TryParseStartLsn("0000000000000001.txt", out _));
    }

    [Fact]
    public void TryParseStartLsn_InvalidHex_ReturnsFalse()
    {
        Assert.False(WalSegmentLayout.TryParseStartLsn("00000000ZZZZZZZZ.SDBWAL", out _));
    }

    [Fact]
    public void Enumerate_EmptyDirectory_ReturnsEmpty()
    {
        var list = WalSegmentLayout.Enumerate(_tempDir);
        Assert.Empty(list);
    }

    [Fact]
    public void Enumerate_ReturnsSegmentsInAscendingOrder()
    {
        // 创建几个 segment 文件（内容随意，无需有效头部）
        File.WriteAllBytes(WalSegmentLayout.SegmentPath(_tempDir, 0x15C2), []);
        File.WriteAllBytes(WalSegmentLayout.SegmentPath(_tempDir, 0xA31), []);
        File.WriteAllBytes(WalSegmentLayout.SegmentPath(_tempDir, 1), []);

        // 创建一些应该被忽略的文件
        File.WriteAllBytes(Path.Combine(_tempDir, "active.SDBWAL"), []);
        File.WriteAllBytes(Path.Combine(_tempDir, "0000000000000001.SDBWAL.tmp"), []);
        File.WriteAllBytes(Path.Combine(_tempDir, "garbage.txt"), []);

        var list = WalSegmentLayout.Enumerate(_tempDir);

        Assert.Equal(3, list.Count);
        Assert.Equal(1L, list[0].StartLsn);
        Assert.Equal(0xA31L, list[1].StartLsn);
        Assert.Equal(0x15C2L, list[2].StartLsn);
    }

    [Fact]
    public void Enumerate_NonExistentDirectory_ReturnsEmpty()
    {
        var list = WalSegmentLayout.Enumerate(Path.Combine(_tempDir, "nonexistent"));
        Assert.Empty(list);
    }

    [Fact]
    public void UpgradeLegacyIfPresent_NoLegacyFile_DoesNothing()
    {
        // 目录中无 active.SDBWAL，应静默返回
        WalSegmentLayout.UpgradeLegacyIfPresent(_tempDir);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void UpgradeLegacyIfPresent_WithFirstLsn42_RenamesCorrectly()
    {
        // 用 WalWriter 创建含 FirstLsn=42 的 legacy active.SDBWAL
        string legacyPath = Path.Combine(_tempDir, "active.SDBWAL");
        using (var writer = WalWriter.Open(legacyPath, startLsn: 42))
        {
            writer.AppendWritePoint(1UL, 1000L, "cpu", SonnetDB.Model.FieldValue.FromDouble(1.0));
            writer.Sync();
        }

        WalSegmentLayout.UpgradeLegacyIfPresent(_tempDir);

        // 旧路径应不存在
        Assert.False(File.Exists(legacyPath));

        // 新路径应存在：000000000000002A.SDBWAL（LSN=42=0x2A）
        string newPath = WalSegmentLayout.SegmentPath(_tempDir, 42L);
        Assert.True(File.Exists(newPath));
        Assert.Equal("000000000000002A.SDBWAL", Path.GetFileName(newPath));
    }

    [Fact]
    public void UpgradeLegacyIfPresent_AlreadyUpgraded_DeletesLegacy()
    {
        // 模拟：legacy 文件与目标文件同时存在（重复升级）
        string legacyPath = Path.Combine(_tempDir, "active.SDBWAL");
        string targetPath = WalSegmentLayout.SegmentPath(_tempDir, 1L);

        // 先创建有效 legacy WAL（firstLsn=1）
        using (var writer = WalWriter.Open(legacyPath, startLsn: 1))
        {
            writer.Sync();
        }
        // 再创建目标文件
        File.WriteAllBytes(targetPath, []);

        WalSegmentLayout.UpgradeLegacyIfPresent(_tempDir);

        // legacy 文件应已被删除
        Assert.False(File.Exists(legacyPath));
        // 目标文件仍存在（内容不变）
        Assert.True(File.Exists(targetPath));
    }
}
