using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace SonnetDB.EntityFrameworkCore.Update.Internal;

/// <summary>
/// 使用 SonnetDB ADO.NET <c>RecordsAffected</c> 校验单条实体修改结果的批处理。
/// </summary>
public sealed class SonnetDbModificationCommandBatch : SingularModificationCommandBatch
{
    /// <summary>
    /// 创建 SonnetDB 单语句修改命令批处理。
    /// </summary>
    /// <param name="dependencies">批处理依赖。</param>
    public SonnetDbModificationCommandBatch(ModificationCommandBatchFactoryDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <summary>
    /// 消费同步修改结果；无返回行的 INSERT、UPDATE、DELETE 使用 ADO.NET 真实影响行数执行并发校验。
    /// </summary>
    /// <param name="reader">关系数据读取器。</param>
    protected override void Consume(RelationalDataReader reader)
    {
        if (!ConsumesRecordsAffected(reader, out var recordsAffected))
        {
            base.Consume(reader);
            return;
        }

        if (recordsAffected != 1)
            ThrowAggregateUpdateConcurrencyException(reader, commandIndex: 1, expectedRowsAffected: 1, recordsAffected);
        reader.Close();
    }

    /// <summary>
    /// 消费异步修改结果；无返回行的 INSERT、UPDATE、DELETE 使用 ADO.NET 真实影响行数执行并发校验。
    /// </summary>
    /// <param name="reader">关系数据读取器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步消费过程的任务。</returns>
    protected override async Task ConsumeAsync(
        RelationalDataReader reader,
        CancellationToken cancellationToken = default)
    {
        if (!ConsumesRecordsAffected(reader, out var recordsAffected))
        {
            await base.ConsumeAsync(reader, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (recordsAffected != 1)
        {
            await ThrowAggregateUpdateConcurrencyExceptionAsync(
                    reader,
                    commandIndex: 1,
                    expectedRowsAffected: 1,
                    recordsAffected,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await reader.CloseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 单语句批处理仅在没有结果行时读取 <see cref="System.Data.Common.DbDataReader.RecordsAffected"/>；
    /// 带生成值返回行的命令继续交给 EF Core 标准结果传播逻辑。
    /// </summary>
    private bool ConsumesRecordsAffected(RelationalDataReader reader, out int recordsAffected)
    {
        if (ResultSetMappings.Count == 1 && ResultSetMappings[0] == ResultSetMapping.NoResults)
        {
            recordsAffected = reader.DbDataReader.RecordsAffected;
            return true;
        }

        recordsAffected = -1;
        return false;
    }
}
