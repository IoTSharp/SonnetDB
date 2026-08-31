namespace SonnetDB.Sql.Execution;

/// <summary>把算子私有行类型映射到通用 SQL spill 行。</summary>
internal readonly record struct SqlSpillCodec<T>(
    Func<T, object?[]> Encode,
    Func<object?[], T> Decode,
    Func<T, long>? Estimate = null)
{
    /// <summary>估算行常驻内存；生产 codec 可直接读取原行，避免为了计费创建编码数组。</summary>
    internal long EstimateRowBytes(T row)
        => Estimate?.Invoke(row) ?? SqlSpillRowCodec.EstimateRowBytes(Encode(row));
}

/// <summary>常用 SQL 结果行的 spill 编解码器。</summary>
internal static class SqlSpillCodecs
{
    internal static SqlSpillCodec<IReadOnlyList<object?>> ReadOnlyRows { get; } = new(
        static row => row as object?[] ?? row.ToArray(),
        static row => row,
        static row => SqlSpillRowCodec.EstimateRowBytes(row));

    internal static SqlSpillCodec<object?[]> ArrayRows { get; } = new(
        static row => row,
        static row => row,
        static row => SqlSpillRowCodec.EstimateRowBytes(row));
}

/// <summary>预算感知的稳定外部归并排序。</summary>
internal static class SqlSpillSorter
{
    private static readonly SemaphoreSlim OversizedMergeGate = new(initialCount: 1, maxCount: 1);
    private static int _activeOversizedMergeCount;
    private static int _waitingOversizedMergeCount;
    private const int MergeFanIn = 32;
    // 与 SqlSpillRowCodec 的 64 KiB FileStream 缓冲保持一致，并覆盖 reader、游标和队列节点的小对象开销。
    private const long MergeCursorFixedBytes = (64L * 1024) + 512;
    // 中间归并还会同时持有一个 64 KiB 输出缓冲。
    private const long MergeWriterFixedBytes = (64L * 1024) + 512;
    private const int SortCancellationPollingInterval = 1024;
    // 32^13 大于 long 可表达的非负稳定序号数量，因此初始 run 不可能进位到第 14 层。
    private const int MaxMergeLevels = 13;
    internal const int MaxLiveRunFileCount = ((MergeFanIn - 1) * MaxMergeLevels) + 2;
    internal static int ActiveOversizedMergeCount => Volatile.Read(ref _activeOversizedMergeCount);
    internal static int WaitingOversizedMergeCount => Volatile.Read(ref _waitingOversizedMergeCount);
    private readonly record struct StableItem<T>(T Row, long Ordinal, long Bytes);
    private readonly record struct BudgetedStableItem<T>(T Row, long Ordinal, long Bytes);
    private readonly record struct RunInfo(string Path, long MaxResidentRowBytes);

    /// <summary>同时持有归并预算和可选 oversized gate，保证所有退出路径按同一顺序释放资源。</summary>
    private sealed class MergeMemoryLease : IDisposable
    {
        private SqlQueryResources.SqlOperatorMemoryReservation? _reservation;
        private int _ownsOversizedGate;

        internal MergeMemoryLease(
            SqlQueryResources.SqlOperatorMemoryReservation reservation,
            bool ownsOversizedGate)
        {
            _reservation = reservation;
            _ownsOversizedGate = ownsOversizedGate ? 1 : 0;
        }

        public void Dispose()
        {
            try
            {
                Interlocked.Exchange(ref _reservation, null)?.Dispose();
            }
            finally
            {
                if (Interlocked.Exchange(ref _ownsOversizedGate, 0) != 0)
                    ReleaseOversizedMergeGate();
            }
        }
    }

    /// <summary>
    /// 在查询内存预算内完成稳定排序，并在外排时逐行归并输出，避免重新物化完整排序结果。
    /// </summary>
    internal static IEnumerable<T> Order<T>(
        IEnumerable<T> rows,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(comparer);
        return EnumerateOrdered(rows, comparer, codec);
    }

