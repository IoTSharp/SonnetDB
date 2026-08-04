using System.Text;

namespace SonnetDB.Tables;

/// <summary>
/// 索引恢复专用的临时顺序 spool。文件使用操作系统临时目录与 DeleteOnClose，
/// 仅服务当前打开流程，不属于 SonnetDB 持久格式，也不承诺跨版本兼容。
/// </summary>
internal sealed class TableIndexRepairSpool : IDisposable
{
    private const byte PutOperation = 1;
    private const byte DeleteOperation = 2;
    private const int MaxFieldBytes = 64 * 1024 * 1024;
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private bool _sealed;
    private bool _disposed;

    /// <summary>创建由当前进程句柄托管的 DeleteOnClose 临时 spool。</summary>
    internal TableIndexRepairSpool()
    {
        TemporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"sonnetdb-index-repair-{Guid.NewGuid():N}.tmp");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(TemporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.DeleteOnClose | FileOptions.SequentialScan,
            });
            _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            _stream = stream;
        }
        catch
        {
            stream?.Dispose();
            TryDeleteTemporaryFile(TemporaryPath);
            throw;
        }
    }

    /// <summary>仅供异常清理测试核对的临时文件路径。</summary>
    internal string TemporaryPath { get; }

    /// <summary>当前 spool 中的 mutation 数量。</summary>
    internal int Count { get; private set; }

    /// <summary>顺序追加一个待补写或覆盖的索引条目。</summary>
    /// <param name="key">索引 key。</param>
    /// <param name="value">索引指向的主键。</param>
    /// <param name="uniqueIndexOrdinal">唯一索引在当前 schema 中的序号；非唯一索引为 -1。</param>
    internal void AppendPut(byte[] key, byte[] value, int uniqueIndexOrdinal)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        EnsureWritable();
        _writer.Write(PutOperation);
        _writer.Write(uniqueIndexOrdinal);
        WriteBytes(key);
        WriteBytes(value);
        Count = checked(Count + 1);
    }

    /// <summary>顺序追加一个待删除的索引 key。</summary>
    /// <param name="key">确认不再有效的索引 key。</param>
    internal void AppendDelete(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureWritable();
        _writer.Write(DeleteOperation);
        _writer.Write(-1);
        WriteBytes(key);
        Count = checked(Count + 1);
    }

    /// <summary>从文件起点按固定页回放 mutation，内存中最多保留一页。</summary>
    /// <param name="pageSize">每次回调最多包含的 mutation 数。</param>
    /// <param name="applyPage">同步消费一页 mutation 的回调。</param>
    internal void ReplayPages(
        int pageSize,
        Action<IReadOnlyList<TableIndexRepairSpoolEntry>> applyPage)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentNullException.ThrowIfNull(applyPage);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sealed = true;
        _writer.Flush();
        _stream.Position = 0;

        using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        var page = new List<TableIndexRepairSpoolEntry>(pageSize);
        while (_stream.Position < _stream.Length)
        {
            page.Add(ReadEntry(reader));
            if (page.Count < pageSize)
                continue;

            applyPage(page);
            page = new List<TableIndexRepairSpoolEntry>(pageSize);
        }

        if (page.Count > 0)
            applyPage(page);
    }

    /// <summary>关闭句柄并确保异常路径也尽力删除临时 spool。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _writer.Dispose();
        }
        finally
        {
            try
            {
                _stream.Dispose();
            }
            finally
            {
                TryDeleteTemporaryFile(TemporaryPath);
            }
        }
    }

    /// <summary>写入一个有界长度前缀字节字段。</summary>
    private void WriteBytes(byte[] value)
    {
        if (value.Length > MaxFieldBytes)
            throw new InvalidOperationException("Index repair spool field exceeds the internal size limit.");
        _writer.Write(value.Length);
        _writer.Write(value);
    }

    /// <summary>读取并校验一个完整 mutation record。</summary>
    private static TableIndexRepairSpoolEntry ReadEntry(BinaryReader reader)
    {
        byte operation = reader.ReadByte();
        int uniqueIndexOrdinal = reader.ReadInt32();
        byte[] key = ReadBytes(reader);
        return operation switch
        {
            PutOperation => new TableIndexRepairSpoolEntry(
                IsDelete: false,
                key,
                ReadBytes(reader),
                uniqueIndexOrdinal),
            DeleteOperation when uniqueIndexOrdinal == -1 => new TableIndexRepairSpoolEntry(
                IsDelete: true,
                key,
                Value: null,
                UniqueIndexOrdinal: -1),
            _ => throw new InvalidDataException("Index repair spool operation is invalid."),
        };
    }

    /// <summary>读取一个有界长度字段，并拒绝截断的临时文件。</summary>
    private static byte[] ReadBytes(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaxFieldBytes)
            throw new InvalidDataException("Index repair spool field length is invalid.");

        byte[] value = reader.ReadBytes(length);
        if (value.Length != length)
            throw new EndOfStreamException("Index repair spool ended inside a field.");
        return value;
    }

    /// <summary>拒绝释放后或已经开始回放后的追加。</summary>
    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed)
            throw new InvalidOperationException("Index repair spool is already sealed for replay.");
    }

    /// <summary>在 DeleteOnClose 不可用或构造失败时执行尽力清理。</summary>
    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件清理由 DeleteOnClose 兜底；这里不得掩盖原始恢复异常。
        }
    }
}

/// <summary>从临时 spool 回放的一条索引恢复 mutation。</summary>
internal sealed record TableIndexRepairSpoolEntry(
    bool IsDelete,
    byte[] Key,
    byte[]? Value,
    int UniqueIndexOrdinal);
