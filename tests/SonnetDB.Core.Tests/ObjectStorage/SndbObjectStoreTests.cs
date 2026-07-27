using System.Text;
using SonnetDB.Engine;
using SonnetDB.Kv;
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
        Assert.Equal(expected.LongLength, readResult.TotalLength);
    }

    /// <summary>
    /// 验证范围读取同时返回分段长度和完整对象长度。
    /// </summary>
    [Fact]
    public async Task OpenRead_WithRange_ReturnsOffsetLengthAndTotalLength()
    {
        byte[] expected = Encoding.UTF8.GetBytes("0123456789");
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        await store.PutObjectAsync(
            "test-bucket",
            "videos/range.bin",
            new MemoryStream(expected, writable: false));

        var readResult = Assert.IsType<SndbObjectReadResult>(
            store.OpenRead("test-bucket", "videos/range.bin", new SndbObjectRange(3, 4)));
        await using var content = readResult.Content;
        using var actual = new MemoryStream();
        await content.CopyToAsync(actual);

        Assert.Equal("3456", Encoding.UTF8.GetString(actual.ToArray()));
        Assert.Equal(3, readResult.Offset);
        Assert.Equal(4, readResult.Length);
        Assert.Equal(expected.LongLength, readResult.TotalLength);
        Assert.True(readResult.IsRange);
    }

    /// <summary>
    /// 验证六参数构造函数、可选参数默认值和六元素解构保持可用。
    /// </summary>
    [Fact]
    public void SndbObjectReadResult_SixParameterApi_RemainsAvailable()
    {
        var info = new SndbObjectInfo(
            "test-bucket",
            "videos/legacy.bin",
            "v1",
            "application/octet-stream",
            42,
            "etag",
            "sha256",
            IsDeleteMarker: false,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var result = new SndbObjectReadResult(info, Stream.Null, 3, 4, IsRange: true, TotalLength: 42);
        var (actualInfo, actualContent, offset, length, isRange, totalLength) = result;
        var defaultResult = new SndbObjectReadResult(info, Stream.Null, 0, 42, IsRange: false);

        Assert.Same(info, actualInfo);
        Assert.Same(Stream.Null, actualContent);
        Assert.Equal(3, offset);
        Assert.Equal(4, length);
        Assert.True(isRange);
        Assert.Equal(42, totalLength);
        Assert.Equal(0, defaultResult.TotalLength);
    }

    /// <summary>
    /// 验证改造前的五参数构造函数和五元素解构继续保持二进制与源码兼容。
    /// </summary>
    [Fact]
    public void SndbObjectReadResult_LegacyFiveParameterApi_RemainsAvailable()
    {
        var info = new SndbObjectInfo(
            "test-bucket",
            "videos/legacy.bin",
            "v1",
            "application/octet-stream",
            42,
            "etag",
            "sha256",
            IsDeleteMarker: false,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var result = new SndbObjectReadResult(info, Stream.Null, 3, 4, true);
        var (actualInfo, actualContent, offset, length, isRange) = result;

        Assert.Same(info, actualInfo);
        Assert.Same(Stream.Null, actualContent);
        Assert.Equal(3, offset);
        Assert.Equal(4, length);
        Assert.True(isRange);
        Assert.Equal(0, result.TotalLength);
        var legacyConstructor = Assert.Single(
            typeof(SndbObjectReadResult).GetConstructors(),
            static constructor => constructor.GetParameters().Length == 5);
        var legacyDeconstruct = Assert.Single(
            typeof(SndbObjectReadResult).GetMethods(),
            static method => method.Name == nameof(SndbObjectReadResult.Deconstruct)
                && method.GetParameters().Length == 5);
        string[] expectedParameterNames = ["Info", "Content", "Offset", "Length", "IsRange"];
        Assert.Equal(expectedParameterNames, legacyConstructor.GetParameters().Select(static parameter => parameter.Name));
        Assert.Equal(expectedParameterNames, legacyDeconstruct.GetParameters().Select(static parameter => parameter.Name));
    }

    /// <summary>
    /// 验证元数据原子批次在 WAL 预算拒绝时不会遗留最终对象或可见索引。
    /// </summary>
    [Fact]
    public async Task PutObjectAsync_MetadataBatchRejected_RemovesPublishedFileAndMetadata()
    {
        var options = new TsdbOptions
        {
            RootDirectory = _rootDirectory,
            Kv = KvOptions.Default with
            {
                MaxWalBytes = 4 * 1024,
                MaxOverlayEntries = int.MaxValue,
            },
        };
        using var db = Tsdb.Open(options);
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        var oversizedMetadata = new Dictionary<string, string>
        {
            ["description"] = new string('x', 8 * 1024),
        };

        IOException error = await Assert.ThrowsAsync<IOException>(() => store.PutObjectAsync(
            "test-bucket",
            "videos/rejected.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("complete content"), writable: false),
            metadata: oversizedMetadata));

        Assert.Contains("before WAL append", error.Message, StringComparison.Ordinal);
        Assert.Empty(GetObjectFiles());
        Assert.Null(store.HeadObject("test-bucket", "videos/rejected.bin"));
        Assert.Empty(store.ListObjectVersions("test-bucket", "videos/rejected.bin").Versions);
    }

    /// <summary>
    /// 验证 WAL 同步结果不确定时保留完整对象，并由重启恢复原子元数据批次。
    /// </summary>
    [Fact]
    public async Task PutObjectAsync_MetadataSyncFailure_PreservesContentForRecovery()
    {
        byte[] expectedContent = Encoding.UTF8.GetBytes("recoverable content");
        var expectedError = new InvalidOperationException("simulated metadata sync failure");
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory }))
        {
            var store = new SndbObjectStore(db);
            store.CreateBucket("test-bucket");
            KvKeyspace metadata = db.Keyspaces.Open("__object_storage");
            metadata.WalSyncTestHook = () => throw expectedError;

            InvalidOperationException actualError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.PutObjectAsync(
                    "test-bucket",
                    "videos/recoverable.bin",
                    new MemoryStream(expectedContent, writable: false)));

            Assert.Same(expectedError, actualError);
            Assert.Single(GetObjectFiles());
            metadata.WalSyncTestHook = null;
        }

        using var recoveredDb = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var recoveredStore = new SndbObjectStore(recoveredDb);
        var readResult = Assert.IsType<SndbObjectReadResult>(
            recoveredStore.OpenRead("test-bucket", "videos/recoverable.bin"));
        await using var content = readResult.Content;
        using var actualContent = new MemoryStream();
        await content.CopyToAsync(actualContent);
        Assert.Equal(expectedContent, actualContent.ToArray());
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
