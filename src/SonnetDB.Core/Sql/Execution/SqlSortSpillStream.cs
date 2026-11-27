namespace SonnetDB.Sql.Execution;

/// <summary>为顺序写入的排序 run 在每次增长之前预留实际编码后的字节数。</summary>
internal sealed class SqlSortSpillStream(string path, SqlSpillWorkspace workspace) : Stream
{
    private readonly FileStream _file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
    private long _length;
    private long _position;

    public override bool CanRead => false;
    public override bool CanSeek => _file.CanSeek;
    public override bool CanWrite => _file.CanWrite;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            _file.Position = value;
            _position = value;
        }
    }

    public override void Flush() => _file.Flush();

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = _file.Seek(offset, origin);
        return _position;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        long end = checked(_position + buffer.Length);
        if (end > _length)
        {
            workspace.ReserveSortBytes(end - _length);
            _length = end;
        }
        _file.Write(buffer);
        _position = end;
    }

    public override void WriteByte(byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        Write(buffer);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _file.Dispose();
        base.Dispose(disposing);
    }
}
