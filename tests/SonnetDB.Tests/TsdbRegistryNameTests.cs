using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>验证控制面数据库身份在访问目录前拒绝换行。</summary>
public sealed class TsdbRegistryNameTests
{
    /// <summary>数据库名必须完整匹配，不能在末尾换行前提前结束匹配。</summary>
    [Theory]
    [InlineData("factory\n")]
    [InlineData("factory\r\n")]
    [InlineData("\rfactory")]
    [InlineData("factory\nforged")]
    public void TryCreate_WithLineBreakInName_RejectsBeforeCreatingDatabase(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "sndb-name-test", Guid.NewGuid().ToString("N"));
        try
        {
            using var registry = new TsdbRegistry(root);
            Assert.False(TsdbRegistry.IsValidName(name));
            Assert.Throws<ArgumentException>(() => registry.TryCreate(name, out _));
            Assert.Empty(registry.ListDatabases());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
