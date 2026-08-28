namespace SonnetDB.Sql.Execution;

/// <summary>把算子私有行类型映射到通用 SQL spill 行。</summary>
internal readonly record struct SqlSpillCodec<T>(
    Func<T, object?[]> Encode,
    Func<object?[], T> Decode);

/// <summary>常用 SQL 结果行的 spill 编解码器。</summary>
internal static class SqlSpillCodecs
{
    internal static SqlSpillCodec<IReadOnlyList<object?>> ReadOnlyRows { get; } = new(
        static row => row as object?[] ?? row.ToArray(),
        static row => row);

    internal static SqlSpillCodec<object?[]> ArrayRows { get; } = new(
        static row => row,
        static row => row);
}

/// <summary>预算感知的稳定外部归并排序。</summary>
internal static class SqlSpillSorter
{
    private const int MergeFanIn = 32;
    private readonly record struct StableItem<T>(T Row, long Ordinal);

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

        using var reservation = resources.CreateReservation();
        var buffer = new List<StableItem<T>>();
        var runs = new List<string>();
        long ordinal = 0;
        foreach (T row in rows)
        {
            resources.ThrowIfCancellationRequested();
            object?[] encoded = codec.Encode(row);
            long bytes = checked(SqlSpillRowCodec.EstimateRowBytes(encoded) + 32);
            if (!reservation.TryReserve(bytes))
            {
                if (buffer.Count != 0)
                {
                    runs.Add(WriteRun(resources, buffer, comparer, codec));
                    buffer.Clear();
                    reservation.ReleaseAll();
                }

                if (!reservation.TryReserve(bytes))
                {
                    buffer.Add(new StableItem<T>(row, ordinal++));
                    runs.Add(WriteRun(resources, buffer, comparer, codec));
                    buffer.Clear();
                    continue;
                }
            }
            buffer.Add(new StableItem<T>(row, ordinal++));
        }

        if (runs.Count == 0)
            return SortAndPage(buffer, comparer, offset, fetch);

