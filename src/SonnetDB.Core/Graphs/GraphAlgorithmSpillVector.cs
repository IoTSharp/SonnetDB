using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace SonnetDB.Graphs;

internal sealed class GraphAlgorithmLongVector : IDisposable
{
    private const int ValueSize = sizeof(long);
    private const int PageBytes = 64 * 1024;
    private readonly FileStream _stream;
    private readonly SafeFileHandle _handle;
    private readonly long[]? _memory;
    private readonly byte[]? _page;
    private readonly long _pageValueCount = 0;
    private long _pageStart = -1;
    private int _pageValues;
    private bool _pageDirty;
    private bool _dirty;
    private bool _disposed;

    private GraphAlgorithmLongVector(string path, long count, long memoryBudgetBytes, bool create)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        long length = checked(count * ValueSize);
        _stream = new FileStream(
            path,
            create ? FileMode.Create : FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            64 * 1024,
            FileOptions.RandomAccess);
        if (create)
            _stream.SetLength(length);
        else if (_stream.Length != length)
            throw new InvalidDataException($"Graph algorithm vector '{path}' 长度无效。");
        _handle = _stream.SafeFileHandle;
        Count = count;

        if (length <= memoryBudgetBytes && count <= Array.MaxLength)
        {
            _memory = new long[checked((int)count)];
            if (!create && count > 0)
                ReadAll(_memory);
        }
        else
        {
            // Keep a bounded page cache for spill vectors. Offline algorithms repeatedly
            // touch nearby vertex state; one read/write per value amplified random I/O.
            long pageBytes = Math.Max(ValueSize, Math.Min((long)PageBytes, memoryBudgetBytes));
            _pageValueCount = Math.Max(1, pageBytes / ValueSize);
            _page = new byte[checked((int)Math.Min(_pageValueCount * ValueSize, length))];
        }
    }

    internal long Count { get; }

    internal static GraphAlgorithmLongVector Create(string path, long count, long memoryBudgetBytes)
        => new(path, count, memoryBudgetBytes, create: true);

    internal static GraphAlgorithmLongVector Open(string path, long count, long memoryBudgetBytes)
        => new(path, count, memoryBudgetBytes, create: false);

    internal long Get(long index)
    {
        ValidateIndex(index);
        if (_memory is not null)
            return _memory[checked((int)index)];

        EnsurePage(index);
        return BinaryPrimitives.ReadInt64LittleEndian(
            _page!.AsSpan(checked((int)((index - _pageStart) * ValueSize)), ValueSize));
    }

    internal void Set(long index, long value)
    {
        ValidateIndex(index);
        if (_memory is not null)
        {
            _memory[checked((int)index)] = value;
            _dirty = true;
            return;
        }

        EnsurePage(index);
        BinaryPrimitives.WriteInt64LittleEndian(
            _page!.AsSpan(checked((int)((index - _pageStart) * ValueSize)), ValueSize),
            value);
        _pageDirty = true;
        _dirty = true;
    }

    internal double GetDouble(long index)
        => BitConverter.Int64BitsToDouble(Get(index));

    internal void SetDouble(long index, double value)
        => Set(index, BitConverter.DoubleToInt64Bits(value));

    internal long BinarySearch(long value)
    {
        long low = 0;
        long high = Count - 1;
        while (low <= high)
        {
            long middle = low + ((high - low) >> 1);
            long candidate = Get(middle);
            if (candidate == value)
                return middle;
            if (candidate < value)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return -1;
    }

    internal void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_dirty)
            return;
        if (_memory is not null)
            WriteAll(_memory);
        else
            FlushPage();
        _stream.Flush(flushToDisk: true);
        _dirty = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            Flush();
        }
        finally
        {
            _disposed = true;
            _pageStart = -1;
            _stream.Dispose();
        }
    }

    private void ReadAll(long[] values)
    {
        byte[] buffer = new byte[64 * 1024];
        long offset = 0;
        int valueIndex = 0;
        while (valueIndex < values.Length)
        {
            int valueCount = Math.Min(buffer.Length / ValueSize, values.Length - valueIndex);
            Span<byte> bytes = buffer.AsSpan(0, valueCount * ValueSize);
            ReadExactly(bytes, offset);
            for (int index = 0; index < valueCount; index++)
            {
                values[valueIndex + index] = BinaryPrimitives.ReadInt64LittleEndian(
                    bytes.Slice(index * ValueSize, ValueSize));
            }
            valueIndex += valueCount;
            offset += bytes.Length;
        }
    }

    private void WriteAll(long[] values)
    {
        byte[] buffer = new byte[64 * 1024];
        long offset = 0;
        int valueIndex = 0;
        while (valueIndex < values.Length)
        {
            int valueCount = Math.Min(buffer.Length / ValueSize, values.Length - valueIndex);
            Span<byte> bytes = buffer.AsSpan(0, valueCount * ValueSize);
            for (int index = 0; index < valueCount; index++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(
                    bytes.Slice(index * ValueSize, ValueSize),
                    values[valueIndex + index]);
            }
            RandomAccess.Write(_handle, bytes, offset);
            valueIndex += valueCount;
            offset += bytes.Length;
        }
    }

    private void ReadExactly(Span<byte> destination, long offset)
    {
        int consumed = 0;
        while (consumed < destination.Length)
        {
            int read = RandomAccess.Read(_handle, destination[consumed..], offset + consumed);
            if (read == 0)
                throw new EndOfStreamException("Graph algorithm vector 被截断。");
            consumed += read;
        }
    }

    private void EnsurePage(long index)
    {
        if (_page is null)
            throw new InvalidOperationException("Graph algorithm spill page 未初始化。");
        long pageStart = index - (index % _pageValueCount);
        if (_pageStart == pageStart)
            return;

        FlushPage();
        _pageStart = pageStart;
        _pageValues = checked((int)Math.Min(_pageValueCount, Count - pageStart));
        int bytes = checked(_pageValues * ValueSize);
        Array.Clear(_page, 0, _page.Length);
        ReadExactly(_page.AsSpan(0, bytes), checked(pageStart * ValueSize));
    }

    private void FlushPage()
    {
        if (!_pageDirty || _page is null || _pageStart < 0)
            return;
        int bytes = checked(_pageValues * ValueSize);
        RandomAccess.Write(_handle, _page.AsSpan(0, bytes), checked(_pageStart * ValueSize));
        _pageDirty = false;
    }

    private void ValidateIndex(long index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((ulong)index >= (ulong)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}
