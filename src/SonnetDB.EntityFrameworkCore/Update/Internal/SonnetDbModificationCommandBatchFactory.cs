using Microsoft.EntityFrameworkCore.Update;

namespace SonnetDB.EntityFrameworkCore.Update.Internal;

/// <summary>
/// 为 SonnetDB 创建单语句修改命令批处理，保持 MVP 阶段 SQL 简单可预测。
/// </summary>
public sealed class SonnetDbModificationCommandBatchFactory : IModificationCommandBatchFactory
{
    private readonly ModificationCommandBatchFactoryDependencies _dependencies;

    /// <summary>
    /// 创建 SonnetDB 修改命令批处理工厂。
    /// </summary>
    /// <param name="dependencies">批处理工厂依赖。</param>
    public SonnetDbModificationCommandBatchFactory(ModificationCommandBatchFactoryDependencies dependencies)
    {
        _dependencies = dependencies;
    }

    /// <inheritdoc />
    public ModificationCommandBatch Create()
        => new SingularModificationCommandBatch(_dependencies);
}