    /// <summary>
    /// 生成预算感知的有序行；每个内存 run 达到预算后立即落盘，最终归并只保留固定扇入的游标。
    /// </summary>
    private static IEnumerable<T> EnumerateOrdered<T>(
        IEnumerable<T> rows,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        SqlQueryResources resources = SqlQueryResources.Current
            ?? throw new InvalidOperationException("外部排序要求活动 SQL 查询资源作用域。");
        using var reservation = resources.CreateReservation();
        var buffer = new List<StableItem<T>>();
        var runs = new IncrementalRunSet<T>(resources, comparer, codec);
        long ordinal = 0;
        foreach (T row in rows)
        {
            resources.ThrowIfCancellationRequested();
            long bytes = checked(codec.EstimateRowBytes(row) + 32);
            if (!reservation.TryReserve(bytes))
            {
                if (buffer.Count != 0)
                {
                    RunInfo completedRun = WriteRun(resources, buffer, comparer, codec);
                    ClearRunBuffer(buffer);
                    reservation.ReleaseAll();
                    runs.Add(completedRun);
                }

                // 单行可能大于预算；该行独立成 run，确保内存中不会继续累积同值组的大对象。
                if (!reservation.TryReserve(bytes))
                {
                    buffer.Add(new StableItem<T>(row, TakeOrdinal(ref ordinal), bytes));
                    RunInfo oversizedRun = WriteRun(resources, buffer, comparer, codec);
                    ClearRunBuffer(buffer);
                    runs.Add(oversizedRun);
                    continue;
                }
            }
            buffer.Add(new StableItem<T>(row, TakeOrdinal(ref ordinal), bytes));
        }

        if (runs.Count == 0)
        {
            SortWithCancellation(
                buffer,
                (left, right) => CompareStable(left, right, comparer),
                resources);
            foreach (StableItem<T> item in buffer)
                yield return item.Row;
            yield break;
        }

        if (buffer.Count != 0)
        {
            RunInfo completedRun = WriteRun(resources, buffer, comparer, codec);
            ClearRunBuffer(buffer);
            reservation.ReleaseAll();
            runs.Add(completedRun);
        }
        reservation.ReleaseAll();
        IReadOnlyList<RunInfo> consolidated = runs.Complete();
        foreach (T row in MergeOrdered(resources, consolidated, comparer, codec))
            yield return row;
    }

    internal static T[] OrderByThenPaginate<T>(
        IEnumerable<T> rows,
        IComparer<T> comparer,
        int offset,
        int? fetch,
        SqlSpillCodec<T> codec)
    {
        SqlQueryResources resources = SqlQueryResources.Current
            ?? throw new InvalidOperationException("外部排序要求活动 SQL 查询资源作用域。");
        if (offset < 0)
            offset = 0;
        if (fetch is <= 0)
            return [];

        if (fetch is int take)
        {
            long neededLong = (long)offset + take;
            int needed = neededLong > int.MaxValue ? int.MaxValue : (int)neededLong;
            if (needed <= 0)
                return [];
            return OrderBoundedTopN(rows, comparer, offset, take, needed, codec, resources);
        }

        using var reservation = resources.CreateReservation();
        var buffer = new List<StableItem<T>>();
        var runs = new IncrementalRunSet<T>(resources, comparer, codec);
        long ordinal = 0;
        foreach (T row in rows)
        {
            resources.ThrowIfCancellationRequested();
            long bytes = checked(codec.EstimateRowBytes(row) + 32);
            if (!reservation.TryReserve(bytes))
            {
                if (buffer.Count != 0)
                {
                    RunInfo completedRun = WriteRun(resources, buffer, comparer, codec);
                    ClearRunBuffer(buffer);
                    reservation.ReleaseAll();
                    runs.Add(completedRun);
                }

                if (!reservation.TryReserve(bytes))
                {
                    buffer.Add(new StableItem<T>(row, TakeOrdinal(ref ordinal), bytes));
                    RunInfo oversizedRun = WriteRun(resources, buffer, comparer, codec);
                    ClearRunBuffer(buffer);
                    runs.Add(oversizedRun);
                    continue;
                }
            }
            buffer.Add(new StableItem<T>(row, TakeOrdinal(ref ordinal), bytes));
        }

        if (runs.Count == 0)
            return SortAndPage(buffer, comparer, offset, fetch, resources);

        if (buffer.Count != 0)
        {
            RunInfo completedRun = WriteRun(resources, buffer, comparer, codec);
            ClearRunBuffer(buffer);
            reservation.ReleaseAll();
            runs.Add(completedRun);
        }
        reservation.ReleaseAll();
        IReadOnlyList<RunInfo> consolidated = runs.Complete();
        return MergeAndPage(resources, consolidated, comparer, offset, fetch, codec);
    }

    /// <summary>
    /// 在预算内只保留排序前缀；预算不足时把仍可能进入前缀的行转入外排，已淘汰行无需重新加入。
    /// </summary>
    private static T[] OrderBoundedTopN<T>(
        IEnumerable<T> rows,
        IComparer<T> comparer,
        int offset,
        int take,
        int needed,
        SqlSpillCodec<T> codec,
        SqlQueryResources resources)
    {
        using IEnumerator<T> enumerator = rows.GetEnumerator();
        using var reservation = resources.CreateReservation();
        // 从单元素容量起步，使每次倍增产生的结构空间可由逐行预算保守覆盖。
        var heap = new List<BudgetedStableItem<T>>(1);
        long nextOrdinal = 0;
        long accountedBytes = 0;
        long reservedBytes = 0;

        while (enumerator.MoveNext())
        {
            resources.ThrowIfCancellationRequested();
            var candidate = new StableItem<T>(enumerator.Current, TakeOrdinal(ref nextOrdinal), Bytes: 0);
            if (heap.Count >= needed
                && CompareStable(candidate, heap[0], comparer) >= 0)
            {
                continue;
            }

            long candidateBytes = checked(codec.EstimateRowBytes(candidate.Row) + 48);
            candidate = candidate with { Bytes = candidateBytes };
            long nextAccountedBytes = heap.Count < needed
                ? checked(accountedBytes + candidateBytes)
                : checked(accountedBytes - heap[0].Bytes + candidateBytes);
            long additionalReservation = Math.Max(0, nextAccountedBytes - reservedBytes);
            if (additionalReservation > 0 && !reservation.TryReserve(additionalReservation))
            {
                return SpillBoundedTopNFallback(
                    heap,
                    candidate,
                    candidateBytes,
                    needed,
                    enumerator,
                    ref nextOrdinal,
                    comparer,
                    offset,
                    take,
                    codec,
                    resources,
                    reservation);
            }

            long nextReservedBytes = checked(reservedBytes + additionalReservation);
            var retained = new BudgetedStableItem<T>(candidate.Row, candidate.Ordinal, candidateBytes);
            if (heap.Count < needed)
            {
                heap.Add(retained);
                SiftUp(heap, heap.Count - 1, comparer);
            }
            else
            {
                heap[0] = retained;
                SiftDown(heap, 0, comparer);
            }
            if (nextReservedBytes > nextAccountedBytes)
            {
                reservation.Release(nextReservedBytes - nextAccountedBytes);
                nextReservedBytes = nextAccountedBytes;
            }
            reservedBytes = nextReservedBytes;
            accountedBytes = nextAccountedBytes;
        }

        return SortAndPage(heap, comparer, offset, take, resources);
    }

