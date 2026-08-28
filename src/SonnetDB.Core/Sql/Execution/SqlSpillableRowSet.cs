namespace SonnetDB.Sql.Execution;

/// <summary>按内容比较的磁盘分桶行集合；用于预算耗尽后的稳定去重。</summary>
internal sealed class SqlSpillableRowSet : IDisposable
{
    private const int BucketCount = 64;
    private readonly SqlSpillWorkspace _workspace;
    private readonly IEqualityComparer<IReadOnlyList<object?>> _comparer;
    private readonly string?[] _bucketPaths = new string?[BucketCount];
    private bool _recordedSpill;

    internal SqlSpillableRowSet(
        SqlSpillWorkspace workspace,
        IEqualityComparer<IReadOnlyList<object?>> comparer)
    {
        _workspace = workspace;
        _comparer = comparer;
    }

    internal bool Add(IReadOnlyList<object?> row)
    {
        SqlQueryResources.Current?.ThrowIfCancellationRequested();
        int bucket = (int)((uint)_comparer.GetHashCode(row) % BucketCount);
        string? path = _bucketPaths[bucket];
        if (path is not null)
        {
            using BinaryReader reader = SqlSpillRowCodec.CreateReader(path);
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                SqlQueryResources.Current?.ThrowIfCancellationRequested();
                if (_comparer.Equals(row, SqlSpillRowCodec.ReadRow(reader)))
                    return false;
            }
        }
        else
        {
            path = _workspace.CreateFilePath($"distinct-{bucket:D2}");
            _bucketPaths[bucket] = path;
            using BinaryWriter initial = SqlSpillRowCodec.CreateWriter(path);
            initial.Write(0);
        }

        long before = new FileInfo(path).Length;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 64 * 1024))
        using (var writer = new BinaryWriter(stream))
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            int count = reader.ReadInt32();
            stream.Position = 0;
            writer.Write(checked(count + 1));
            stream.Position = stream.Length;
            SqlSpillRowCodec.WriteRow(writer, row);
        }
        long written = new FileInfo(path).Length - before;
        if (_recordedSpill)
            SqlExecutionTelemetry.RecordSpillBytes(written);
        else
        {
            SqlExecutionTelemetry.RecordSpill(written);
            _recordedSpill = true;
        }
        return true;
    }

    public void Dispose()
    {
        // 文件由查询工作区统一拥有和删除。
    }
}

/// <summary>阻塞算子的共享预算感知实现。</summary>
internal static class SqlBlockingOperators
{
    internal static IEnumerable<IReadOnlyList<object?>> DistinctRows(
        IEnumerable<IReadOnlyList<object?>> rows,
        IEqualityComparer<IReadOnlyList<object?>> comparer)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(comparer);
        SqlQueryResources? resources = SqlQueryResources.Current;
        if (resources is null)
        {
            var plain = new HashSet<IReadOnlyList<object?>>(comparer);
            foreach (IReadOnlyList<object?> row in rows)
            {
                if (plain.Add(row))
                    yield return row;
            }
            yield break;
        }

        using var reservation = resources.CreateReservation();
        var memorySet = new HashSet<IReadOnlyList<object?>>(comparer);
        SqlSpillableRowSet? diskSet = null;
        try
        {
            foreach (IReadOnlyList<object?> row in rows)
            {
                resources.ThrowIfCancellationRequested();
                if (diskSet is null)
                {
                    long bytes = SqlSpillRowCodec.EstimateRowBytes(row) + 48;
                    if (reservation.TryReserve(bytes))
                    {
                        if (memorySet.Add(row))
                            yield return row;
                        continue;
                    }

                    diskSet = new SqlSpillableRowSet(resources.GetWorkspace(), comparer);
                    foreach (IReadOnlyList<object?> existing in memorySet)
                        _ = diskSet.Add(existing);
                    memorySet.Clear();
                    reservation.ReleaseAll();
                }

                if (diskSet.Add(row))
                    yield return row;
            }
        }
        finally
        {
            diskSet?.Dispose();
        }
    }
}
