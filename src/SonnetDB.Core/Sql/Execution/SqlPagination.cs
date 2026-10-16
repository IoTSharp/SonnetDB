using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>将 Int64 逻辑分页与有界集合容量、索引下推和查询取消分开处理。</summary>
internal static class SqlPagination
{
    internal static IEnumerable<T> Skip<T>(this IEnumerable<T> rows, long count)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Iterate();

        IEnumerable<T> Iterate()
        {
            if (rows.TryGetNonEnumeratedCount(out int actualCount) && count >= actualCount)
                yield break;
            long remaining = count;
            foreach (T row in rows)
            {
                SqlQueryResources.Current?.ThrowIfCancellationRequested();
                if (remaining > 0)
                    remaining--;
                else
                    yield return row;
            }
        }
    }

    internal static IEnumerable<T> Take<T>(this IEnumerable<T> rows, long count)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Iterate();

        IEnumerable<T> Iterate()
        {
            if (count == 0)
                yield break;
            long remaining = count;
            foreach (T row in rows)
            {
                SqlQueryResources.Current?.ThrowIfCancellationRequested();
                yield return row;
                if (--remaining == 0)
                    yield break;
            }
        }
    }

    internal static int InitialCapacity(long count) => (int)Math.Min(Math.Max(0, count), 256);

    internal static bool TryGetInt32Prefix(long offset, long fetch, out int count)
    {
        count = 0;
        if (offset < 0 || fetch < 0 || offset > int.MaxValue || fetch > int.MaxValue - offset)
            return false;
        count = (int)(offset + fetch);
        return true;
    }

    internal static bool FitsInt32(PaginationSpec? pagination)
        => pagination is null || pagination.Offset <= int.MaxValue && pagination.Fetch is null or <= int.MaxValue;

    internal static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;
}
