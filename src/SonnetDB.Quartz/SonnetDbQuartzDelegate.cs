using System.Data.Common;
using System.Text.RegularExpressions;
using Quartz.Impl.AdoJobStore;

namespace SonnetDB.Quartz;

/// <summary>
/// 将 Quartz.NET ADO.NET 委托的方言差异限制在对应的 SQL 语句中。
/// </summary>
public sealed partial class SonnetDbQuartzDelegate : StdAdoDelegate
{
    /// <summary>
    /// 适配 Quartz 的作业连接语法和 ADO.NET 结果列名。
    /// </summary>
    /// <param name="cth">Quartz 当前数据库连接和事务。</param>
    /// <param name="commandText">Quartz 提供的 SQL 文本。</param>
    /// <returns>已绑定连接和事务的命令。</returns>
    public override DbCommand PrepareCommand(ConnectionAndTransactionHolder cth, string commandText)
    {
        commandText = commandText.Replace(
            "FROM QRTZ_TRIGGERS T, QRTZ_JOB_DETAILS J WHERE",
            "FROM QRTZ_TRIGGERS T INNER JOIN QRTZ_JOB_DETAILS J"
            + " ON T.SCHED_NAME = J.SCHED_NAME AND T.JOB_NAME = J.JOB_NAME AND T.JOB_GROUP = J.JOB_GROUP WHERE",
            StringComparison.Ordinal);

        // SonnetDB 保留限定列的投影名。只为简单投影补别名，保留表达式、谓词和显式别名。
        Match projection = SimpleProjection().Match(commandText);
        if (projection.Success)
        {
            commandText = QualifiedColumn().Replace(projection.Value, "$& AS ${column}")
                + commandText[projection.Length..];
        }

        return base.PrepareCommand(cth, commandText);
    }

    [GeneratedRegex(@"\ASELECT\s+(?:DISTINCT\s+)?[\w.,\s]+?(?=\bFROM\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SimpleProjection();

    [GeneratedRegex(@"(?<=\s)[A-Za-z][A-Za-z0-9_]*\.(?<column>[A-Z][A-Z0-9_]*)(?=\s*(?:,|\z))", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedColumn();
}
