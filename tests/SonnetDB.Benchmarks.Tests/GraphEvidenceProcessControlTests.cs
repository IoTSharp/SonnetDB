using SonnetDB.Benchmarks.Benchmarks;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

/// <summary>验证证据控制状态与原子文件替换的跨平台兼容性。</summary>
public sealed class GraphEvidenceProcessControlTests
{
    /// <summary>Windows 原子替换持有 delete access 时，读者仍能读取已完整发布的状态。</summary>
    [Fact]
    public void TryReadCompletion_WithDeleteAccessHandle_ReadsPublishedState()
    {
        string token = Guid.NewGuid().ToString("N");
        using var control = GraphEvidenceProcessControl.Create(token);
        GraphEvidenceProcessControl.PublishCompletion(control.CompletionPath, token, 42, outputDrained: true);
        using var deleteAccess = new FileStream(
            control.CompletionPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 1,
            FileOptions.DeleteOnClose);

        Assert.True(control.TryReadCompletion(out var completion));
        Assert.Equal(42, completion.ExitCode);
        Assert.True(completion.OutputDrained);
    }
}
