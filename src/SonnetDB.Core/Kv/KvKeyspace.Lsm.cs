using System.Text;
using SonnetDB.Exceptions;

namespace SonnetDB.Kv;

/// <summary>
/// <see cref="KvKeyspace"/> 在 <see cref="KvStorageEngine.Lsm"/> 模式下的方法实现。
/// 每个公开/内部入口在完成与 legacy 相同的参数校验后转到这里；合同差异见 <see cref="KvLsmKeyspaceBackend"/>。
/// </summary>
public sealed partial class KvKeyspace
{
    private void ThrowIfDisposedForLsm()
    {
        lock (_sync)
            ThrowIfDisposed();
    }

    // ───────────────────────────── 单键写入 ─────────────────────────────

    private long LsmPut(byte[] key, byte[] value, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        return _lsm!.Execute(() =>
        {
            _lsm.Engine.WaitForWriteBudgetUnderLock(cancellationToken);
            return _lsm.CommitLocked([KvBatchMutation.Put(key, value, expiresAtUtc)], [], null, cancellationToken);
        }, cancellationToken);
    }

    private KvSetResult LsmSet(byte[] key, byte[] value, KvSetCondition condition, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken)
    {
        if (condition == KvSetCondition.Always)
            return new KvSetResult(true, LsmPut(key, value, expiresAtUtc, cancellationToken));

        KvBatchPrecondition precondition = condition == KvSetCondition.IfNotExists
            ? KvBatchPrecondition.KeyVersion(key, expectedVersion: 0)
            : KvBatchPrecondition.Exists(key);
        KvConditionalBatchResult result = LsmApplyConditional(
            [KvBatchMutation.Put(key, value, expiresAtUtc)], [precondition], cancellationToken);
        return new KvSetResult(result.Applied, result.Applied ? result.Sequence : null);
    }

    private KvExchangeResult LsmGetAndSet(byte[] key, byte[] value, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        return _lsm!.Execute(() =>
        {
            _lsm.Engine.WaitForWriteBudgetUnderLock(cancellationToken);
            KvValueEntry? previous = Visible(_lsm.GetRawLocked(key, CancellationToken.None), DateTimeOffset.UtcNow);
            long version = _lsm.CommitLocked([KvBatchMutation.Put(key, value, expiresAtUtc)], [], null, cancellationToken);
            return new KvExchangeResult(previous is null ? null : CreateEntryCopy(key, previous), version);
        }, cancellationToken);
    }

    private KvExchangeResult LsmGetAndDelete(byte[] key, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        return _lsm!.Execute(() =>
        {
            _lsm.Engine.WaitForWriteBudgetUnderLock(cancellationToken);
            KvValueEntry? previous = Visible(_lsm.GetRawLocked(key, CancellationToken.None), DateTimeOffset.UtcNow);
            cancellationToken.ThrowIfCancellationRequested();
            if (previous is null)
                return new KvExchangeResult(null, null);
            // 与 legacy 一致：删除前的基础查找钩子之后仍是可取消点（尚未追加 WAL）。
            BaseLookupTestHook?.Invoke();
            long version = _lsm.CommitLocked([KvBatchMutation.Delete(key)], [], null, cancellationToken);
            return new KvExchangeResult(CreateEntryCopy(key, previous), version);
        }, cancellationToken);
    }

