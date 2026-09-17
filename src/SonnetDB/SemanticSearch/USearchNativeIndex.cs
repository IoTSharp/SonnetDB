using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Cloud.Unum.USearch;
using Microsoft.Win32.SafeHandles;

namespace SonnetDB.SemanticSearch;

/// <summary>
/// 补齐已固定的 USearch 2.26.0 C ABI；NuGet 托管包装尚未公开 filtered search。
/// </summary>
/// <remarks>
/// 签名逐项对应 https://github.com/unum-cloud/usearch/blob/v2.26.0/c/usearch.h。
/// 仅用于 NuGet 提供的 64 位 RID，因此复用其 IndexOptions 布局；不访问托管包装私有句柄。
/// 固定 DllImport 签名可由 NativeAOT 预编译，不需要 unsafe 或动态生成 marshalling。
/// </remarks>
internal sealed class USearchNativeIndex : IDisposable
{
    private readonly IndexHandle _handle;
    private readonly int _dimensions;
    private nuint _capacity;
    private nuint _size;

    internal USearchNativeIndex(int dimensions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        _dimensions = dimensions;
        var options = new IndexOptions
        {
            metric_kind = MetricKind.Cos,
            quantization = ScalarKind.Float32,
            dimensions = checked((ulong)dimensions),
            connectivity = 16,
            expansion_add = 200,
            expansion_search = 64,
        };
        nint pointer = Native.Init(ref options, out nint error);
        _handle = new IndexHandle(pointer);
        if (error != 0 || _handle.IsInvalid)
        {
            _handle.Dispose();
            ThrowIfError(error);
            throw new USearchException("USearch 返回了无效索引句柄。");
        }
    }

    internal void Add(ulong key, float[] vector)
    {
        ValidateVector(vector);
        if (_size == _capacity)
        {
            nuint capacity = Math.Max((nuint)16, checked(_capacity * 2));
            Native.Reserve(_handle, capacity, out nint reserveError);
            ThrowIfError(reserveError);
            _capacity = capacity;
        }
        Native.Add(_handle, key, vector, ScalarKind.Float32, out nint error);
        ThrowIfError(error);
        _size++;
    }

    internal void Remove(ulong key)
    {
        nuint removed = Native.Remove(_handle, key, out nint error);
        ThrowIfError(error);
        _size -= removed;
    }

    internal int Search(float[] query, int count, out ulong[] keys, out float[] distances)
    {
        ValidateVector(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (_size == 0)
        {
            keys = [];
            distances = [];
            return 0;
        }
        Native.ChangeExpansionSearch(_handle, 64, out nint expansionError);
        ThrowIfError(expansionError);
        keys = new ulong[count];
        distances = new float[count];
        nuint matches = Native.Search(_handle, query, ScalarKind.Float32, checked((nuint)count), keys, distances, out nint error);
        ThrowIfError(error);
        return checked((int)matches);
    }

    internal int SearchFiltered(
        float[] query, int count, IReadOnlySet<ulong> allowedKeys, int candidateLimit,
        CancellationToken cancellationToken, out ulong[] keys, out float[] distances, out bool budgetExceeded)
    {
        ValidateVector(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(candidateLimit);
        cancellationToken.ThrowIfCancellationRequested();
        if (_size == 0)
        {
            keys = [];
            distances = [];
            budgetExceeded = false;
            return 0;
        }
        Native.ChangeExpansionSearch(_handle, checked((nuint)candidateLimit), out nint expansionError);
        ThrowIfError(expansionError);
        keys = new ulong[count];
        distances = new float[count];
        var state = new FilterState(allowedKeys, candidateLimit, cancellationToken);
        Native.Filter callback = (key, _) => state.Accept(key);
        try
        {
            nuint matches = Native.FilteredSearch(
                _handle, query, ScalarKind.Float32, checked((nuint)count), callback,
                0, keys, distances, out nint error);
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Failure is not null)
                ExceptionDispatchInfo.Capture(state.Failure).Throw();
            ThrowIfError(error);
            budgetExceeded = state.BudgetExceeded;
            return checked((int)matches);
        }
        finally
        {
            // usearch_filtered_search 同步执行回调且不保存函数指针。
            GC.KeepAlive(callback);
        }
    }

    public void Dispose() => _handle.Dispose();

    private void ValidateVector(float[] vector)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (vector.Length != _dimensions)
            throw new ArgumentException("向量维度与 USearch 索引不一致。", nameof(vector));
    }

    private static void ThrowIfError(nint error)
    {
        // usearch_error_t 是库拥有的 UTF-8 字符串，调用方不得释放。
        if (error != 0)
            throw new USearchException(Marshal.PtrToStringUTF8(error) ?? "USearch 调用失败。");
    }

    private sealed class FilterState(IReadOnlySet<ulong> allowedKeys, int limit, CancellationToken cancellationToken)
    {
        private int _visited;
        internal bool BudgetExceeded { get; private set; }
        internal Exception? Failure { get; private set; }

        internal int Accept(ulong key)
        {
            // 回调不能向原生栈抛出异常；取消/预算结果在同步调用返回后处理。
            try
            {
                if (cancellationToken.IsCancellationRequested || BudgetExceeded || Failure is not null)
                    return 0;
                if (++_visited > limit)
                {
                    BudgetExceeded = true;
                    return 0;
                }
                return allowedKeys.Contains(key) ? 1 : 0;
            }
            catch (Exception exception)
            {
                Failure = exception;
                return 0;
            }
        }
    }

    private sealed class IndexHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal IndexHandle(nint pointer) : base(true)
        {
            SetHandle(pointer);
        }

        protected override bool ReleaseHandle()
        {
            Native.Free(handle, out nint error);
            return error == 0;
        }
    }

    private static class Native
    {
        private const string Library = "libusearch_c";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int Filter(ulong key, nint state);

        [DllImport(Library, EntryPoint = "usearch_init", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Init(ref IndexOptions options, out nint error);

        [DllImport(Library, EntryPoint = "usearch_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Free(nint index, out nint error);

        [DllImport(Library, EntryPoint = "usearch_reserve", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Reserve(IndexHandle index, nuint capacity, out nint error);

        [DllImport(Library, EntryPoint = "usearch_change_expansion_search", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ChangeExpansionSearch(IndexHandle index, nuint expansion, out nint error);

        [DllImport(Library, EntryPoint = "usearch_add", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Add(IndexHandle index, ulong key, [In] float[] vector, ScalarKind kind, out nint error);

        [DllImport(Library, EntryPoint = "usearch_remove", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint Remove(IndexHandle index, ulong key, out nint error);

        [DllImport(Library, EntryPoint = "usearch_search", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint Search(IndexHandle index, [In] float[] query, ScalarKind kind, nuint count,
            [Out] ulong[] keys, [Out] float[] distances, out nint error);

        [DllImport(Library, EntryPoint = "usearch_filtered_search", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint FilteredSearch(IndexHandle index, [In] float[] query, ScalarKind kind, nuint count,
            Filter filter, nint state, [Out] ulong[] keys, [Out] float[] distances, out nint error);
    }
}
