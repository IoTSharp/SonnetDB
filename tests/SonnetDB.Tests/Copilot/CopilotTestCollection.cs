using Xunit;

namespace SonnetDB.Tests.Copilot;

/// <summary>
/// 串行运行使用全局 Copilot 诊断监听器和产生 Copilot 活动的端点测试。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CopilotTestCollection
{
    /// <summary>
    /// Copilot 测试集合名称。
    /// </summary>
    public const string Name = "Copilot activity listener tests";
}
