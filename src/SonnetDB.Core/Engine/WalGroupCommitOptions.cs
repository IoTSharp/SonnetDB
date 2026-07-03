namespace SonnetDB.Engine;

/// <summary>
/// WAL group-commit 配置。用于写入路径（当 <see cref="TsdbOptions.SyncWalOnEveryWrite"/> 为 <c>true</c> 时）
/// 以及始终强制同步的 Delete 路径（#194）：同一时间窗口内的多个请求共享一次 WAL fsync。
/// </summary>
public sealed record WalGroupCommitOptions
{
    /// <summary>
    /// 是否启用 WAL group-commit。启用后，同一时间窗口内的多个写请求会共享一次 WAL fsync。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 组提交等待窗口。窗口结束后执行一次 WAL fsync，并唤醒该批次内的所有写请求。
    /// </summary>
    public TimeSpan FlushWindow { get; init; } = TimeSpan.FromMilliseconds(2);

    /// <summary>默认 WAL group-commit 配置。</summary>
    public static WalGroupCommitOptions Default { get; } = new();
}