    private (long Value, long Version) LsmIncrement(byte[] key, long delta)
    {
        ThrowIfDisposedForLsm();
        return _lsm!.Execute(() =>
        {
            _lsm.Engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
            KvValueEntry? entry = Visible(_lsm.GetRawLocked(key, CancellationToken.None), DateTimeOffset.UtcNow);
            long current = entry is null ? 0 : KvLsmKeyspaceBackend.ParseInteger(entry.Value);
            long next = checked(current + delta);
            byte[] value = Encoding.UTF8.GetBytes(next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            long version = _lsm.CommitLocked([KvBatchMutation.Put(key, value, entry?.ExpiresAtUtc)], [], null, CancellationToken.None);
            return (next, version);
        }, CancellationToken.None);
    }

    private KvCasResult LsmCompareAndSet(byte[] key, long expectedVersion, byte[] value, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        return _lsm!.Execute(() =>
        {
            _lsm.Engine.WaitForWriteBudgetUnderLock(cancellationToken);
            KvValueEntry? entry = Visible(_lsm.GetRawLocked(key, CancellationToken.None), DateTimeOffset.UtcNow);
            cancellationToken.ThrowIfCancellationRequested();
            long currentVersion = entry?.Version ?? 0;
            if (currentVersion != expectedVersion)
                return new KvCasResult(false, currentVersion, null);
            long newVersion = _lsm.CommitLocked([KvBatchMutation.Put(key, value, expiresAtUtc)], [], null, cancellationToken);
            return new KvCasResult(true, currentVersion, newVersion);
        }, cancellationToken);
    }

    private bool LsmRewriteTtl(byte[] key, DateTimeOffset? expiresAtUtc, bool requireExistingTtl, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        return _lsm!.Execute(() =>
        {
            _lsm.Engine.WaitForWriteBudgetUnderLock(cancellationToken);
            KvValueEntry? entry = Visible(_lsm.GetRawLocked(key, CancellationToken.None), DateTimeOffset.UtcNow);
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || (requireExistingTtl && !entry.ExpiresAtUtc.HasValue))
                return false;
            _lsm.CommitLocked([KvBatchMutation.Put(key, entry.Value.ToArray(), expiresAtUtc)], [], null, cancellationToken);
            return true;
        }, cancellationToken);
    }

    private bool LsmDelete(byte[] key)
    {
        ThrowIfDisposedForLsm();
        return _lsm!.Execute(() =>
        {
            _lsm.Engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
            if (Visible(_lsm.GetRawLocked(key, CancellationToken.None), DateTimeOffset.UtcNow) is null)
                return false;
            _lsm.CommitLocked([KvBatchMutation.Delete(key)], [], null, CancellationToken.None);
            return true;
        }, CancellationToken.None);
    }

    private KvTtlResult LsmGetTimeToLive(byte[] key, DateTimeOffset now)
    {
        ThrowIfDisposedForLsm();
        KvValueEntry? entry = _lsm!.GetRaw(key, CancellationToken.None);
        if (entry is null || entry.IsExpired(now))
            return new KvTtlResult(KvTtlResult.Missing, null);
        if (!entry.ExpiresAtUtc.HasValue)
            return new KvTtlResult(KvTtlResult.NoExpiration, null);
        long remaining = Math.Max(0, (long)Math.Ceiling((entry.ExpiresAtUtc.Value - now).TotalMilliseconds));
        return new KvTtlResult(remaining, entry.ExpiresAtUtc);
    }

    // ───────────────────────────── 批量写入 ─────────────────────────────

    /// <summary>
    /// 与 legacy 的单批次上限合同对齐：批次 WAL 记录本身装不进空白 WAL 预算（<see cref="KvOptions.MaxWalBytes"/>）时，
    /// 在追加前抛出 <see cref="KvAtomicBatchErrors.CreateTooLarge"/>。LSM 没有覆盖层条目预算，因此不检查条目数。
    /// </summary>
    private void LsmRejectOversizedBatch(IReadOnlyList<KvBatchMutation> batch, string batchName)
    {
        if (!_options.AutoCheckpointEnabled || batch.Count == 0)
            return;
        long maxWalBytes = _lsm!.IndexRebuildScopeActive ? _options.IndexRebuildMaxWalBytes : _options.MaxWalBytes;
        if (maxWalBytes <= 0)
            return;
        long required = KvWalFile.CalculateMutationBatchRecordBytes(batch);
        if (required > maxWalBytes - KvWalFile.HeaderSize)
        {
            SonnetDB.Diagnostics.SonnetDbMeter.KvCheckpointWriteRejections.Add(1);
            throw KvAtomicBatchErrors.CreateTooLarge(
                $"KV {batchName} was rejected before WAL append because the batch itself exceeds the fresh checkpoint budget; " +
                "reduce the batch size or increase/disable MaxWalBytes.");
        }
    }

