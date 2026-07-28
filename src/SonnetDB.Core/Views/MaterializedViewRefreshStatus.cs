namespace SonnetDB.Views;

/// <summary>
/// 物化视图最近一次刷新生命周期的状态。
/// </summary>
public enum MaterializedViewRefreshStatus : byte
{
    /// <summary>已创建定义，但尚未成功刷新。</summary>
    Uninitialized = 0,

    /// <summary>全量刷新正在生成新的临时代际。</summary>
    Refreshing = 1,

    /// <summary>最近一次刷新成功，活动代际可读。</summary>
    Ready = 2,

    /// <summary>最近一次刷新失败；若已有活动代际，该代际仍保持可读。</summary>
    Failed = 3,
}
