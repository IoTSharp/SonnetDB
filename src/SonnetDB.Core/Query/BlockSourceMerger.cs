using SonnetDB.Model;

namespace SonnetDB.Query;

/// <summary>
/// 多路有序流式合并器：将已解码的 MemTable 切片与「惰性解码」的 Segment Block 源按时间戳升序
/// 合并为单个 <see cref="DataPoint"/> 流。
/// <para>
/// 与旧实现（先把全部候选 block 解码进 <c>List&lt;DataPoint[]&gt;</c> 再合并）相比，本合并器只在某个
/// block 抵达合并前沿时才解码它，从而把解码工作集限制为「当前时间点上相互重叠的 block 数」（overlap
/// depth），而非候选 block 总数——消除大范围扫描一次性把所有 block 解码进堆的 LOH 峰值（C9 / #220）。
/// </para>
/// <para>
/// 关键不变式：block 的 <c>MinTimestamp</c>（经查询下界裁剪后的 <see cref="LazyBlock.LowerBound"/>）是其可能
/// 产出的最小时间戳的下界，无需解码即可获得。据此把尚未解码的 block 按 <c>LowerBound</c> 升序排列，仅当
/// <c>LowerBound &lt;=</c> 当前活跃前沿时间戳时才解码并加入活跃堆——保证任何点被产出前，所有可能早于它的
/// block 都已解码就位。
/// </para>
/// </summary>
internal static class BlockSourceMerger
{
    /// <summary>惰性解码某个 Segment Block 并返回其经时间范围裁剪后的只读点视图（可能为空）。</summary>
    internal delegate ReadOnlyMemory<DataPoint> BlockDecodeFunc();

    /// <summary>
    /// 一个尚未解码的 Segment Block 源。
    /// </summary>
    internal readonly struct LazyBlock
    {
        /// <summary>该 block 可能产出的最小时间戳的下界（通常为 <c>max(查询下界, descriptor.MinTimestamp)</c>）。</summary>
        public readonly long LowerBound;

        /// <summary>按需解码该 block（已做时间范围裁剪）的委托。</summary>
        public readonly BlockDecodeFunc Decode;

        public LazyBlock(long lowerBound, BlockDecodeFunc decode)
        {
            LowerBound = lowerBound;
            Decode = decode;
        }
    }

    /// <summary>
    /// 按时间戳升序流式合并已解码的 MemTable 切片与惰性 Segment Block 源。
    /// <para>
    /// 稳定性（决定同时间戳的产出优先级，Rank 小者先）：先所有 segment block（<paramref name="segmentBlocks"/>
    /// 顺序，即 SegmentId 升序、段内 MinTimestamp 升序），后所有 MemTable 切片（<paramref name="memTableSlices"/>
    /// 顺序，即 sealing 在前、active 在后）。v1 不去重：同时间戳多源全部 yield。
    /// </para>
    /// </summary>
    /// <param name="memTableSlices">已解码的 MemTable 侧切片列表（各自已按时间升序）；空列表表示无 MemTable 数据。</param>
    /// <param name="segmentBlocks">惰性 Segment Block 源列表（顺序对应 SegmentId 升序、段内 MinTimestamp 升序）。</param>
    /// <returns>按时间戳升序合并后的 DataPoint 序列。</returns>
    public static IEnumerable<DataPoint> Merge(
        IReadOnlyList<ReadOnlyMemory<DataPoint>> memTableSlices,
        IReadOnlyList<LazyBlock> segmentBlocks)
    {
        int segmentCount = segmentBlocks.Count;
        if (segmentCount == 0 && memTableSlices.Count == 0)
            yield break;

        // 未解码 block 按 (LowerBound, Rank) 升序排列；顺序消费即可 O(1) 取「下一个待解码最小前沿」，
        // 无需二级堆（LowerBound 不可变）。Rank 为原始候选序，保留同时间戳稳定性。
        var pending = new PendingBlock[segmentCount];
        for (int j = 0; j < segmentCount; j++)
            pending[j] = new PendingBlock(segmentBlocks[j].LowerBound, j, segmentBlocks[j].Decode);
        Array.Sort(pending, static (a, b) =>
        {
            int cmp = a.LowerBound.CompareTo(b.LowerBound);
            return cmp != 0 ? cmp : a.Rank.CompareTo(b.Rank);
        });

        // 活跃堆：仅持有「已解码且下界 <= 当前前沿」的 chunk，规模 = 当前 overlap depth。
        var active = new List<ActiveChunk>();

        // MemTable 切片已解码，直接入活跃堆（Rank 排在所有 segment block 之后）。
        for (int i = 0; i < memTableSlices.Count; i++)
        {
            var slice = memTableSlices[i];
            if (slice.Length > 0)
                HeapPush(active, new ActiveChunk(slice, 0, segmentCount + i, slice.Span[0].Timestamp));
        }

        int p = 0;
        while (true)
        {
            // 解码所有下界不晚于当前活跃前沿的待解码 block（活跃为空时至少解码一个以推进）。
            while (p < pending.Length
                && (active.Count == 0 || pending[p].LowerBound <= active[0].HeadTimestamp))
            {
                var decoded = pending[p].Decode();
                p++;
                if (decoded.Length > 0)
                    HeapPush(active, new ActiveChunk(decoded, 0, pending[p - 1].Rank, decoded.Span[0].Timestamp));
            }

            if (active.Count == 0)
                yield break;

            var top = active[0];
            DataPoint point = top.Chunk.Span[top.Cursor];

            int nextCursor = top.Cursor + 1;
            if (nextCursor < top.Chunk.Length)
            {
                top.Cursor = nextCursor;
                top.HeadTimestamp = top.Chunk.Span[nextCursor].Timestamp;
                active[0] = top;
                HeapSiftDown(active, 0);
            }
            else
            {
                HeapPopLast(active);
            }

            yield return point;
        }
    }