        if (buffer.Count != 0)
        {
            runs.Add(WriteRun(resources, buffer, comparer, codec));
            buffer.Clear();
        }
        reservation.ReleaseAll();
        IReadOnlyList<string> consolidated = ConsolidateRuns(resources, runs, comparer, codec);
        return MergeAndPage(resources, consolidated, comparer, offset, fetch, codec);
    }

    private static string WriteRun<T>(
        SqlQueryResources resources,
        List<StableItem<T>> items,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        items.Sort((left, right) => CompareStable(left, right, comparer));
        string path = resources.GetWorkspace().CreateFilePath("sort-run");
        using (BinaryWriter writer = SqlSpillRowCodec.CreateWriter(path))
        {
            writer.Write((long)items.Count);
            foreach (StableItem<T> item in items)
            {
                resources.ThrowIfCancellationRequested();
                writer.Write(item.Ordinal);
                SqlSpillRowCodec.WriteRow(writer, codec.Encode(item.Row));
            }
        }
        SqlExecutionTelemetry.RecordSpill(new FileInfo(path).Length);
        return path;
    }

    private static IReadOnlyList<string> ConsolidateRuns<T>(
        SqlQueryResources resources,
        IReadOnlyList<string> initialRuns,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        IReadOnlyList<string> current = initialRuns;
        while (current.Count > MergeFanIn)
        {
            var next = new List<string>((current.Count + MergeFanIn - 1) / MergeFanIn);
            for (int start = 0; start < current.Count; start += MergeFanIn)
            {
                int count = Math.Min(MergeFanIn, current.Count - start);
                if (count == 1)
                {
                    next.Add(current[start]);
                    continue;
                }
                var mergePaths = new string[count];
                for (int index = 0; index < count; index++)
                    mergePaths[index] = current[start + index];
                string merged = MergeRunsToFile(
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

    private static string MergeRunsToFile<T>(
        SqlQueryResources resources,
        IReadOnlyList<string> paths,
        IComparer<T> comparer,
        SqlSpillCodec<T> codec)
    {
        string outputPath = resources.GetWorkspace().CreateFilePath("sort-merge");
        var priorityComparer = Comparer<StableItem<T>>.Create(
            (left, right) => CompareStable(left, right, comparer));
        var queue = new PriorityQueue<RunCursor<T>, StableItem<T>>(priorityComparer);
        var cursors = new List<RunCursor<T>>(paths.Count);
        try
        {
            foreach (string path in paths)
            {
                var cursor = new RunCursor<T>(path, codec);
                cursors.Add(cursor);
                if (cursor.MoveNext())
                    queue.Enqueue(cursor, cursor.Current);
            }

            using BinaryWriter writer = SqlSpillRowCodec.CreateWriter(outputPath);
            writer.Write(0L);
            long count = 0;
            while (queue.TryDequeue(out RunCursor<T>? cursor, out StableItem<T> item))
            {
                resources.ThrowIfCancellationRequested();
                writer.Write(item.Ordinal);
                SqlSpillRowCodec.WriteRow(writer, codec.Encode(item.Row));
                count++;
                if (cursor.MoveNext())
                    queue.Enqueue(cursor, cursor.Current);
            }
            writer.Flush();
            writer.BaseStream.Position = 0;
            writer.Write(count);
        }
        finally
        {
            foreach (RunCursor<T> cursor in cursors)
                cursor.Dispose();
        }

        SqlExecutionTelemetry.RecordSpill(new FileInfo(outputPath).Length);
        foreach (string path in paths)
            File.Delete(path);
        return outputPath;
    }

    private static T[] MergeAndPage<T>(
        SqlQueryResources resources,
        IReadOnlyList<string> paths,
        IComparer<T> comparer,
        int offset,
        int? fetch,
        SqlSpillCodec<T> codec)
    {
        var priorityComparer = Comparer<StableItem<T>>.Create(
            (left, right) => CompareStable(left, right, comparer));
        var queue = new PriorityQueue<RunCursor<T>, StableItem<T>>(priorityComparer);
        var cursors = new List<RunCursor<T>>(paths.Count);
        try
        {
            foreach (string path in paths)
            {
                var cursor = new RunCursor<T>(path, codec);
                cursors.Add(cursor);
                if (cursor.MoveNext())
                    queue.Enqueue(cursor, cursor.Current);
            }

            int capacity = fetch is { } take ? Math.Max(0, take) : 0;
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
            foreach (RunCursor<T> cursor in cursors)
                cursor.Dispose();
        }
    }

    private static T[] SortAndPage<T>(
        List<StableItem<T>> items,
        IComparer<T> comparer,
        int offset,
        int? fetch)
    {
        items.Sort((left, right) => CompareStable(left, right, comparer));
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

    private static int CompareStable<T>(StableItem<T> left, StableItem<T> right, IComparer<T> comparer)
    {
        int comparison = comparer.Compare(left.Row, right.Row);
        return comparison != 0 ? comparison : left.Ordinal.CompareTo(right.Ordinal);
    }

    private sealed class RunCursor<T> : IDisposable
    {
        private readonly BinaryReader _reader;
        private readonly SqlSpillCodec<T> _codec;
        private long _remaining;

        internal RunCursor(string path, SqlSpillCodec<T> codec)
        {
            _reader = SqlSpillRowCodec.CreateReader(path);
            _codec = codec;
            _remaining = _reader.ReadInt64();
            if (_remaining < 0)
                throw new InvalidDataException("SQL 排序 run 的行数非法。");
        }

        internal StableItem<T> Current { get; private set; }

        internal bool MoveNext()
        {
            if (_remaining == 0)
                return false;
            long ordinal = _reader.ReadInt64();
            Current = new StableItem<T>(_codec.Decode(SqlSpillRowCodec.ReadRow(_reader)), ordinal);
            _remaining--;
            return true;
        }

        public void Dispose() => _reader.Dispose();
    }
}