    private long LsmApplyBatch(KvBatchMutation[] batch, CancellationToken cancellationToken, bool bounded)
    {
        ThrowIfDisposedForLsm();
        if (batch.Length == 0)
            return _lsm!.LastSequence;
        LsmRejectOversizedBatch(batch, "atomic mutation batch");
        return _lsm!.Execute(() =>
        {
            _lsm.Engine.WaitForWriteBudgetUnderLock(cancellationToken);
            return _lsm.CommitLocked(batch, [], null, cancellationToken);
        }, cancellationToken, bounded);
    }

    /// <summary>
    /// 索引恢复批次：与 legacy 相同，单页超过空白 WAL 预算时按条目数二分提交，返回最后一个子批次的 sequence；
    /// 单条 mutation 仍超限时抛出 TooLarge。
    /// </summary>
    private long LsmApplyIndexRebuildBatch(KvBatchMutation[] batch, CancellationToken cancellationToken, bool bounded)
    {
        try
        {
            return LsmApplyBatch(batch, cancellationToken, bounded);
        }
        catch (IOException exception) when (KvAtomicBatchErrors.IsTooLarge(exception) && batch.Length > 1)
        {
            int midpoint = batch.Length / 2;
            _ = LsmApplyIndexRebuildBatch(batch[..midpoint], cancellationToken, bounded);
            return LsmApplyIndexRebuildBatch(batch[midpoint..], cancellationToken, bounded);
        }
    }

