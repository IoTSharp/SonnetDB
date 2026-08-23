using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace SonnetDB.Graphs;

internal sealed class GraphAlgorithmLongVector : IDisposable
{
    private const int ValueSize = sizeof(long);
    private readonly FileStream _stream;
    private readonly SafeFileHandle _handle;
    private readonly long[]? _memory;
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

        Span<byte> encoded = stackalloc byte[ValueSize];
        ReadExactly(encoded, checked(index * ValueSize));
        return BinaryPrimitives.ReadInt64LittleEndian(encoded);
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

        Span<byte> encoded = stackalloc byte[ValueSize];
        BinaryPrimitives.WriteInt64LittleEndian(encoded, value);
        RandomAccess.Write(_handle, encoded, checked(index * ValueSize));
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

    private void ValidateIndex(long index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((ulong)index >= (ulong)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}
