using System.Data;
using System.Data.Common;
using Quartz;
using Quartz.Impl.AdoJobStore.Common;
using SonnetDB.Data;

namespace SonnetDB.Quartz;

/// <summary>
/// 将 Quartz 的 ADO.NET 作业存储与 SonnetDB 关系数据库连接起来。
/// </summary>
public static class SonnetDbQuartzExtensions
{
    private const string ProviderName = "SonnetDB";

    /// <summary>
    /// 使用 SonnetDB 持久化单实例 Quartz 调度器的作业、触发器和恢复状态。
    /// </summary>
    /// <param name="store">Quartz 持久化存储选项。</param>
    /// <param name="connectionString">指向已初始化调度数据库的 SonnetDB ADO.NET 连接字符串。</param>
    public static void UseSonnetDB(this IPersistentStoreBuilder store, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var metadata = new DbMetadata
        {
            ProductName = ProviderName,
            ParameterNamePrefix = "@",
            BindByName = true,
            ExceptionType = typeof(DbException),
            ConfigureBinaryParameter = static parameter => parameter.DbType = DbType.Binary,
        };
        store.UseConnectionProvider(_ => new ProviderFactoryDbProvider(metadata, SndbProviderFactory.Instance, connectionString));
        store.UseDriverDelegate<SonnetDbQuartzDelegate>();
        store.ConfigureStore(options =>
        {
            options.TablePrefix = "QRTZ_";
        });
        store.UseSystemTextJsonSerializer();
    }
}