    /// <summary>
    /// 预算不足后以当前稳定前缀为首个 run，并外排剩余输入；此前淘汰项不可能重新进入最终前缀。
    /// </summary>
    private static T[] SpillBoundedTopNFallback<T>(
        List<BudgetedStableItem<T>> heap,
        StableItem<T> candidate,
        long candidateBytes,
        int needed,
        IEnumerator<T> remaining,
        ref long nextOrdinal,
        IComparer<T> comparer,
        int offset,
        int take,
        SqlSpillCodec<T> codec,
        SqlQueryResources resources,
        SqlQueryResources.SqlOperatorMemoryReservation reservation)
    {
        var runs = new IncrementalRunSet<T>(resources, comparer, codec);
        if (heap.Count == needed)
        {
            // 堆已满且候选优于堆顶，原堆顶不可能再进入最终 K 行，可直接替换后落盘。
            heap[0] = new BudgetedStableItem<T>(candidate.Row, candidate.Ordinal, candidateBytes);
            runs.Add(WriteRun(resources, heap, comparer, codec));
        }
        else
        {
            if (heap.Count != 0)
                runs.Add(WriteRun(resources, heap, comparer, codec));
            runs.Add(WriteSingleRun(resources, candidate, codec));
        }

        // 归还预算前先解除 K 级 backing array，避免并发查询复用账面预算时出现真实堆内存超配。
        candidate = default;
        heap.Clear();
        heap.Capacity = 0;
        reservation.ReleaseAll();

        var buffer = new List<StableItem<T>>();
        while (remaining.MoveNext())
        {
            resources.ThrowIfCancellationRequested();
            T row = remaining.Current;
            long bytes = checked(codec.EstimateRowBytes(row) + 32);
            if (!reservation.TryReserve(bytes))
            {
                if (buffer.Count != 0)
                {
                    RunInfo completedRun = WriteRun(resources, buffer, comparer, codec);
                    ClearRunBuffer(buffer);
                    reservation.ReleaseAll();
                    runs.Add(completedRun);
                }

                if (!reservation.TryReserve(bytes))
                {
                    buffer.Add(new StableItem<T>(row, TakeOrdinal(ref nextOrdinal), bytes));
                    RunInfo oversizedRun = WriteRun(resources, buffer, comparer, codec);
                    ClearRunBuffer(buffer);
                    runs.Add(oversizedRun);
                    continue;
                }
            }
            buffer.Add(new StableItem<T>(row, TakeOrdinal(ref nextOrdinal), bytes));
        }

        if (buffer.Count != 0)
        {
            RunInfo completedRun = WriteRun(resources, buffer, comparer, codec);
            ClearRunBuffer(buffer);
            reservation.ReleaseAll();
            runs.Add(completedRun);
        }
        reservation.ReleaseAll();
        return MergeAndPage(resources, runs.Complete(), comparer, offset, take, codec);
    }

    /// <summary>把预算内稳定最大堆排序后应用 OFFSET/FETCH。</summary>
    private static T[] SortAndPage<T>(
        List<BudgetedStableItem<T>> items,
        IComparer<T> comparer,
        int offset,
        int take,
        SqlQueryResources resources)
    {
        SortWithCancellation(
            items,
            (left, right) => CompareStable(left, right, comparer),
            resources);
        if (offset >= items.Count)
            return [];
        int count = Math.Min(take, items.Count - offset);
        var result = new T[count];
        for (int index = 0; index < count; index++)
            result[index] = items[offset + index].Row;
        return result;
    }

