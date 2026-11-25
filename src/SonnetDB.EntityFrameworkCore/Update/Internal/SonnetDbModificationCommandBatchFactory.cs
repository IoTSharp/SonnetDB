using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;

namespace SonnetDB.EntityFrameworkCore.Update.Internal;

/// <summary>
/// 为 SonnetDB 创建保留 EF 命令顺序的有界修改命令批处理。
/// </summary>
public sealed class SonnetDbModificationCommandBatchFactory : IModificationCommandBatchFactory
{
    private const int DefaultBatchSize = 64;
    private const int MaximumBatchSize = 256;
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
    {
        var context = _dependencies.CurrentContext.Context;
        var options = context.GetService<IDbContextOptions>();
        var batchSize = Math.Min(RelationalOptionsExtension.Extract(options).MaxBatchSize ?? DefaultBatchSize, MaximumBatchSize);
        return batchSize == 1
            || context.GetService<IInterceptors>().Aggregate<IDbCommandInterceptor>() is not null
            || (context.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never
                && context.Database.CurrentTransaction is null)
            ? new SonnetDbModificationCommandBatch(_dependencies)
            : new SonnetDbDmlModificationCommandBatch(_dependencies, batchSize);
    }
}