    private KvConditionalBatchResult LsmApplyConditional(
        IReadOnlyList<KvBatchMutation> batch,
        IReadOnlyList<KvBatchPrecondition> conditions,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        LsmRejectOversizedBatch(batch, "conditional atomic mutation batch");
        return _lsm!.Execute(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 先等待预算（可能暂时释放写锁），再在写锁内评估条件并提交，二者之间不会插入其他提交。
            _lsm.Engine.WaitForWriteBudgetUnderLock(cancellationToken);
            int failed = _lsm.FindFailedPreconditionLocked(conditions, DateTimeOffset.UtcNow);
            if (failed >= 0)
                return KvConditionalBatchResult.Conflict(_lsm.LastSequence, failed);
            if (batch.Count == 0)
                return KvConditionalBatchResult.Success(_lsm.LastSequence);
            return KvConditionalBatchResult.Success(_lsm.CommitLocked(batch, [], null, cancellationToken));
        }, cancellationToken);
    }

    private long LsmApplySequenceFactory(Func<long, IReadOnlyList<KvBatchMutation>> mutationFactory)
    {
        ThrowIfDisposedForLsm();
        int invalidations = 0;
        while (true)
        {
            long expected = _lsm!.Engine.NextSequence;
            KvBatchMutation[] batch;
            if (invalidations < MaxOptimisticSequenceFactoryInvalidations)
            {
                batch = CanonicalizeBatchMutations(
                    mutationFactory(expected)
                    ?? throw new InvalidOperationException("KV mutation factory 不能返回 null。"));
                LsmRejectOversizedBatch(batch, "atomic mutation batch");
                long? committed = _lsm.Execute<long?>(() =>
                {
                    _lsm.Engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
                    if (_lsm.Engine.NextSequence != expected)
                        return null;
                    return batch.Length == 0
                        ? _lsm.LastSequence
                        : _lsm.CommitLocked(batch, [], expected, CancellationToken.None);
                }, CancellationToken.None);
                if (committed is long sequence)
                    return sequence;
                invalidations++;
                continue;
            }

            // 连续竞争后在写锁内构造，避免大批次在繁忙 keyspace 中饥饿。
            return _lsm.Execute(() =>
            {
                _lsm.Engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
                long next = _lsm.Engine.NextSequence;
                KvBatchMutation[] locked = CanonicalizeBatchMutations(
                    mutationFactory(next)
                    ?? throw new InvalidOperationException("KV mutation factory 不能返回 null。"));
                LsmRejectOversizedBatch(locked, "atomic mutation batch");
                return locked.Length == 0
                    ? _lsm.LastSequence
                    : _lsm.CommitLocked(locked, [], next, CancellationToken.None);
            }, CancellationToken.None);
        }
    }

    private int LsmDeleteMany(IReadOnlyList<byte[]> keys)
    {
        ThrowIfDisposedForLsm();
        var unique = new HashSet<byte[]>(KvKeyComparer.Instance);
        var candidates = new List<byte[]>(keys.Count);
        foreach (byte[] key in keys)
        {
            ArgumentNullException.ThrowIfNull(key);
            ValidateKey(key, _options);
            if (unique.Add(key))
                candidates.Add(key.ToArray());
        }

        return _lsm!.Execute(() =>
        {
            _lsm.Engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var deletes = new List<KvBatchMutation>(candidates.Count);
            int removed = 0;
            foreach (byte[] key in candidates)
            {
                KvValueEntry? entry = _lsm.GetRawLocked(key, CancellationToken.None);
                if (entry is null)
                    continue;
                if (!entry.IsExpired(now))
                    removed++;
                deletes.Add(KvBatchMutation.Delete(key));
            }
            if (deletes.Count > 0)
                _lsm.CommitLocked(deletes, [], null, CancellationToken.None);
            return removed;
        }, CancellationToken.None);
    }

    private int LsmDeletePrefix(byte[] prefix, int take)
    {
        ThrowIfDisposedForLsm();
        return _lsm!.Execute(() =>
        {
            _lsm.Engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
            var deletes = new List<KvBatchMutation>();
            foreach (KeyValuePair<byte[], KvValueEntry> pair in _lsm.EnumerateRaw(prefix, null, null, null, CancellationToken.None))
            {
                deletes.Add(KvBatchMutation.Delete(pair.Key));
                if (deletes.Count >= take)
                    break;
            }
            if (deletes.Count > 0)
                _lsm.CommitLocked(deletes, [], null, CancellationToken.None);
            return deletes.Count;
        }, CancellationToken.None);
    }

    private int LsmCleanExpired(DateTimeOffset now, int take)
    {
        ThrowIfDisposedForLsm();
        return _lsm!.Execute(() =>
        {
            _lsm.Engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
            var deletes = new List<KvBatchMutation>();
            foreach (KeyValuePair<byte[], KvValueEntry> pair in _lsm.EnumerateRaw([], null, null, null, CancellationToken.None))
            {
                if (!pair.Value.IsExpired(now))
                    continue;
                deletes.Add(KvBatchMutation.Delete(pair.Key));
                if (deletes.Count >= take)
                    break;
            }
            if (deletes.Count > 0)
                _lsm.CommitLocked(deletes, [], null, CancellationToken.None);
            return deletes.Count;
        }, CancellationToken.None);
    }

    private int LsmCleanExpiredMaintenance(DateTimeOffset utcNow, int maximumDeletes, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        return _lsm!.Execute(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 后台维护不等待背压，下一轮继续。
            if (_lsm.Engine.IsWriteStalledUnderLock)
                return 0;

            var deletes = new List<KvBatchMutation>(Math.Min(maximumDeletes, 1024));
            byte[]? lastKey = null;
            int visited = 0;
            bool exhausted = true;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            foreach (KeyValuePair<byte[], KvValueEntry> pair in _lsm.EnumerateRaw([], null, null, _lsm.ExpirationMaintenanceCursor, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastKey = pair.Key;
                visited++;
                if (pair.Value.IsExpired(utcNow))
                    deletes.Add(KvBatchMutation.Delete(pair.Key));
                if (visited >= 4096 || deletes.Count >= maximumDeletes || elapsed.Elapsed >= TimeSpan.FromMilliseconds(50))
                {
                    exhausted = false;
                    break;
                }
            }

            if (deletes.Count > 0)
                _lsm.CommitLocked(deletes, [], null, CancellationToken.None);
            _lsm.ExpirationMaintenanceCursor = exhausted ? null : lastKey;
            return deletes.Count;
        }, cancellationToken, bounded: true);
    }

    // ───────────────────────────── 读取 ─────────────────────────────

    private KvValueEntry? LsmReadVisible(byte[] key, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        cancellationToken.ThrowIfCancellationRequested();
        return _lsm!.GetVisible(key, DateTimeOffset.UtcNow, cancellationToken);
    }

    private IReadOnlyList<KvEntry> LsmScan(
        byte[] prefix, byte[]? startInclusive, byte[]? endExclusive, byte[]? afterKey,
        int take, CancellationToken cancellationToken, Action? candidateVisited)
    {
        ThrowIfDisposedForLsm();
        var rows = new List<KvEntry>(Math.Min(take, ScanResultInitialCapacity));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<byte[], KvValueEntry> pair in _lsm!.EnumerateRaw(prefix, startInclusive, endExclusive, afterKey, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateVisited?.Invoke();
            if (pair.Value.IsExpired(now))
                continue;
            rows.Add(new KvEntry(pair.Key, pair.Value.Value, pair.Value.Version, pair.Value.ExpiresAtUtc));
            if (rows.Count >= take)
                break;
        }
        return rows;
    }

    private IReadOnlyList<byte[]> LsmScanKeys(byte[] prefix, byte[]? afterKey, int limit)
    {
        ThrowIfDisposedForLsm();
        var keys = new List<byte[]>(Math.Min(limit, ScanResultInitialCapacity));
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        // 只复制 key：value 留在块/arena 中，不随维护扫描物化。
        _lsm!.ScanForward(prefix, afterKey, CancellationToken.None, iterator =>
        {
            long expires = iterator.ExpiresAtUtcTicks;
            if (expires == 0 || expires > nowTicks)
                keys.Add(iterator.Key.ToArray());
            return keys.Count < limit;
        });
        return keys;
    }

    private void LsmReadStablePrefixPages(byte[] prefix, int pageSize, Action<IReadOnlyList<KvEntry>> readPage)
    {
        ThrowIfDisposedForLsm();
        StablePrefixScanTestHook?.Invoke(prefix);
        // 快照提供与 legacy 相同的稳定视图；LSM 不在回调期间阻塞写入（回调合同本就禁止写入）。
        // 每个 value 只复制一次（直接交给回调页，不经过中间物化）。
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var page = new List<KvEntry>(pageSize);
        _lsm!.ScanForward(prefix, null, CancellationToken.None, iterator =>
        {
            long expires = iterator.ExpiresAtUtcTicks;
            if (expires != 0 && expires <= nowTicks)
                return true;
            page.Add(new KvEntry(
                iterator.Key.ToArray(),
                iterator.Value.ToArray(),
                iterator.Sequence,
                expires == 0 ? null : new DateTimeOffset(expires, TimeSpan.Zero)));
            if (page.Count < pageSize)
                return true;
            readPage(page);
            page = new List<KvEntry>(pageSize);
            return true;
        });
        if (page.Count > 0)
            readPage(page);
    }

    private int LsmCountPrefix(byte[] prefix)
    {
        ThrowIfDisposedForLsm();
        int count = 0;
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        _lsm!.ScanForward(prefix, null, CancellationToken.None, iterator =>
        {
            long expires = iterator.ExpiresAtUtcTicks;
            if (expires == 0 || expires > nowTicks)
                count++;
            return true;
        });
        return count;
    }

    private KvExpirationStats LsmGetExpirationStats(DateTimeOffset now)
    {
        ThrowIfDisposedForLsm();
        int expired = 0;
        int expiring = 0;
        int total = 0;
        DateTimeOffset? nearest = null;
        foreach (KeyValuePair<byte[], KvValueEntry> pair in _lsm!.EnumerateRaw([], null, null, null, CancellationToken.None))
        {
            KvValueEntry entry = pair.Value;
            total++;
            if (!entry.ExpiresAtUtc.HasValue)
                continue;
            expiring++;
            if (entry.IsExpired(now))
            {
                expired++;
                continue;
            }
            nearest = nearest is null || entry.ExpiresAtUtc.Value < nearest.Value ? entry.ExpiresAtUtc : nearest;
        }
        return new KvExpirationStats(total, total - expired, expired, expiring, nearest);
    }

    private KvEntry? LsmGetEntryBounded(byte[] key, int maxValueBytes, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        cancellationToken.ThrowIfCancellationRequested();
        return _lsm!.GetVisibleBounded(key, maxValueBytes, DateTimeOffset.UtcNow, cancellationToken);
    }

    private KvBoundedReadPage LsmScanPrefixBounded(
        byte[] prefix, byte[]? afterKey, int maxEntries, int maxValueBytes, int maxPageBytes,
        int maxCandidates, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        var rows = new List<KvEntry>(Math.Min(maxEntries, 256));
        long used = 0;
        byte[]? consumed = afterKey;
        int candidates = 0;
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        KvBoundedReadPage? truncated = null;
        _lsm!.ScanForward(prefix, afterKey, cancellationToken, iterator =>
        {
            if (candidates++ >= maxCandidates)
            {
                truncated = new(rows, true, consumed?.ToArray());
                return false;
            }
            long expires = iterator.ExpiresAtUtcTicks;
            if (expires != 0 && expires <= nowTicks)
            {
                consumed = iterator.Key.ToArray();
                return true;
            }
            if (rows.Count == maxEntries)
            {
                truncated = new(rows, true, consumed?.ToArray());
                return false;
            }
            // 先按长度计费，超限时不复制 value。
            int length = iterator.Value.Length;
            long remaining = maxPageBytes - used;
            if (length > maxValueBytes || (long)iterator.Key.Length + length > remaining)
            {
                if (rows.Count == 0)
                    throw new PayloadLimitExceededException(Math.Min(maxValueBytes, remaining));
                truncated = new(rows, true, consumed?.ToArray());
                return false;
            }
            byte[] key = iterator.Key.ToArray();
            consumed = key;
            used += (long)key.Length + length;
            rows.Add(new KvEntry(
                key,
                iterator.Value.ToArray(),
                iterator.Sequence,
                expires == 0 ? null : new DateTimeOffset(expires, TimeSpan.Zero)));
            return true;
        });
        return truncated ?? new(rows, false, null);
    }

    private KvCandidatePage LsmScanCandidatePage(
        byte[] prefix, byte[]? endExclusive, byte[]? afterKey,
        int resultLimit, int candidateLimit, TimeSpan timeBudget, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        // LSM 视图不暴露已删除 key：候选只计入仍存在的 key（含已到期），续扫位置为最后检查的 key。
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var rows = new List<KvEntry>(Math.Min(resultLimit, ScanResultInitialCapacity));
        byte[]? continuation = afterKey;
        int visited = 0;
        bool hasMore = false;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using IEnumerator<KeyValuePair<byte[], KvValueEntry>> enumerator =
            _lsm!.EnumerateRaw(prefix, null, endExclusive, afterKey, cancellationToken).GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited >= candidateLimit || (visited > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(started) >= timeBudget))
            {
                hasMore = enumerator.MoveNext();
                break;
            }
            if (!enumerator.MoveNext())
                break;
            KeyValuePair<byte[], KvValueEntry> pair = enumerator.Current;
            visited++;
            continuation = pair.Key;
            if (!pair.Value.IsExpired(now))
                rows.Add(new KvEntry(pair.Key, pair.Value.Value, pair.Value.Version, pair.Value.ExpiresAtUtc));
            if (rows.Count >= resultLimit)
            {
                hasMore = enumerator.MoveNext();
                break;
            }
        }
        return new KvCandidatePage(rows, continuation, hasMore, visited);
    }

    private void LsmReadSchemaMigrationPrefix(byte prefix, Action<int, int> reserve, Action<byte[], byte[]> consume, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        // 在复制 key/value 之前按长度预留预算（legacy 合同）。
        _lsm!.ScanForward([prefix], null, cancellationToken, iterator =>
        {
            long expires = iterator.ExpiresAtUtcTicks;
            if (expires != 0 && expires <= nowTicks)
                return true;
            reserve(iterator.Key.Length, iterator.Value.Length);
            consume(iterator.Key.ToArray(), iterator.Value.ToArray());
            return true;
        });
    }

    private byte[]? LsmReadSchemaMigrationValue(byte[] key, Action<int> reserve, CancellationToken cancellationToken)
    {
        ThrowIfDisposedForLsm();
        cancellationToken.ThrowIfCancellationRequested();
        KvEntry? entry;
        try
        {
            entry = _lsm!.GetVisibleBounded(key, int.MaxValue, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (PayloadLimitExceededException)
        {
            throw new System.Diagnostics.UnreachableException("int.MaxValue bound cannot be exceeded.");
        }
        if (entry is null)
            return null;
        reserve(entry.Value.Length);
        return entry.Value.ToArray();
    }

    private KvDiagnostics LsmGetDiagnostics(int topHotKeys, KvExpirationStats expiration)
    {
        (int mutable, int frozen, long walBytes) = _lsm!.DiagnosticsCounts();
        lock (_sync)
        {
            ThrowIfDisposed();
            var hotKeys = _readCounts
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, KvKeyComparer.Instance)
                .Take(topHotKeys)
                .Select(static pair => new KvHotKey(FormatKey(pair.Key), pair.Value))
                .ToArray();
            return new KvDiagnostics(
                expiration.TotalKeys,
                expiration.ActiveKeys,
                expiration.ExpiredKeys,
                expiration.ExpiringKeys,
                expiration.NearestExpiresAtUtc,
                mutable,
                frozen,
                walBytes,
                _options.MaxSnapshotOverlayEntries,
                hotKeys);
        }
    }

    private static KvValueEntry? Visible(KvValueEntry? entry, DateTimeOffset now)
        => entry is null || entry.IsExpired(now) ? null : entry;

    /// <summary>
    /// 关闭 LSM 实例：先关闭引擎（停止后台线程并关闭 WAL、SST 与 manifest），
    /// 再释放目录租约与读取预算引用，与 legacy 的释放顺序一致。首个失败按原类型重新抛出。
    /// </summary>
    private void DisposeLsm()
    {
        FileStream? lifecycleLease;
        IDisposable? budgetLease;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            lifecycleLease = _lifecycleLease;
            _lifecycleLease = null;
            budgetLease = _diskReadBudgetKeyspaceLease;
            _diskReadBudgetKeyspaceLease = null;
            Monitor.PulseAll(_sync);
        }

        Exception? failure = null;
        void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception) when (failure is null)
            {
                failure = exception;
            }
            catch (Exception)
            {
                // 保留首个失败，其余资源仍继续释放。
            }
        }

        Run(_lsm!.Dispose);
        Run(() => lifecycleLease?.Dispose());
        Run(() => budgetLease?.Dispose());
        if (_ownsDiskReadBudget)
            Run(_diskReadBudget.Dispose);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
