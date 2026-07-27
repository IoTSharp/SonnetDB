using System.Text;
using SonnetDB.Engine;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Core.Tests.ObjectStorage;

public sealed class SndbObjectStoreTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SonnetDB.ObjectStorage.Tests.{Guid.NewGuid():N}");

    /// <summary>
    /// 创建隔离的对象存储测试目录。
    /// </summary>
    public SndbObjectStoreTests()
    {
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <summary>
    /// 清理测试数据库及对象文件。
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 验证成功写入仅发布完整的最终文件。
    /// </summary>
    [Fact]
    public async Task PutObjectAsync_Success_MovesCompleteFileWithoutTemporaryArtifact()
    {
        byte[] expected = Encoding.UTF8.GetBytes("complete object payload");
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");

        await store.PutObjectAsync(
            "test-bucket",
            "videos/sample.bin",
            new MemoryStream(expected, writable: false));

        string[] files = GetObjectFiles();
        string finalPath = Assert.Single(files);
        Assert.EndsWith(".bin", finalPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(files, static path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

        var readResult = Assert.IsType<SndbObjectReadResult>(store.OpenRead("test-bucket", "videos/sample.bin"));
        await using var content = readResult.Content;
        using var actual = new MemoryStream();
        await content.CopyToAsync(actual);
        Assert.Equal(expected, actual.ToArray());
    }

    /// <summary>
    /// 验证部分写入后取消会清理临时文件且不发布最终文件。
    /// </summary>
    [Fact]
    public async Task PutObjectAsync_Cancellation_RemovesTemporaryAndFinalFiles()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        using var cancellation = new CancellationTokenSource();
        using var content = InterruptingReadStream.CancelAfterFirstRead(
            Encoding.UTF8.GetBytes("partially written content"),
            cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutObjectAsync(
            "test-bucket",
            "videos/canceled.bin",
            content,
            cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Empty(GetObjectFiles());
        Assert.Null(store.HeadObject("test-bucket", "videos/canceled.bin"));
    }

    /// <summary>
    /// 验证部分写入后读取异常会清理临时文件且不发布最终文件。
    /// </summary>
    [Fact]
    public async Task PutObjectAsync_ReadFailure_RemovesTemporaryAndFinalFiles()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        var expected = new IOException("Injected read failure.");
        using var content = InterruptingReadStream.FailAfterFirstRead(
            Encoding.UTF8.GetBytes("partially written content"),
            expected);

        IOException actual = await Assert.ThrowsAsync<IOException>(() => store.PutObjectAsync(
            "test-bucket",
            "videos/failed.bin",
            content));

        Assert.Same(expected, actual);
        Assert.Empty(GetObjectFiles());
        Assert.Null(store.HeadObject("test-bucket", "videos/failed.bin"));
    }

    /// <summary>
    /// 枚举对象内容目录中的全部文件。
    /// </summary>
    private string[] GetObjectFiles()
    {
        string objectRoot = Path.Combine(_rootDirectory, "objects");
        return Directory.Exists(objectRoot)
            ? Directory.GetFiles(objectRoot, "*", SearchOption.AllDirectories)
            : [];
    }

    private sealed class InterruptingReadStream : Stream
    {
        private readonly byte[] _firstChunk;
        private readonly CancellationTokenSource? _cancellation;
        private readonly IOException? _failure;
        private bool _firstRead = true;

        /// <summary>
        /// 构造在第二次读取时中断的测试流。
        /// </summary>
        private InterruptingReadStream(
            byte[] firstChunk,
            CancellationTokenSource? cancellation,
            IOException? failure)
        {
            _firstChunk = firstChunk;
            _cancellation = cancellation;
            _failure = failure;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// 创建在首次读取后取消调用的测试流。
        /// </summary>
        public static InterruptingReadStream CancelAfterFirstRead(
            byte[] firstChunk,
            CancellationTokenSource cancellation)
        {
            return new InterruptingReadStream(firstChunk, cancellation, failure: null);
        }

        /// <summary>
        /// 创建在首次读取后抛出读取异常的测试流。
        /// </summary>
        public static InterruptingReadStream FailAfterFirstRead(byte[] firstChunk, IOException failure)
        {
            return new InterruptingReadStream(firstChunk, cancellation: null, failure);
        }

        /// <summary>
        /// 刷新测试流；该只读流没有待刷新内容。
        /// </summary>
        public override void Flush()
        {
        }

        /// <summary>
        /// 同步读取不用于当前测试。
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 首次返回部分内容，第二次按测试场景取消或抛出异常。
        /// </summary>
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                _firstChunk.CopyTo(buffer);
                return ValueTask.FromResult(_firstChunk.Length);
            }

            if (_cancellation is not null)
            {
                _cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_failure is not null)
                throw _failure;

            throw new InvalidOperationException("The test stream was not configured to interrupt reads.");
        }

        /// <summary>
        /// 定位操作不受该只读测试流支持。
        /// </summary>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 调整长度不受该只读测试流支持。
        /// </summary>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 写入操作不受该只读测试流支持。
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
