using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace SonnetDB.Kv;

/// <summary>在一页索引内复用邻近文件数据，避免每条键的头、后缀与 CRC 都发起独立系统调用。</summary>
internal sealed class KvIndexPageReader : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly CancellationToken _cancellationToken;
    private readonly byte[] _buffer;
    private long _offset = -1;
    private int _length;

    /// <summary>复用磁盘状态持有的句柄；读取器仅拥有临时缓冲。</summary>
    internal KvIndexPageReader(SafeFileHandle handle, CancellationToken cancellationToken, int bufferSize = 4096)
    {
        _handle = handle;
        _cancellationToken = cancellationToken;
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    internal long BytesRead { get; private set; }

    /// <summary>按明确偏移读取元数据，跨大值直接跳转；不把对象正文整体预读到内存。</summary>
    internal int Read(Span<byte> destination, long offset)
    {
        int copied = 0;
        for (int step = 0; copied < destination.Length && step <= destination.Length; step++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            long position = checked(offset + copied);
            if (position < _offset || position >= checked(_offset + _length))
            {
                _offset = position;
                _length = RandomAccess.Read(_handle, _buffer, position);
                BytesRead = checked(BytesRead + _length);
                if (_length == 0) break;
            }
            int available = _length - checked((int)(position - _offset));
            int take = Math.Min(available, destination.Length - copied);
            _buffer.AsSpan(checked((int)(position - _offset)), take).CopyTo(destination[copied..]);
            copied += take;
        }
        return copied;
    }

    /// <summary>归还本次页面读取的临时缓冲，不关闭共享文件句柄。</summary>
    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}
