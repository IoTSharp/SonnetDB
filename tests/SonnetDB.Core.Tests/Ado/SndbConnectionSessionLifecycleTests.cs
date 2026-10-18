using System.Data;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

/// <summary>验证嵌入式 ADO 连接对临时会话清理失败保留可重试所有权。</summary>
public sealed class SndbConnectionSessionLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-ado-session-close-" + Guid.NewGuid().ToString("N"));

    /// <summary>首次关闭因 owner 标记变化失败后，连接保留实现并允许恢复标记后重试。</summary>
    [Fact]
    public void Close_TemporaryCleanupFails_CanRetrySameConnection()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TEMP TABLE staged(Id INT,PRIMARY KEY(Id))";
            Assert.Equal(0, command.ExecuteNonQuery());
        }
        Guid sessionId = connection.EmbeddedSessionId
            ?? throw new InvalidOperationException("嵌入式连接缺少会话 ID。");
        string directory = Path.Combine(
            Path.GetTempPath(), "sonnetdb-sql-session-" + sessionId.ToString("N"));
        string marker = Path.Combine(directory, "session.owner");
        string owner = File.ReadAllText(marker);
        File.WriteAllText(marker, "foreign");

        Assert.Throws<IOException>(connection.Close);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.True(Directory.Exists(directory));

        File.WriteAllText(marker, owner);
        connection.Close();
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>只回收本测试创建的唯一数据库目录。</summary>
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporary, full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sndb-ado-session-close-", Path.GetFileName(full), StringComparison.Ordinal);
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }
}
