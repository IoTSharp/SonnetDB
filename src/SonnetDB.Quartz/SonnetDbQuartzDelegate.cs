using System.Data.Common;
using Quartz.Impl.AdoJobStore;

namespace SonnetDB.Quartz;

/// <summary>
/// 将 Quartz.NET 3.18 ADO.NET 委托的方言差异限制在对应的 SQL 语句中。
/// </summary>
public sealed class SonnetDbQuartzDelegate : StdAdoDelegate
{
    /// <summary>
    /// 为读取触发器关联作业的语句使用 SonnetDB 支持的显式连接语法。
    /// </summary>
    /// <param name="cth">Quartz 当前数据库连接和事务。</param>
    /// <param name="commandText">Quartz 提供的 SQL 文本。</param>
    /// <returns>已绑定连接和事务的命令。</returns>
    public override DbCommand PrepareCommand(ConnectionAndTransactionHolder cth, string commandText)
    {
        // Quartz 3.18 的触发器关联作业查询使用逗号连接，其余触发器查询已使用显式 JOIN。
        commandText = commandText.Replace(
            "SELECT J.JOB_NAME, J.JOB_GROUP, J.IS_DURABLE, J.JOB_CLASS_NAME, J.REQUESTS_RECOVERY FROM QRTZ_TRIGGERS T, QRTZ_JOB_DETAILS J WHERE",
            "SELECT J.JOB_NAME AS JOB_NAME, J.JOB_GROUP AS JOB_GROUP, J.IS_DURABLE AS IS_DURABLE,"
            + " J.JOB_CLASS_NAME AS JOB_CLASS_NAME, J.REQUESTS_RECOVERY AS REQUESTS_RECOVERY"
            + " FROM QRTZ_TRIGGERS T INNER JOIN QRTZ_JOB_DETAILS J"
            + " ON T.SCHED_NAME = J.SCHED_NAME AND T.JOB_NAME = J.JOB_NAME AND T.JOB_GROUP = J.JOB_GROUP WHERE",
            StringComparison.Ordinal)
            .Replace("t.TRIGGER_NAME, t.TRIGGER_GROUP, jd.JOB_CLASS_NAME, t.EXECUTION_GROUP",
                "t.TRIGGER_NAME, t.TRIGGER_GROUP, jd.JOB_CLASS_NAME, t.EXECUTION_GROUP AS EXECUTION_GROUP",
                StringComparison.Ordinal)
            .Replace("t.TRIGGER_NAME, t.TRIGGER_GROUP, jd.JOB_CLASS_NAME",
                "t.TRIGGER_NAME AS TRIGGER_NAME, t.TRIGGER_GROUP AS TRIGGER_GROUP, jd.JOB_CLASS_NAME AS JOB_CLASS_NAME",
                StringComparison.Ordinal)
            // SonnetDB 保留限定列的投影名，Quartz 按未限定的列名读取。
            .Replace("t.MISFIRE_ORIG_FIRE_TIME", "t.MISFIRE_ORIG_FIRE_TIME AS MISFIRE_ORIG_FIRE_TIME",
                StringComparison.Ordinal);
        return base.PrepareCommand(cth, commandText);
    }
}
