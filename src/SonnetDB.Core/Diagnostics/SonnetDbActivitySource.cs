using System.Diagnostics;

namespace SonnetDB.Diagnostics;

/// <summary>
/// SonnetDB.Core 的进程级 <see cref="ActivitySource"/>（M17 #89）。
/// <para>
/// 无监听者时 <see cref="ActivitySource.StartActivity(string)"/> 返回 null、近似零开销。
/// span 命名与 tag 遵守 OTel 数据库语义约定：<c>db.system=sonnetdb</c>、<c>db.operation</c>、
/// 自有前缀 tag 用 <c>sonnetdb.*</c>（如 <c>sonnetdb.segment.id</c>、<c>sonnetdb.measurement.name</c>）。
/// </para>
/// </summary>
public static class SonnetDbActivitySource
{
    /// <summary>ActivitySource 名称；服务端 OTel 引导用 <c>AddSource(SonnetDbActivitySource.SourceName)</c> 订阅。</summary>
    public const string SourceName = "SonnetDB.Core";

    /// <summary>共享 ActivitySource 实例。</summary>
    public static readonly ActivitySource Source = new(SourceName);

    /// <summary><c>db.system</c> 语义约定值。</summary>
    public const string DbSystem = "sonnetdb";

    /// <summary>
    /// 开启一个内部操作 span 并写入 <c>db.system</c>/<c>db.operation</c>；无监听者时返回 null。
    /// </summary>
    internal static Activity? StartOperation(string name, string operation)
    {
        var activity = Source.StartActivity(name);
        if (activity is not null)
        {
            activity.SetTag("db.system", DbSystem);
            activity.SetTag("db.operation", operation);
        }

        return activity;
    }

    /// <summary>把异常标记到 span（状态 + 异常类型），供 catch 块复用。</summary>
    internal static void RecordFailure(Activity? activity, Exception ex)
    {
        if (activity is null)
            return;
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("exception.type", ex.GetType().FullName);
    }
}