    /// <summary>把新保留项上浮，维持堆顶为当前排序前缀中的最大项。</summary>
    private static void SiftUp<T>(
        List<BudgetedStableItem<T>> heap,
        int index,
        IComparer<T> comparer)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (CompareStable(heap[index], heap[parent], comparer) <= 0)
                break;
            (heap[index], heap[parent]) = (heap[parent], heap[index]);
            index = parent;
        }
    }

    /// <summary>替换堆顶后向下调整，维持稳定最大堆。</summary>
    private static void SiftDown<T>(
        List<BudgetedStableItem<T>> heap,
        int index,
        IComparer<T> comparer)
    {
        while (true)
        {
            int left = (2 * index) + 1;
            int right = left + 1;
            int largest = index;
            if (left < heap.Count && CompareStable(heap[left], heap[largest], comparer) > 0)
                largest = left;
            if (right < heap.Count && CompareStable(heap[right], heap[largest], comparer) > 0)
                largest = right;
            if (largest == index)
                return;
            (heap[index], heap[largest]) = (heap[largest], heap[index]);
            index = largest;
        }
    }

    /// <summary>把内存 run 写入工作区，并携带后续游标所需的最大行驻留估算。</summary>
    private static RunInfo WriteRun<T>(
        SqlQueryResources resources,
        List<StableItem<T>> items,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        SortWithCancellation(
            items,
            (left, right) => CompareStable(left, right, comparer),
            resources);
        string path = resources.GetWorkspace().CreateFilePath("sort-run");
        long maxResidentRowBytes = 0;
        using (BinaryWriter writer = SqlSpillRowCodec.CreateWriter(path))
        {
            writer.Write((long)items.Count);
            foreach (StableItem<T> item in items)
            {
                resources.ThrowIfCancellationRequested();
                maxResidentRowBytes = Math.Max(maxResidentRowBytes, item.Bytes);
                writer.Write(item.Ordinal);
                SqlSpillRowCodec.WriteRow(writer, codec.Encode(item.Row));
            }
        }
        SqlExecutionTelemetry.RecordSpill(new FileInfo(path).Length);
        return new RunInfo(path, maxResidentRowBytes);
    }

    /// <summary>把预算堆原地排序并写成 run，避免回退外排时复制整个 K 行前缀。</summary>
    private static RunInfo WriteRun<T>(
        SqlQueryResources resources,
        List<BudgetedStableItem<T>> items,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        SortWithCancellation(
            items,
            (left, right) => CompareStable(left, right, comparer),
            resources);
        string path = resources.GetWorkspace().CreateFilePath("sort-run");
        long maxResidentRowBytes = 0;
        using (BinaryWriter writer = SqlSpillRowCodec.CreateWriter(path))
        {
            writer.Write((long)items.Count);
            foreach (BudgetedStableItem<T> item in items)
            {
                resources.ThrowIfCancellationRequested();
                maxResidentRowBytes = Math.Max(maxResidentRowBytes, item.Bytes);
                writer.Write(item.Ordinal);
                SqlSpillRowCodec.WriteRow(writer, codec.Encode(item.Row));
            }
        }
        SqlExecutionTelemetry.RecordSpill(new FileInfo(path).Length);
        return new RunInfo(path, maxResidentRowBytes);
    }

    /// <summary>把无法纳入预算的单行直接写成独立 run，不分配额外的 K 级列表。</summary>
    private static RunInfo WriteSingleRun<T>(
        SqlQueryResources resources,
        StableItem<T> item,
        SqlSpillCodec<T> codec)
    {
        resources.ThrowIfCancellationRequested();
        string path = resources.GetWorkspace().CreateFilePath("sort-run");
        using (BinaryWriter writer = SqlSpillRowCodec.CreateWriter(path))
        {
            writer.Write(1L);
            writer.Write(item.Ordinal);
            SqlSpillRowCodec.WriteRow(writer, codec.Encode(item.Row));
        }
        SqlExecutionTelemetry.RecordSpill(new FileInfo(path).Length);
        return new RunInfo(path, item.Bytes);
    }

    /// <summary>把保留 run 压缩到最终一次归并允许的 32 路以内。</summary>
    private static IReadOnlyList<RunInfo> ConsolidateRuns<T>(
        SqlQueryResources resources,
        IReadOnlyList<RunInfo> initialRuns,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        IReadOnlyList<RunInfo> current = initialRuns;
        while (current.Count > MergeFanIn)
        {
            var next = new List<RunInfo>((current.Count + MergeFanIn - 1) / MergeFanIn);
            for (int start = 0; start < current.Count; start += MergeFanIn)
            {
                int count = Math.Min(MergeFanIn, current.Count - start);
                if (count == 1)
                {
                    next.Add(current[start]);
                    continue;
                }
                var mergePaths = new RunInfo[count];
                for (int index = 0; index < count; index++)
                    mergePaths[index] = current[start + index];
                RunInfo merged = MergeRunsToSingle(
                    resources,
                    mergePaths,
                    comparer,
                    codec);
                next.Add(merged);
            }
            current = next;
        }
        return current;
    }

    /// <summary>
    /// 把最多 32 个 run 归并为一个；预算不足时递归缩小 fan-in，常规预算充足时仍保持一次 32 路归并。
    /// </summary>
    private static RunInfo MergeRunsToSingle<T>(
        SqlQueryResources resources,
        IReadOnlyList<RunInfo> paths,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        if (paths.Count is < 2 or > MergeFanIn)
            throw new ArgumentOutOfRangeException(nameof(paths), "SQL 排序归并输入必须在 2 到固定 fan-in 之间。");

        var current = new List<RunInfo>(paths);
        while (current.Count > 1)
        {
            var next = new List<RunInfo>((current.Count + 1) / 2);
            for (int start = 0; start < current.Count;)
            {
                int remaining = current.Count - start;
                if (remaining == 1)
                {
                    next.Add(current[start]);
                    break;
                }

                using MergeMemoryLease reservation = ReserveMergeGroup(
                    resources,
                    current,
                    start,
                    Math.Min(MergeFanIn, remaining),
                    includeWriter: true,
                    out int count);
                next.Add(MergeRunsToFileCore(
                    resources,
                    current,
                    start,
                    count,
                    comparer,
                    codec));
                start += count;
            }
            current = next;
        }
        return current[0];
    }

    /// <summary>在已取得的归并预算内写出一个中间 run；成功后才删除其输入文件。</summary>
    private static RunInfo MergeRunsToFileCore<T>(
        SqlQueryResources resources,
        IReadOnlyList<RunInfo> paths,
        int start,
        int count,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        if (count is < 2 or > MergeFanIn || start < 0 || start > paths.Count - count)
            throw new ArgumentOutOfRangeException(nameof(count), "SQL 排序归并分组范围非法。");
        string outputPath = resources.GetWorkspace().CreateFilePath("sort-merge");
        var priorityComparer = Comparer<StableItem<T>>.Create(
            (left, right) => CompareStable(left, right, comparer));
        var queue = new PriorityQueue<RunCursor<T>, StableItem<T>>(priorityComparer);
        var cursors = new List<RunCursor<T>>(count);
        long maxResidentRowBytes = 0;
        try
        {
            for (int index = 0; index < count; index++)
            {
                resources.ThrowIfCancellationRequested();
                RunInfo run = paths[start + index];
                maxResidentRowBytes = Math.Max(maxResidentRowBytes, run.MaxResidentRowBytes);
                var cursor = new RunCursor<T>(run, codec);
                cursors.Add(cursor);
                if (cursor.MoveNext())
                    queue.Enqueue(cursor, cursor.Current);
            }

            using BinaryWriter writer = SqlSpillRowCodec.CreateWriter(outputPath);
            writer.Write(0L);
            long writtenCount = 0;
            while (queue.TryDequeue(out RunCursor<T>? cursor, out StableItem<T> item))
            {
                resources.ThrowIfCancellationRequested();
                writer.Write(item.Ordinal);
                SqlSpillRowCodec.WriteRow(writer, codec.Encode(item.Row));
                writtenCount++;
                if (cursor.MoveNext())
                    queue.Enqueue(cursor, cursor.Current);
            }
            writer.Flush();
            writer.BaseStream.Position = 0;
            writer.Write(writtenCount);
        }
        finally
        {
            queue.Clear();
            foreach (RunCursor<T> cursor in cursors)
                cursor.Dispose();
        }

        SqlExecutionTelemetry.RecordSpill(new FileInfo(outputPath).Length);
        for (int index = 0; index < count; index++)
            File.Delete(paths[start + index].Path);
        return new RunInfo(outputPath, maxResidentRowBytes);
    }

    /// <summary>
    /// 为一个中间归并组取得预算；完整组失败时按二分 fan-in 缩小，最小两路允许无法预留的 oversized 行继续前进。
    /// </summary>
    private static MergeMemoryLease ReserveMergeGroup(
        SqlQueryResources resources,
        IReadOnlyList<RunInfo> paths,
        int start,
        int maxCount,
        bool includeWriter,
        out int count)
    {
        if (maxCount < 2 || start < 0 || start > paths.Count - maxCount)
            throw new ArgumentOutOfRangeException(nameof(maxCount), "SQL 排序归并预算分组范围非法。");

        var reservation = resources.CreateReservation();
        int candidateCount = maxCount;
        while (true)
        {
            long charge = EstimateMergeCharge(paths, start, candidateCount, includeWriter);
            if (reservation.TryReserve(charge))
            {
                count = candidateCount;
                return new MergeMemoryLease(reservation, ownsOversizedGate: false);
            }
            if (candidateCount == 2)
                break;
            candidateCount = Math.Max(2, candidateCount / 2);
        }

        // 当前 codec 只能比较完整 T；若单行大于预算，正确归并至少必须同时持有两个 run head。
        MergeMemoryLease oversizedLease = AcquireOversizedMergeGate(resources, reservation);
        if (includeWriter)
            _ = reservation.TryReserve(MergeWriterFixedBytes);
        for (int index = 0; index < 2; index++)
        {
            long cursorCharge = SaturatingAdd(
                MergeCursorFixedBytes,
                paths[start + index].MaxResidentRowBytes);
            _ = reservation.TryReserve(cursorCharge);
        }
        _ = reservation.TryReserve(Math.Max(
            paths[start].MaxResidentRowBytes,
            paths[start + 1].MaxResidentRowBytes));
        count = 2;
        return oversizedLease;
    }

    /// <summary>串行取得无法完整计费的 oversized 归并例外，并让等待过程响应查询取消。</summary>
    private static MergeMemoryLease AcquireOversizedMergeGate(
        SqlQueryResources resources,
        SqlQueryResources.SqlOperatorMemoryReservation reservation)
    {
        Interlocked.Increment(ref _waitingOversizedMergeCount);
        try
        {
            OversizedMergeGate.Wait(resources.CancellationToken);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _waitingOversizedMergeCount);
        }

        Interlocked.Increment(ref _activeOversizedMergeCount);
        return new MergeMemoryLease(reservation, ownsOversizedGate: true);
    }

    /// <summary>释放 oversized 归并串行闸门，并同步维护测试与诊断计数。</summary>
    private static void ReleaseOversizedMergeGate()
    {
        int remaining = Interlocked.Decrement(ref _activeOversizedMergeCount);
        if (remaining < 0)
            throw new InvalidOperationException("SQL oversized 归并闸门被重复释放。");
        OversizedMergeGate.Release();
    }

    /// <summary>计算一组归并游标及可选输出 writer 的保守预算，溢出时饱和到 long 上限。</summary>
    private static long EstimateMergeCharge(
        IReadOnlyList<RunInfo> paths,
        int start,
        int count,
        bool includeWriter)
    {
        long charge = includeWriter ? MergeWriterFixedBytes : 0;
        long largestRowBytes = 0;
        for (int index = 0; index < count; index++)
        {
            long rowBytes = paths[start + index].MaxResidentRowBytes;
            largestRowBytes = Math.Max(largestRowBytes, rowBytes);
            long cursorCharge = SaturatingAdd(
                MergeCursorFixedBytes,
                rowBytes);
            charge = SaturatingAdd(charge, cursorCharge);
        }
        // 游标读取下一行或 codec 编码当前行时，旧 head 与一个额外行对象会短暂并存。
        return SaturatingAdd(charge, largestRowBytes);
    }

    /// <summary>执行非负内存估算的饱和加法，避免极端估算值在分组规划时回绕。</summary>
    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    /// <summary>
    /// 为最终归并准备预算；全部游标无法同时进入预算时先压缩部分 run，再重试而不是静默打开 32 个大行。
    /// </summary>
    private static MergeMemoryLease PrepareFinalMerge<T>(
        SqlQueryResources resources,
        ref IReadOnlyList<RunInfo> paths,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        ValidateFinalRunCount(paths);
        var current = new List<RunInfo>(paths);
        while (true)
        {
            var finalReservation = resources.CreateReservation();
            long finalCharge = EstimateMergeCharge(current, 0, current.Count, includeWriter: false);
            if (finalReservation.TryReserve(finalCharge))
            {
                paths = current;
                return new MergeMemoryLease(finalReservation, ownsOversizedGate: false);
            }

            if (current.Count == 1)
            {
                // 单行超预算时同样串行化，避免多个查询同时绕过全局预算保留大行。
                MergeMemoryLease oversizedLease = AcquireOversizedMergeGate(resources, finalReservation);
                _ = finalReservation.TryReserve(MergeCursorFixedBytes);
                _ = finalReservation.TryReserve(current[0].MaxResidentRowBytes);
                _ = finalReservation.TryReserve(current[0].MaxResidentRowBytes);
                paths = current;
                return oversizedLease;
            }
            finalReservation.Dispose();

            if (current.Count == 2)
            {
                MergeMemoryLease pairReservation = ReserveMergeGroup(
                    resources,
                    current,
                    start: 0,
                    maxCount: 2,
                    includeWriter: false,
                    out _);
                paths = current;
                return pairReservation;
            }

            using MergeMemoryLease groupReservation = ReserveMergeGroup(
                resources,
                current,
                start: 0,
                maxCount: Math.Min(MergeFanIn, current.Count),
                includeWriter: true,
                out int mergeCount);
            RunInfo merged = MergeRunsToFileCore(
                resources,
                current,
                start: 0,
                mergeCount,
                comparer,
                codec);
            current.RemoveRange(0, mergeCount);
            current.Insert(0, merged);
        }
    }

    private static T[] MergeAndPage<T>(
        SqlQueryResources resources,
        IReadOnlyList<RunInfo> paths,
        IComparer<T> comparer,
        int offset,
        int? fetch,
        SqlSpillCodec<T> codec)
    {
        using MergeMemoryLease mergeReservation = PrepareFinalMerge(
            resources,
            ref paths,
            comparer,
            codec);
        var priorityComparer = Comparer<StableItem<T>>.Create(
            (left, right) => CompareStable(left, right, comparer));
        var queue = new PriorityQueue<RunCursor<T>, StableItem<T>>(priorityComparer);
        var cursors = new List<RunCursor<T>>(paths.Count);
        try
        {
            foreach (RunInfo run in paths)
            {
                resources.ThrowIfCancellationRequested();
                var cursor = new RunCursor<T>(run, codec);
                cursors.Add(cursor);
                if (cursor.MoveNext())
                    queue.Enqueue(cursor, cursor.Current);
            }

            // LIMIT 可能接近 int.MaxValue，结果数量尚未知时只做小幅预分配，避免绕过查询预算触发 OOM。
            int capacity = fetch is { } take ? Math.Min(Math.Max(0, take), 256) : 0;
            var result = capacity == 0 ? new List<T>() : new List<T>(capacity);
            long index = 0;
            while (queue.TryDequeue(out RunCursor<T>? cursor, out StableItem<T> item))
            {
                resources.ThrowIfCancellationRequested();
                if (index++ >= offset)
                {
                    result.Add(item.Row);
                    if (fetch is { } limit && result.Count == limit)
                        break;
                }
                if (cursor.MoveNext())
                    queue.Enqueue(cursor, cursor.Current);
            }
            return result.ToArray();
        }
        finally
        {
            try
            {
                queue.Clear();
                foreach (RunCursor<T> cursor in cursors)
                    cursor.Dispose();
            }
            finally
            {
                DeleteRunsBestEffort(paths);
            }
        }
    }

    /// <summary>
    /// 逐行归并已排序的 run；调用方提前停止时通过迭代器 finally 立即关闭全部文件游标。
    /// </summary>
    private static IEnumerable<T> MergeOrdered<T>(
        SqlQueryResources resources,
        IReadOnlyList<RunInfo> paths,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        using MergeMemoryLease mergeReservation = PrepareFinalMerge(
            resources,
            ref paths,
            comparer,
            codec);
        var priorityComparer = Comparer<StableItem<T>>.Create(
            (left, right) => CompareStable(left, right, comparer));
        var queue = new PriorityQueue<RunCursor<T>, StableItem<T>>(priorityComparer);
        var cursors = new List<RunCursor<T>>(paths.Count);
        try
        {
            foreach (RunInfo run in paths)
            {
                resources.ThrowIfCancellationRequested();
                var cursor = new RunCursor<T>(run, codec);
                cursors.Add(cursor);
                if (cursor.MoveNext())
                    queue.Enqueue(cursor, cursor.Current);
            }

            while (queue.TryDequeue(out RunCursor<T>? cursor, out StableItem<T> item))
            {
                resources.ThrowIfCancellationRequested();
                yield return item.Row;
                if (cursor.MoveNext())
                    queue.Enqueue(cursor, cursor.Current);
            }
        }
        finally
        {
            try
            {
                queue.Clear();
                foreach (RunCursor<T> cursor in cursors)
                    cursor.Dispose();
            }
            finally
            {
                DeleteRunsBestEffort(paths);
            }
        }
    }

    /// <summary>关闭归并游标后尽力删除最终 run；失败文件由查询根工作区释放时兜底清理。</summary>
    private static void DeleteRunsBestEffort(IReadOnlyList<RunInfo> runs)
    {
        foreach (RunInfo run in runs)
        {
            try
            {
                File.Delete(run.Path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static T[] SortAndPage<T>(
        List<StableItem<T>> items,
        IComparer<T> comparer,
        int offset,
        int? fetch,
        SqlQueryResources resources)
    {
        SortWithCancellation(
            items,
            (left, right) => CompareStable(left, right, comparer),
            resources);
        if (offset >= items.Count)
            return [];
        int count = fetch is { } take
            ? Math.Min(take, items.Count - offset)
            : items.Count - offset;
        var result = new T[count];
        for (int i = 0; i < count; i++)
            result[i] = items[offset + i].Row;
        return result;
    }

    /// <summary>清空已落盘 run 的行引用与 backing array，确保归还预算后不继续占用 K 级结构内存。</summary>
    private static void ClearRunBuffer<T>(List<StableItem<T>> buffer)
    {
        buffer.Clear();
        buffer.Capacity = 0;
    }

    /// <summary>
    /// 对可取消查询按固定比较次数轮询取消；普通查询继续走原比较器，不增加每次比较的分支成本。
    /// </summary>
    private static void SortWithCancellation<T>(
        List<T> items,
        Comparison<T> comparison,
        SqlQueryResources resources)
    {
        resources.ThrowIfCancellationRequested();
        CancellationToken cancellationToken = resources.CancellationToken;
        if (!cancellationToken.CanBeCanceled)
        {
            items.Sort(comparison);
            return;
        }

        int comparisonsUntilPoll = SortCancellationPollingInterval;
        try
        {
            items.Sort((left, right) =>
            {
                if (--comparisonsUntilPoll == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    comparisonsUntilPoll = SortCancellationPollingInterval;
                }
                return comparison(left, right);
            });
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is OperationCanceledException
                  && cancellationToken.IsCancellationRequested)
        {
            // List.Sort 会包装比较器异常；恢复标准取消语义，避免上层把超时误报为服务器错误。
            throw new OperationCanceledException(
                exception.InnerException.Message,
                exception.InnerException,
                cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static int CompareStable<T>(StableItem<T> left, StableItem<T> right, IComparer<T> comparer)
    {
        int comparison = comparer.Compare(left.Row, right.Row);
        return comparison != 0 ? comparison : left.Ordinal.CompareTo(right.Ordinal);
    }

    /// <summary>比较候选项与预算堆项，并以原始序号保持同键稳定性。</summary>
    private static int CompareStable<T>(
        StableItem<T> left,
        BudgetedStableItem<T> right,
        IComparer<T> comparer)
    {
        int comparison = comparer.Compare(left.Row, right.Row);
        return comparison != 0 ? comparison : left.Ordinal.CompareTo(right.Ordinal);
    }

    /// <summary>比较两个预算堆项，并以原始序号保持同键稳定性。</summary>
    private static int CompareStable<T>(
        BudgetedStableItem<T> left,
        BudgetedStableItem<T> right,
        IComparer<T> comparer)
    {
        int comparison = comparer.Compare(left.Row, right.Row);
        return comparison != 0 ? comparison : left.Ordinal.CompareTo(right.Ordinal);
    }

    /// <summary>取得下一个稳定序号，并在无法继续保持稳定顺序前终止查询。</summary>
    private static long TakeOrdinal(ref long nextOrdinal)
    {
        if (nextOrdinal == long.MaxValue)
            throw new InvalidOperationException("SQL 外部排序输入行数超过稳定序号上限。");
        return nextOrdinal++;
    }

    /// <summary>校验最终一次归并不会打开超过固定 fan-in 的输入游标。</summary>
    private static void ValidateFinalRunCount(IReadOnlyList<RunInfo> paths)
    {
        if (paths.Count is < 1 or > MergeFanIn)
            throw new InvalidOperationException("SQL 排序最终归并 run 数量超出固定 fan-in。");
    }

    /// <summary>按固定 fan-in 分层归并 run，使生成期保留文件数量具有常量上界。</summary>
    private sealed class IncrementalRunSet<T>
    {
        private readonly SqlQueryResources _resources;
        private readonly IComparer<T> _comparer;
        private readonly SqlSpillCodec<T> _codec;
        private readonly List<RunInfo>?[] _levels = new List<RunInfo>?[MaxMergeLevels];
        private int _count;

        /// <summary>创建当前外部排序专用的增量 run 集合。</summary>
        internal IncrementalRunSet(
            SqlQueryResources resources,
            IComparer<T> comparer,
            SqlSpillCodec<T> codec)
        {
            _resources = resources;
            _comparer = comparer;
            _codec = codec;
        }

        internal int Count => _count;

        /// <summary>加入一个初始 run；层满时立即归并并把结果向上一层进位。</summary>
        internal void Add(RunInfo run)
        {
            ArgumentException.ThrowIfNullOrEmpty(run.Path);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(run.MaxResidentRowBytes);
            for (int level = 0; level < _levels.Length; level++)
            {
                List<RunInfo> levelRuns = _levels[level] ??= new List<RunInfo>(MergeFanIn);
                levelRuns.Add(run);
                _count++;
                if (levelRuns.Count < MergeFanIn)
                    return;
                if (level == _levels.Length - 1)
                    throw new InvalidOperationException("SQL 外部排序 run 数量超过稳定序号允许的理论上限。");

                // 归并输出创建时旧输入仍存在，额外只增加一个临时文件；成功后输入立即删除。
                run = MergeRunsToSingle(_resources, levelRuns, _comparer, _codec);
                _count -= levelRuns.Count;
                levelRuns.Clear();
            }
        }

        /// <summary>收集各层剩余 run，并压缩到最终一次归并允许的固定 fan-in。</summary>
        internal IReadOnlyList<RunInfo> Complete()
        {
            var retained = new List<RunInfo>(_count);
            foreach (List<RunInfo>? levelRuns in _levels)
            {
                if (levelRuns is not null)
                    retained.AddRange(levelRuns);
            }
            return ConsolidateRuns(_resources, retained, _comparer, _codec);
        }
    }

    private sealed class RunCursor<T> : IDisposable
    {
        private readonly BinaryReader _reader;
        private readonly SqlSpillCodec<T> _codec;
        private readonly long _maxResidentRowBytes;
        private long _remaining;

        internal RunCursor(RunInfo run, SqlSpillCodec<T> codec)
        {
            _reader = SqlSpillRowCodec.CreateReader(run.Path);
            _codec = codec;
            _maxResidentRowBytes = run.MaxResidentRowBytes;
            try
            {
                _remaining = _reader.ReadInt64();
                if (_remaining < 0)
                    throw new InvalidDataException("SQL 排序 run 的行数非法。");
            }
            catch
            {
                _reader.Dispose();
                throw;
            }
        }

        internal StableItem<T> Current { get; private set; }

        internal bool MoveNext()
        {
            if (_remaining == 0)
            {
                Current = default;
                return false;
            }
            long ordinal = _reader.ReadInt64();
            Current = new StableItem<T>(
                _codec.Decode(SqlSpillRowCodec.ReadRow(_reader)),
                ordinal,
                _maxResidentRowBytes);
            _remaining--;
            return true;
        }

        public void Dispose()
        {
            Current = default;
            _reader.Dispose();
        }
    }
}
