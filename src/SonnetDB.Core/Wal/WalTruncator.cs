namespace SonnetDB.Wal;

/// <summary>
/// WAL 回收辅助类：在 Checkpoint 已经持久到 Segment 之后，安全地"截断"WAL。
/// </summary>
/// <remarks>
/// v1 实现策略：rename + 重建（"swap"），避免就地截断的并发风险。
/// </remarks>
public static class WalTruncator
{
    /// <summary>
    /// 安全截断 WAL：
    /// <list type="number">
    ///   <item><description>Sync 当前 WAL；</description></item>
    ///   <item><description>关闭（Dispose）当前 WalWriter；</description></item>
    ///   <item><description>把当前 WAL 重命名为归档文件 "active.SDBWAL.archived-{nextLsn:X16}"；</description></item>
    ///   <item><description>若 <paramref name="keepArchive"/> 为 false，立即删除归档文件；</description></item>
    ///   <item><description>以 <paramref name="nextLsn"/> 为 startLsn 创建新的 active WAL；</description></item>
    ///   <item><description>返回新打开的 <see cref="WalWriter"/>。</description></item>
    /// </list>
    /// </summary>
    /// <param name="currentWriter">当前活跃的 WAL 写入器（必须处于打开状态）。</param>
    /// <param name="activeWalPath">active WAL 文件的完整路径。</param>
    /// <param name="nextLsn">新 WAL 文件的起始 LSN。</param>
    /// <param name="bufferSize">新 WAL 写缓冲区大小（字节）。</param>
    /// <param name="keepArchive">是否保留归档文件（默认 false，即 rename 后立即删除）。</param>
    /// <returns>新创建的 <see cref="WalWriter"/> 实例，startLsn 为 <paramref name="nextLsn"/>。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null 时抛出。</exception>
    /// <remarks>
    /// 此方法已废弃。请改用 <see cref="WalSegmentSet.Roll"/> + <see cref="WalSegmentSet.RecycleUpTo"/>。
    /// </remarks>
    [Obsolete("Use WalSegmentSet.Roll + RecycleUpTo instead. This method remains for backward compatibility.")]
    public static WalWriter SwapAndTruncate(
        WalWriter currentWriter,
        string activeWalPath,
        long nextLsn,
        int bufferSize,
        bool keepArchive = false)
    {
        ArgumentNullException.ThrowIfNull(currentWriter);
        ArgumentNullException.ThrowIfNull(activeWalPath);

        // 1. Sync 旧 WAL（确保 Checkpoint 已持久化）
        if (currentWriter.IsOpen)
            currentWriter.Sync();

        // 2. 关闭旧 WAL 写入器
        currentWriter.Dispose();

        // 3. 重命名为归档文件
        string? dir = Path.GetDirectoryName(activeWalPath);
        string archiveFileName = $"{Path.GetFileName(activeWalPath)}.archived-{nextLsn:X16}";
        string archivePath = dir != null
            ? Path.Combine(dir, archiveFileName)
            : archiveFileName;

        File.Move(activeWalPath, archivePath);

        // 4. 删除归档文件（keepArchive == false 时）
        if (!keepArchive)
            File.Delete(archivePath);

        // 5. 创建新的 active WAL，从 nextLsn 开始
        return WalWriter.Open(activeWalPath, startLsn: nextLsn, bufferSize: bufferSize);
    }
}
