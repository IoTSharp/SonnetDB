namespace SonnetDB.Sql.Execution;

/// <summary>
/// <c>ORDER BY … LIMIT k</c> 的有界 Top-N 选择工具（#214 / SQL Q6）。
/// <para>
/// 当存在 <c>Fetch</c> 上限时，只需按排序序取前 <c>offset + fetch</c> 行，无需把全部候选行全量排序。
/// 用大小为 <c>offset + fetch</c> 的有界堆单遍扫描（O(N log K)，K = offset+fetch），再对这 K 行排序、
/// 跳过 offset，替代"全量 <c>OrderBy().ToArray()</c> 再 <c>Skip().Take()</c>"（O(N log N) + 大物化峰值）。
/// 无 <c>Fetch</c> 上限时回退到全量排序 + 跳过 offset。
/// </para>
/// </summary>
internal static class TopN
{
    /// <summary>
    /// 按 <paramref name="comparer"/> 排序 <paramref name="rows"/>，应用 <paramref name="offset"/> /
    /// <paramref name="fetch"/> 分页；有 fetch 上限时走有界堆 Top-N，否则全量排序。稳定：同序保持输入相对顺序。
    /// </summary>
    /// <typeparam name="T">行类型。</typeparam>
    /// <param name="rows">候选行（只读，不修改）。</param>
    /// <param name="comparer">行排序比较器（升序语义；降序应在比较器内部处理）。</param>
    /// <param name="offset">跳过的行数（&lt; 0 视为 0）。</param>
    /// <param name="fetch">取的行数；<c>null</c> 表示不限（取到末尾）。</param>
    /// <returns>排序 + 分页后的行数组。</returns>
    public static T[] OrderByThenPaginate<T>(
        IReadOnlyList<T> rows,
        IComparer<T> comparer,
        int offset,
        int? fetch)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(comparer);

        if (offset < 0)
            offset = 0;

        // 无 fetch 上限：全量排序后跳过 offset。
        if (fetch is not int take)
        {
            if (rows.Count == 0 || offset >= rows.Count)
                return [];
            var sortedAll = StableSort(rows, comparer);
            return Slice(sortedAll, offset, sortedAll.Length - offset);
        }

        if (take <= 0 || rows.Count == 0 || offset >= rows.Count)
            return [];

        // 需要的前缀长度 = offset + take（对 int 溢出做保护）。
        long neededLong = (long)offset + take;
        int needed = neededLong >= rows.Count ? rows.Count : (int)neededLong;

        // 前缀已覆盖全部行：直接全量排序 + 分页（有界堆无收益）。
        if (needed >= rows.Count)
        {
            var sortedAll = StableSort(rows, comparer);
            int available = sortedAll.Length - offset;
            return Slice(sortedAll, offset, Math.Min(take, available));
        }

        // 有界堆 Top-N：维护大小为 needed 的最大堆（堆顶为当前前缀中"最大"的行），
        // 扫描时若新行比堆顶小则替换，最终堆内即排序序最小的 needed 行。
        var topPrefix = SelectTopPrefix(rows, comparer, needed);
        // topPrefix 已按升序排序；跳过 offset 取 take。
        int avail = topPrefix.Length - offset;
        return Slice(topPrefix, offset, Math.Min(take, avail));
    }

    /// <summary>
    /// 有界堆选出排序序最小的 <paramref name="count"/> 行，返回按升序排好的数组。
    /// 稳定性：以 (原始下标) 作次级键，保证等序行保留输入相对顺序。
    /// </summary>
    private static T[] SelectTopPrefix<T>(IReadOnlyList<T> rows, IComparer<T> comparer, int count)
    {
        // 用数组做二叉最大堆；元素带原始下标以保证稳定。
        var heap = new (T Row, int Index)[count];
        int size = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var candidate = (Row: rows[i], Index: i);
            if (size < count)
            {
                heap[size] = candidate;
                SiftUp(heap, size, comparer);
                size++;
            }
            else if (CompareStable(candidate, heap[0], comparer) < 0)
            {
                // 候选比堆顶（当前前缀中最大）更小：替换堆顶并下沉。
                heap[0] = candidate;
                SiftDown(heap, 0, size, comparer);
            }
        }

        // 堆内即所需前缀；按稳定序升序排序输出。
        Array.Sort(heap, 0, size, Comparer<(T Row, int Index)>.Create(
            (a, b) => CompareStable(a, b, comparer)));

        var result = new T[size];
        for (int i = 0; i < size; i++)
            result[i] = heap[i].Row;
        return result;
    }

    private static int CompareStable<T>(in (T Row, int Index) a, in (T Row, int Index) b, IComparer<T> comparer)
    {
        int c = comparer.Compare(a.Row, b.Row);
        return c != 0 ? c : a.Index.CompareTo(b.Index);
    }

    // 最大堆：父节点 >= 子节点（按稳定比较序）。堆顶是当前保留集合里排序序最大的一个。
    private static void SiftUp<T>((T Row, int Index)[] heap, int i, IComparer<T> comparer)
    {
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (CompareStable(heap[i], heap[parent], comparer) <= 0)
                break;
            (heap[i], heap[parent]) = (heap[parent], heap[i]);
            i = parent;
        }
    }

    private static void SiftDown<T>((T Row, int Index)[] heap, int i, int size, IComparer<T> comparer)
    {
        while (true)
        {
            int left = 2 * i + 1;
            int right = 2 * i + 2;
            int largest = i;
            if (left < size && CompareStable(heap[left], heap[largest], comparer) > 0)
                largest = left;
            if (right < size && CompareStable(heap[right], heap[largest], comparer) > 0)
                largest = right;
            if (largest == i)
                break;
            (heap[i], heap[largest]) = (heap[largest], heap[i]);
            i = largest;
        }
    }

    private static T[] StableSort<T>(IReadOnlyList<T> rows, IComparer<T> comparer)
    {
        var indexed = new (T Row, int Index)[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            indexed[i] = (rows[i], i);

        Array.Sort(indexed, (a, b) =>
        {
            int c = comparer.Compare(a.Row, b.Row);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });

        var result = new T[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            result[i] = indexed[i].Row;
        return result;
    }

    private static T[] Slice<T>(T[] source, int start, int length)
    {
        if (length <= 0)
            return [];
        var result = new T[length];
        Array.Copy(source, start, result, 0, length);
        return result;
    }
}
