using System.Text;
using System.Text.Json;
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
    /// 验证 multipart 分片写入取消后不会遗留未引用的分片正文。
    /// </summary>
    [Fact]
    public async Task UploadPartAsync_Cancellation_RemovesUnpublishedPartFile()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        SndbMultipartUploadInfo upload = store.InitiateMultipartUpload("test-bucket", "videos/canceled.bin");
        using var cancellation = new CancellationTokenSource();
        using var content = InterruptingReadStream.CancelAfterFirstRead(
            Encoding.UTF8.GetBytes("partially written content"),
            cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UploadPartAsync(
            upload.UploadId,
            1,
            content,
            cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Empty(store.GetMultipartUpload(upload.UploadId).Parts);
        Assert.Empty(GetObjectFiles());
    }

    /// <summary>
    /// 验证替换分片时旧路径损坏不会把已成功发布的新分片反向报告为失败。
    /// </summary>
    [Fact]
    public async Task UploadPartAsync_ReplacingCorruptedPartPath_PublishesReplacementAndPreservesExternalFile()
    {
        const string bucket = "test-bucket";
        const string key = "videos/replaced.bin";
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket(bucket);
        SndbMultipartUploadInfo upload = store.InitiateMultipartUpload(bucket, key);
        await store.UploadPartAsync(
            upload.UploadId,
            1,
            new MemoryStream(Encoding.UTF8.GetBytes("old"), writable: false));

        string siblingDirectory = Path.Combine(_rootDirectory, "objects-sibling");
        Directory.CreateDirectory(siblingDirectory);
        string siblingPath = Path.Combine(siblingDirectory, "must-remain.bin");
        File.WriteAllText(siblingPath, "must-not-be-deleted");
        CorruptMultipartPartStoragePath(db, upload.UploadId, 1, "../objects-sibling/must-remain.bin");

        await store.UploadPartAsync(
            upload.UploadId,
            1,
            new MemoryStream(Encoding.UTF8.GetBytes("replacement"), writable: false));
        await store.CompleteMultipartUploadAsync(upload.UploadId, [1]);

        var readResult = Assert.IsType<SndbObjectReadResult>(store.OpenRead(bucket, key));
        await using (readResult.Content)
        {
            using var content = new MemoryStream();
            await readResult.Content.CopyToAsync(content);
            Assert.Equal("replacement", Encoding.UTF8.GetString(content.ToArray()));
        }
        Assert.Equal("must-not-be-deleted", File.ReadAllText(siblingPath));
    }

    /// <summary>
    /// 验证中止上传时损坏的分片路径不会阻断状态提交或触碰外部文件。
    /// </summary>
    [Fact]
    public async Task AbortMultipartUpload_CorruptedPartPath_CompletesAndPreservesExternalFile()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        SndbMultipartUploadInfo upload = store.InitiateMultipartUpload("test-bucket", "videos/aborted.bin");
        await store.UploadPartAsync(
            upload.UploadId,
            1,
            new MemoryStream(Encoding.UTF8.GetBytes("part"), writable: false));

        string siblingDirectory = Path.Combine(_rootDirectory, "objects-sibling");
        Directory.CreateDirectory(siblingDirectory);
        string siblingPath = Path.Combine(siblingDirectory, "must-remain.bin");
        File.WriteAllText(siblingPath, "must-not-be-deleted");
        CorruptMultipartPartStoragePath(db, upload.UploadId, 1, "../objects-sibling/must-remain.bin");

        store.AbortMultipartUpload(upload.UploadId);

        SndbMultipartUploadSessionInfo session = store.GetMultipartUpload(upload.UploadId);
        Assert.Equal("aborted", session.Status);
        Assert.Empty(session.Parts);
        Assert.Equal("must-not-be-deleted", File.ReadAllText(siblingPath));
    }

    /// <summary>
    /// 验证 multipart 合并取消后不会遗留未引用的对象正文。
    /// </summary>
    [Fact]
    public async Task CompleteMultipartUploadAsync_Cancellation_RemovesUnpublishedObjectFile()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        SndbMultipartUploadInfo upload = store.InitiateMultipartUpload("test-bucket", "videos/canceled.bin");
        await store.UploadPartAsync(
            upload.UploadId,
            1,
            new MemoryStream(Encoding.UTF8.GetBytes("multipart content"), writable: false));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompleteMultipartUploadAsync(
            upload.UploadId,
            [1],
            cancellation.Token));

        Assert.Null(store.HeadObject("test-bucket", "videos/canceled.bin"));
        Assert.Single(store.GetMultipartUpload(upload.UploadId).Parts);
        Assert.DoesNotContain(
            GetObjectFiles(),
            path => path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 验证 multipart 完成批次在 WAL 同步结果不确定时，重启后不会同时留下可见对象和活动上传。
    /// </summary>
    [Fact]
    public async Task CompleteMultipartUploadAsync_MetadataSyncFailure_RecoversOneAtomicState()
    {
        const string bucket = "test-bucket";
        const string key = "videos/recovered.bin";
        byte[] first = Encoding.UTF8.GetBytes("first-");
        byte[] second = Encoding.UTF8.GetBytes("second");
        string uploadId;
        var expectedError = new InvalidOperationException("simulated multipart completion sync failure");

        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory }))
        {
            var store = new SndbObjectStore(db);
            store.CreateBucket(bucket);
            SndbMultipartUploadInfo upload = store.InitiateMultipartUpload(bucket, key);
            uploadId = upload.UploadId;
            await store.UploadPartAsync(uploadId, 1, new MemoryStream(first, writable: false));
            await store.UploadPartAsync(uploadId, 2, new MemoryStream(second, writable: false));

            KvKeyspace metadata = db.Keyspaces.Open("__object_storage");
            metadata.WalSyncTestHook = () => throw expectedError;
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CompleteMultipartUploadAsync(uploadId, [1, 2]));
            Assert.Same(expectedError, error);
        }

        using var recoveredDb = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var recoveredStore = new SndbObjectStore(recoveredDb);
        SndbMultipartUploadSessionInfo session = recoveredStore.GetMultipartUpload(uploadId);
        Assert.Equal("completed", session.Status);
        Assert.Empty(session.Parts);

        var readResult = Assert.IsType<SndbObjectReadResult>(recoveredStore.OpenRead(bucket, key));
        await using (readResult.Content)
        {
            using var content = new MemoryStream();
            await readResult.Content.CopyToAsync(content);
            Assert.Equal(first.Concat(second), content.ToArray());
        }

        Assert.Single(recoveredStore.ListAudit(bucket), static audit => audit.Action == "multipart.complete");
        SndbObjectStorageException retry = await Assert.ThrowsAsync<SndbObjectStorageException>(() =>
            recoveredStore.CompleteMultipartUploadAsync(uploadId, [1, 2]));
        Assert.Equal("multipart_not_active", retry.Code);
    }

    /// <summary>
    /// 验证生命周期单遍处理多个 key 时，仍按扫描开始时的当前版本快照区分三类过期对象。
    /// </summary>
    [Fact]
    public async Task ApplyLifecycle_MultipleKeysAndDeleteMarker_PreservesSnapshotClassification()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");

        var alphaV1 = await store.PutObjectAsync(
            "test-bucket",
            "alpha.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("alpha-v1"), writable: false));
        await WaitForClockAdvanceAsync(alphaV1.CreatedUtc);
        var alphaV2 = await store.PutObjectAsync(
            "test-bucket",
            "alpha.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("alpha-v2"), writable: false));

        var betaV1 = await store.PutObjectAsync(
            "test-bucket",
            "beta.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("beta-v1"), writable: false));
        await WaitForClockAdvanceAsync(betaV1.CreatedUtc);
        var betaDeleteMarker = store.DeleteObject("test-bucket", "beta.bin");

        var versionsBefore = store.ListObjectVersions("test-bucket").Versions;
        Assert.Equal(
            [alphaV2.VersionId, alphaV1.VersionId, betaDeleteMarker.VersionId, betaV1.VersionId],
            versionsBefore.Select(static version => version.VersionId));
        int versionListAuditsBeforeApply = store.ListAudit("test-bucket")
            .Count(static entry => entry.Action == "object.versions.list");

        store.SetLifecycle(
            "test-bucket",
            expireCurrentAfterDays: 0,
            expireNoncurrentAfterDays: 0,
            expireDeleteMarkerAfterDays: 0);

        var result = store.ApplyLifecycle("test-bucket");
        int versionListAuditsAfterApply = store.ListAudit("test-bucket")
            .Count(static entry => entry.Action == "object.versions.list");

        Assert.Equal(1, result.ExpiredCurrentObjects);
        Assert.Equal(2, result.RemovedNoncurrentVersions);
        Assert.Equal(1, result.RemovedDeleteMarkers);
        Assert.Equal(versionListAuditsBeforeApply + 1, versionListAuditsAfterApply);
        var expired = Assert.Single(result.ExpiredObjects);
        Assert.Equal("alpha.bin", expired.Key);
        Assert.Equal(alphaV2.VersionId, expired.VersionId);
        Assert.Equal(alphaV2.ContentType, expired.ContentType);

        var alphaVersions = store.ListObjectVersions("test-bucket", "alpha.bin").Versions;
        Assert.Equal(2, alphaVersions.Count);
        Assert.Contains(alphaVersions, version => version.VersionId == alphaV2.VersionId);
        Assert.Contains(alphaVersions, static version => version.IsDeleteMarker);
        Assert.DoesNotContain(alphaVersions, version => version.VersionId == alphaV1.VersionId);

        Assert.Empty(store.ListObjectVersions("test-bucket", "beta.bin").Versions);
        Assert.DoesNotContain(
            store.ListObjectVersions("test-bucket").Versions,
            version => version.VersionId == betaDeleteMarker.VersionId || version.VersionId == betaV1.VersionId);
    }

    /// <summary>
    /// 验证生命周期以 latest 指针而非创建时间排序区分当前对象。
    /// </summary>
    [Fact]
    public async Task ApplyLifecycle_UsesLatestPointerInsteadOfCreatedUtcOrdering()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        var first = await store.PutObjectAsync(
            "test-bucket",
            "pointer.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("first"), writable: false));
        await WaitForClockAdvanceAsync(first.CreatedUtc);
        var second = await store.PutObjectAsync(
            "test-bucket",
            "pointer.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("second"), writable: false));

        // 模拟恢复后的 latest 指针与记录创建时间不一致，生命周期必须服从指针。
        KvKeyspace metadata = db.Keyspaces.Open("__object_storage");
        metadata.Put(BuildLatestMetadataKey("test-bucket", "pointer.bin"), Encoding.UTF8.GetBytes(first.VersionId));
        store.SetLifecycle("test-bucket", expireCurrentAfterDays: 0, expireNoncurrentAfterDays: null, expireDeleteMarkerAfterDays: null);

        var result = store.ApplyLifecycle("test-bucket");

        var expired = Assert.Single(result.ExpiredObjects);
        Assert.Equal(first.VersionId, expired.VersionId);
        Assert.Null(store.HeadObject("test-bucket", "pointer.bin"));
        Assert.Contains(
            store.ListObjectVersions("test-bucket", "pointer.bin").Versions,
            version => version.VersionId == second.VersionId);
    }

    /// <summary>
    /// 验证生命周期元数据提交后遇到损坏的越界正文路径时，不会反向报错或触碰外部文件。
    /// </summary>
    [Fact]
    public async Task ApplyLifecycle_CorruptedStoragePath_CompletesMetadataCleanupWithoutDeletingExternalFile()
    {
        const string bucket = "test-bucket";
        const string key = "corrupted.bin";
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket(bucket);
        SndbObjectInfo first = await store.PutObjectAsync(
            bucket,
            key,
            new MemoryStream(Encoding.UTF8.GetBytes("first object content"), writable: false));
        SndbObjectInfo second = await store.PutObjectAsync(
            bucket,
            key,
            new MemoryStream(Encoding.UTF8.GetBytes("second object content"), writable: false));

        string siblingDirectory = Path.Combine(_rootDirectory, "objects-sibling");
        Directory.CreateDirectory(siblingDirectory);
        string siblingPath = Path.Combine(siblingDirectory, "must-remain.bin");
        File.WriteAllText(siblingPath, "must-not-be-deleted");

        KvKeyspace metadata = db.Keyspaces.Open("__object_storage");
        KvEntry objectEntry = Assert.Single(
            metadata.ScanPrefix($"object:{bucket}/", limit: int.MaxValue),
            entry => JsonSerializer.Deserialize(
                    entry.Value.Span,
                    SndbObjectStoreJsonContext.Default.SndbObjectRecord)!.VersionId == first.VersionId);
        SndbObjectRecord record = JsonSerializer.Deserialize(
            objectEntry.Value.Span,
            SndbObjectStoreJsonContext.Default.SndbObjectRecord)!;
        byte[] corrupted = JsonSerializer.SerializeToUtf8Bytes(
            record with { StoragePath = "../objects-sibling/must-remain.bin" },
            SndbObjectStoreJsonContext.Default.SndbObjectRecord);
        metadata.Put(Encoding.UTF8.GetString(objectEntry.Key.Span), corrupted);
        store.SetLifecycle(bucket, expireCurrentAfterDays: null, expireNoncurrentAfterDays: 0, expireDeleteMarkerAfterDays: null);

        SndbBucketLifecycleApplyResult result = store.ApplyLifecycle(bucket);

        Assert.Equal(1, result.RemovedNoncurrentVersions);
        SndbObjectInfo remaining = Assert.Single(store.ListObjectVersions(bucket, key).Versions);
        Assert.Equal(second.VersionId, remaining.VersionId);
        Assert.Equal("must-not-be-deleted", File.ReadAllText(siblingPath));
    }

    /// <summary>
    /// 验证空批量删除仍校验 bucket，避免把错误请求静默当作成功。
    /// </summary>
    [Fact]
    public void DeleteObjects_EmptyKeys_StillValidatesBucket()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);

        SndbObjectStorageException missing = Assert.Throws<SndbObjectStorageException>(
            () => store.DeleteObjects("missing-bucket", []));

        Assert.Equal("bucket_not_found", missing.Code);
        Assert.Throws<ArgumentException>(() => store.DeleteObjects(" ", []));
    }

    /// <summary>
    /// 验证内容写入期间删除 bucket 后，对象不能发布元数据或遗留正文。
    /// </summary>
    [Fact]
    public async Task PutObjectAsync_DeletedBucketDuringContentWrite_DoesNotPublishObject()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        using var content = BlockingReadStream.Create(Encoding.UTF8.GetBytes("payload"));

        Task<SndbObjectInfo> put = store.PutObjectAsync("test-bucket", "late.bin", content);
        await content.WaitUntilFirstReadAsync();
        Assert.True(store.DeleteBucket("test-bucket"));
        content.Release();

        SndbObjectStorageException error = await Assert.ThrowsAsync<SndbObjectStorageException>(() => put);
        Assert.Equal("bucket_not_found", error.Code);
        Assert.Empty(GetObjectFiles());
    }

    /// <summary>
    /// 验证正文写入期间删除并重建同名 bucket 时，旧写入不会发布到新 bucket。
    /// </summary>
    [Fact]
    public async Task PutObjectAsync_RecreatedBucketDuringContentWrite_DoesNotPublishObject()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        using var content = BlockingReadStream.Create(Encoding.UTF8.GetBytes("payload"));

        Task<SndbObjectInfo> put = store.PutObjectAsync("test-bucket", "late.bin", content);
        await content.WaitUntilFirstReadAsync();
        Assert.True(store.DeleteBucket("test-bucket"));
        store.CreateBucket("test-bucket");
        content.Release();

        SndbObjectStorageException error = await Assert.ThrowsAsync<SndbObjectStorageException>(() => put);
        Assert.Equal("bucket_recreated", error.Code);
        Assert.Null(store.HeadObject("test-bucket", "late.bin"));
        Assert.Empty(GetObjectFiles());
    }

    /// <summary>
    /// 验证中止并删除 bucket 后，正在写入的分片不能重新发布元数据或遗留正文。
    /// </summary>
    [Fact]
    public async Task UploadPartAsync_AbortedAndDeletedBucketDuringContentWrite_DoesNotPublishPart()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        SndbMultipartUploadInfo upload = store.InitiateMultipartUpload("test-bucket", "late.bin");
        using var content = BlockingReadStream.Create(Encoding.UTF8.GetBytes("multipart payload"));

        Task<SndbMultipartPartInfo> putPart = store.UploadPartAsync(upload.UploadId, 1, content);
        await content.WaitUntilFirstReadAsync();
        SndbObjectStorageException notEmpty = Assert.Throws<SndbObjectStorageException>(() => store.DeleteBucket("test-bucket"));
        Assert.Equal("bucket_not_empty", notEmpty.Code);
        store.AbortMultipartUpload(upload.UploadId);
        Assert.True(store.DeleteBucket("test-bucket"));
        content.Release();

        SndbObjectStorageException error = await Assert.ThrowsAsync<SndbObjectStorageException>(() => putPart);
        Assert.Equal("multipart_not_active", error.Code);
        Assert.Empty(GetObjectFiles());
    }

    /// <summary>
    /// 验证删除并重建 bucket 后不会继承旧配置，且旧预签名令牌立即失效。
    /// </summary>
    [Fact]
    public void DeleteBucket_RemovesConfigurationAndInvalidatesPresignedTokenBeforeRecreation()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket("test-bucket");
        store.SetPolicy("test-bucket", "{\"Version\":\"2012-10-17\"}");
        store.SetLifecycle("test-bucket", 1, 2, 3);
        store.SetRetention("test-bucket", 4, 5);
        store.SetQuota("test-bucket", 6_000, 7);
        store.SetSemanticOptions("test-bucket", true, true, 640, 480, 90);
        SndbPresignedObjectUrl presigned = store.CreatePresignedUrl(
            "https://example.test/objects/late.bin",
            "GET",
            "test-bucket",
            "late.bin",
            TimeSpan.FromHours(1));
        string token = Uri.UnescapeDataString(new Uri(presigned.Url).Query["?sndb-presigned=".Length..]);

        Assert.True(store.DeleteBucket("test-bucket"));
        store.CreateBucket("test-bucket");

        Assert.Null(store.GetPolicy("test-bucket").PolicyJson);
        SndbBucketLifecycleInfo lifecycle = store.GetLifecycle("test-bucket");
        Assert.Null(lifecycle.ExpireCurrentAfterDays);
        Assert.Null(lifecycle.ExpireNoncurrentAfterDays);
        Assert.Null(lifecycle.ExpireDeleteMarkerAfterDays);
        SndbBucketRetentionInfo retention = store.GetRetention("test-bucket");
        Assert.Null(retention.RetainCurrentForDays);
        Assert.Null(retention.RetainNoncurrentForDays);
        SndbBucketQuotaInfo quota = store.GetQuota("test-bucket");
        Assert.Null(quota.MaxSizeBytes);
        Assert.Null(quota.MaxObjectVersions);
        SndbBucketSemanticOptionsInfo semantic = store.GetSemanticOptions("test-bucket");
        Assert.False(semantic.AsyncIngestionEnabled);
        Assert.False(semantic.ThumbnailEnabled);
        Assert.False(store.TryValidatePresignedToken(token, "GET", "test-bucket", "late.bin"));
    }

    /// <summary>
    /// 验证升级前未保存 bucket 版本的预签名令牌在原 bucket 上继续有效。
    /// </summary>
    [Fact]
    public void TryValidatePresignedToken_LegacyRecordForOriginalBucket_RemainsValid()
    {
        const string bucket = "test-bucket";
        const string key = "legacy.bin";
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket(bucket);
        string token = CreateLegacyPresignedToken(db, store, bucket, key);

        Assert.True(store.TryValidatePresignedToken(token, "GET", bucket, key));
    }

    /// <summary>
    /// 验证升级前令牌不会在同名 bucket 删除重建后恢复权限。
    /// </summary>
    [Fact]
    public void TryValidatePresignedToken_LegacyRecordAfterBucketRecreated_IsRejected()
    {
        const string bucket = "test-bucket";
        const string key = "legacy.bin";
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket(bucket);
        string token = CreateLegacyPresignedToken(db, store, bucket, key);

        Assert.True(store.DeleteBucket(bucket));
        store.CreateBucket(bucket);

        Assert.False(store.TryValidatePresignedToken(token, "GET", bucket, key));
    }

    /// <summary>
    /// 验证删桶 WAL 同步异常后重启恢复整个清理批次，策略被清理且旧令牌失效。
    /// </summary>
    [Fact]
    public void DeleteBucket_MetadataSyncFailure_RecoversConfigurationAndTokenInvalidationAtomically()
    {
        const string bucket = "test-bucket";
        const string key = "late.bin";
        string token;
        var expectedError = new InvalidOperationException("simulated bucket deletion sync failure");

        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory }))
        {
            var store = new SndbObjectStore(db);
            store.CreateBucket(bucket);
            store.SetPolicy(bucket, "{\"Version\":\"2012-10-17\"}");
            SndbPresignedObjectUrl presigned = store.CreatePresignedUrl(
                "https://example.test/objects/late.bin",
                "GET",
                bucket,
                key,
                TimeSpan.FromHours(1));
            token = Uri.UnescapeDataString(new Uri(presigned.Url).Query["?sndb-presigned=".Length..]);

            KvKeyspace metadata = db.Keyspaces.Open("__object_storage");
            metadata.WalSyncTestHook = () => throw expectedError;
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => store.DeleteBucket(bucket));
            Assert.Same(expectedError, error);
        }

        using var recoveredDb = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var recoveredStore = new SndbObjectStore(recoveredDb);
        recoveredStore.CreateBucket(bucket);
        Assert.Null(recoveredStore.GetPolicy(bucket).PolicyJson);
        Assert.False(recoveredStore.TryValidatePresignedToken(token, "GET", bucket, key));
    }

    /// <summary>
    /// 验证大量预签名令牌已进入磁盘快照后，删桶批次仍受固定预算约束且同名重建不会复活旧令牌。
    /// </summary>
    [Fact]
    public void DeleteBucket_ManyPresignedTokens_DoesNotBuildUnboundedAtomicBatch()
    {
        const string bucket = "test-bucket";
        const string key = "large-token-set.bin";
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _rootDirectory,
            Kv = new KvOptions
            {
                MaxOverlayEntries = 8,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            },
        });
        var store = new SndbObjectStore(db);
        store.CreateBucket(bucket);
        var tokens = new List<string>();
        for (int index = 0; index < 24; index++)
        {
            SndbPresignedObjectUrl presigned = store.CreatePresignedUrl(
                "https://example.test/objects/large-token-set.bin",
                "GET",
                bucket,
                key,
                TimeSpan.FromHours(1));
            tokens.Add(Uri.UnescapeDataString(new Uri(presigned.Url).Query["?sndb-presigned=".Length..]));
        }

        KvKeyspace metadata = db.Keyspaces.Open("__object_storage");
        metadata.CreateSnapshot();
        Assert.All(tokens, token => Assert.True(store.TryValidatePresignedToken(token, "GET", bucket, key)));

        Assert.True(store.DeleteBucket(bucket));
        store.CreateBucket(bucket);

        Assert.All(tokens, token => Assert.False(store.TryValidatePresignedToken(token, "GET", bucket, key)));
    }

    /// <summary>验证损坏元数据不能借助内容根目录同名前缀访问兄弟目录文件。</summary>
    [Fact]
    public async Task OpenRead_StoragePathEscapesToSiblingPrefix_RejectsMetadata()
    {
        const string bucket = "test-bucket";
        const string key = "escape.bin";
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var store = new SndbObjectStore(db);
        store.CreateBucket(bucket);
        await store.PutObjectAsync(
            bucket,
            key,
            new MemoryStream(Encoding.UTF8.GetBytes("legitimate"), writable: false));

        string siblingDirectory = Path.Combine(_rootDirectory, "objects-sibling");
        Directory.CreateDirectory(siblingDirectory);
        string siblingPath = Path.Combine(siblingDirectory, "secret.bin");
        File.WriteAllText(siblingPath, "must-not-be-read");

        KvKeyspace metadata = db.Keyspaces.Open("__object_storage");
        KvEntry objectEntry = Assert.Single(metadata.ScanPrefix($"object:{bucket}/", limit: int.MaxValue));
        SndbObjectRecord record = JsonSerializer.Deserialize(
            objectEntry.Value.Span,
            SndbObjectStoreJsonContext.Default.SndbObjectRecord)!;
        byte[] corrupted = JsonSerializer.SerializeToUtf8Bytes(
            record with { StoragePath = "../objects-sibling/secret.bin" },
            SndbObjectStoreJsonContext.Default.SndbObjectRecord);
        metadata.Put(Encoding.UTF8.GetString(objectEntry.Key.Span), corrupted);

        SndbObjectStorageException error = Assert.Throws<SndbObjectStorageException>(() => store.OpenRead(bucket, key));
        Assert.Equal("invalid_storage_path", error.Code);
        Assert.Equal("must-not-be-read", File.ReadAllText(siblingPath));
    }

    /// <summary>
    /// 验证并发策略写入与删除 bucket 后，重建的 bucket 不会保留孤儿策略。
    /// </summary>
    [Fact]
    public async Task DeleteBucket_ConcurrentPolicyWrite_DoesNotLeaveConfigurationForRecreatedBucket()
    {
        const string bucket = "test-bucket";
        string policyJson = "{\"padding\":\"" + new string('x', 1_000_000) + "\"}";
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _rootDirectory });
        var writerStore = new SndbObjectStore(db);
        var deletingStore = new SndbObjectStore(db);

        for (int attempt = 0; attempt < 4; attempt++)
        {
            writerStore.CreateBucket(bucket);
            using var start = new ManualResetEventSlim(false);
            Task write = Task.Factory.StartNew(() =>
            {
                start.Wait();
                try
                {
                    writerStore.SetPolicy(bucket, policyJson);
                }
                catch (SndbObjectStorageException error) when (error.Code == "bucket_not_found")
                {
                    // 删除操作先获得 gate 时，策略写入应被重新校验拒绝。
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task delete = Task.Factory.StartNew(() =>
            {
                start.Wait();
                Assert.True(deletingStore.DeleteBucket(bucket));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            start.Set();
            await Task.WhenAll(write, delete);
            writerStore.CreateBucket(bucket);
            Assert.Null(writerStore.GetPolicy(bucket).PolicyJson);
            Assert.True(writerStore.DeleteBucket(bucket));
        }
    }

    /// <summary>
    /// 等待系统时钟越过上一版本时间，确保测试中的版本先后关系确定。
    /// </summary>
    private static async Task WaitForClockAdvanceAsync(DateTimeOffset previousTimestamp)
    {
        while (DateTimeOffset.UtcNow <= previousTimestamp)
            await Task.Delay(1);
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

    /// <summary>
    /// 构造测试中用于模拟 latest 指针的元数据键。
    /// </summary>
    private static string BuildLatestMetadataKey(string bucket, string key)
    {
        string escapedKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return $"latest:{bucket}/{escapedKey}";
    }

    /// <summary>
    /// 把指定分片的正文路径改为测试值，用于验证已提交元数据损坏后的安全回收。
    /// </summary>
    private static void CorruptMultipartPartStoragePath(
        Tsdb db,
        string uploadId,
        int partNumber,
        string storagePath)
    {
        KvKeyspace metadata = db.Keyspaces.Open("__object_storage");
        string metadataKey = $"part:{uploadId}:{partNumber:D5}";
        KvEntry entry = Assert.IsType<KvEntry>(metadata.GetEntry(metadataKey));
        SndbMultipartPartRecord record = JsonSerializer.Deserialize(
            entry.Value.Span,
            SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord)!;
        byte[] corrupted = JsonSerializer.SerializeToUtf8Bytes(
            record with { StoragePath = storagePath },
            SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord);
        metadata.Put(metadataKey, corrupted, entry.ExpiresAtUtc);
    }

    /// <summary>
    /// 创建令牌后移除新增的 bucket 版本字段，用真实 KV 顺序模拟升级前持久化记录。
    /// </summary>
    private static string CreateLegacyPresignedToken(
        Tsdb db,
        SndbObjectStore store,
        string bucket,
        string key)
    {
        SndbPresignedObjectUrl presigned = store.CreatePresignedUrl(
            "https://example.test/objects/legacy.bin",
            "GET",
            bucket,
            key,
            TimeSpan.FromHours(1));
        string token = Uri.UnescapeDataString(new Uri(presigned.Url).Query["?sndb-presigned=".Length..]);

        KvKeyspace metadata = db.Keyspaces.Open("__object_storage");
        KvEntry entry = Assert.Single(metadata.ScanPrefix("presign:", limit: int.MaxValue));
        using JsonDocument document = JsonDocument.Parse(entry.Value);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(
                        property.Name,
                        nameof(SndbPresignedTokenRecord.BucketVersion),
                        StringComparison.OrdinalIgnoreCase))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        metadata.Put(Encoding.UTF8.GetString(entry.Key.Span), output.ToArray(), entry.ExpiresAtUtc);
        return token;
    }

    /// <summary>
    /// 在首次异步读取时阻塞，以便确定性地插入并发元数据操作。
    /// </summary>
    private sealed class BlockingReadStream : Stream
    {
        private readonly byte[] _content;
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        /// <summary>
        /// 构造仅支持异步读取的阻塞测试流。
        /// </summary>
        private BlockingReadStream(byte[] content)
        {
            _content = content;
        }

        /// <summary>
        /// 指示该测试流可被读取。
        /// </summary>
        public override bool CanRead => true;

        /// <summary>
        /// 指示该测试流不支持定位。
        /// </summary>
        public override bool CanSeek => false;

        /// <summary>
        /// 指示该测试流不支持写入。
        /// </summary>
        public override bool CanWrite => false;

        /// <summary>
        /// 获取流长度；该测试流不支持该操作。
        /// </summary>
        public override long Length => throw new NotSupportedException();

        /// <summary>
        /// 获取或设置位置；该测试流不支持该操作。
        /// </summary>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// 创建在首次异步读取时阻塞的测试流。
        /// </summary>
        public static BlockingReadStream Create(byte[] content)
        {
            return new BlockingReadStream(content);
        }

        /// <summary>
        /// 等待写入任务首次请求内容。
        /// </summary>
        public Task WaitUntilFirstReadAsync()
        {
            return _firstRead.Task;
        }

        /// <summary>
        /// 允许首次读取返回测试内容。
        /// </summary>
        public void Release()
        {
            _release.TrySetResult();
        }

        /// <summary>
        /// 刷新测试流；只读流没有待刷新内容。
        /// </summary>
        public override void Flush()
        {
        }

        /// <summary>
        /// 同步读取不受该测试流支持。
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 首次读取等待测试释放，随后返回全部剩余内容。
        /// </summary>
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position == 0)
            {
                _firstRead.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_position >= _content.Length)
                return 0;

            int count = Math.Min(buffer.Length, _content.Length - _position);
            _content.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return count;
        }

        /// <summary>
        /// 定位操作不受该测试流支持。
        /// </summary>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 调整长度不受该测试流支持。
        /// </summary>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 写入操作不受该测试流支持。
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
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
