using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Core.Tests.SemanticContent;

public sealed class FaceSqlResourceIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-face-sql-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("__face_recognition")]
    [InlineData("__FACE_RECOGNITION.")]
    public void SqlScope_ReservedFaceKeyspace_RejectsAccessAndRestoresTrustedSdk(string name)
    {
        using var database = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Kv = KvOptions.Default with { AutoCheckpointEnabled = false, ExpirerEnabled = false, CleanupEnabled = false },
        });
        var trusted = database.Keyspaces.Open("__face_recognition");
        trusted.Put("private", [1, 2, 3]);
        using (SqlRagResourceScope.Enter())
        {
            Assert.Throws<InvalidOperationException>(() => database.Keyspaces.Open(name));
            Assert.False(SqlRagResourceScope.IsVisible(name));
            Assert.True(SqlRagResourceScope.IsVisible("user_faces"));
        }
        Assert.NotNull(database.Keyspaces.Open("__face_recognition").Get("private"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