    private readonly struct PendingBlock
    {
        public readonly long LowerBound;
        public readonly int Rank;
        public readonly BlockDecodeFunc Decode;

        public PendingBlock(long lowerBound, int rank, BlockDecodeFunc decode)
        {
            LowerBound = lowerBound;
            Rank = rank;
            Decode = decode;
        }
    }

    private struct ActiveChunk
    {
        public readonly ReadOnlyMemory<DataPoint> Chunk;
        public readonly int Rank;
        public int Cursor;
        public long HeadTimestamp;

        public ActiveChunk(ReadOnlyMemory<DataPoint> chunk, int cursor, int rank, long headTimestamp)
        {
            Chunk = chunk;
            Cursor = cursor;
            Rank = rank;
            HeadTimestamp = headTimestamp;
        }
    }

    // ── 活跃 chunk 最小堆（键：HeadTimestamp，同值时 Rank 小者优先） ─────────────────────────

    private static bool IsLess(in ActiveChunk a, in ActiveChunk b)
    {
        if (a.HeadTimestamp != b.HeadTimestamp)
            return a.HeadTimestamp < b.HeadTimestamp;
        return a.Rank < b.Rank;
    }

    private static void HeapPush(List<ActiveChunk> heap, ActiveChunk item)
    {
        heap.Add(item);
        int i = heap.Count - 1;
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (!IsLess(heap[i], heap[parent]))
                break;
            (heap[i], heap[parent]) = (heap[parent], heap[i]);
            i = parent;
        }
    }

    /// <summary>移除堆顶：把堆尾移到堆顶后下沉。调用方已通过 <c>active[0]</c> 读取过原堆顶。</summary>
    private static void HeapPopLast(List<ActiveChunk> heap)
    {
        int last = heap.Count - 1;
        heap[0] = heap[last];
        heap.RemoveAt(last);
        if (heap.Count > 0)
            HeapSiftDown(heap, 0);
    }

    private static void HeapSiftDown(List<ActiveChunk> heap, int i)
    {
        int n = heap.Count;
        while (true)
        {
            int smallest = i;
            int left = 2 * i + 1;
            int right = 2 * i + 2;

            if (left < n && IsLess(heap[left], heap[smallest]))
                smallest = left;
            if (right < n && IsLess(heap[right], heap[smallest]))
                smallest = right;

            if (smallest == i)
                break;

            (heap[i], heap[smallest]) = (heap[smallest], heap[i]);
            i = smallest;
        }
    }
}
