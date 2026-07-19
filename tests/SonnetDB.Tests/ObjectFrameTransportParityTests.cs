using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.ObjectStorage;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M28 P5b #262 对象存储客户端帧贯通端到端等价测试：同一 put/get 分别在 <c>Protocol=frame-http2</c>
/// 与 <c>Protocol=rest</c> 下经真实 Kestrel 执行，断言内容字节一致、元数据一致、多分块大 blob 完整、
/// 错误码（object_not_found / bucket_not_found / forbidden）跨传输同码；Range 读与超大对象恒走 REST。
/// </summary>
public sealed class ObjectFrameTransportParityTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string _frameH2Url = string.Empty;
    private string? _dataRoot;
    private const string _adminToken = "obj-parity-admin";
    private const string _readOnlyToken = "obj-parity-ro";
    private const string _dbName = "obj_parity_e2e";
    private const string _bucket = "blob";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-obj-frame-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [_adminToken] = ServerRoles.Admin,
                [_readOnlyToken] = ServerRoles.ReadOnly,
            },
        };
        _app = TestServerHost.Build(options, extraArgs:
        [
            "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
            "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
        ]);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        foreach (string address in addresses.Addresses)
        {
            if (await ProbeIsHttp11Async(address))
                _baseUrl = address;
            else
                _frameH2Url = address;
        }
        Assert.NotEmpty(_baseUrl);
        Assert.NotEmpty(_frameH2Url);

        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var resp = await http.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{_dbName}\"}}", Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();

        // 桶经 admin REST 建好（管理面恒走 REST），两传输共用。
        using var admin = new SndbObjectStorageClient(ConnString("rest", _adminToken));
        await admin.CreateBucketAsync(_bucket, "parity");
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private string ConnString(string protocol, string token = _adminToken)
    {
        string baseUrl = protocol == "frame-http2" ? _frameH2Url : _baseUrl;
        return $"Data Source=sonnetdb+http://{new Uri(baseUrl).Authority}/{_dbName};Token={token};Timeout=30;Protocol={protocol}";
    }

    private static async Task<byte[]> ReadAllAsync(SndbObjectReadResult result)
    {
        await using (result.Content)
        {
            using var ms = new MemoryStream();
            await result.Content.CopyToAsync(ms);
            return ms.ToArray();
        }
    }

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public async Task PutThenGet_RawBytes_RoundTrip(string protocol)
    {
        using var client = new SndbObjectStorageClient(ConnString(protocol), useDedicatedHttpHandler: true);
        string key = "raw/" + protocol + ".bin";
        byte[] content = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF, 0x00, 0x42];
        // metadata 值走 REST 时经 x-amz-meta-* 头（仅 ASCII），故此处用 ASCII；UTF-8 元数据是帧独有能力。
        var metadata = new Dictionary<string, string> { ["origin"] = "device-a", ["seq"] = "7" };

        var put = await client.PutObjectAsync(_bucket, key,
            new MemoryStream(content), "application/octet-stream", metadata);
        Assert.Equal(content.Length, put.SizeBytes);
        Assert.False(string.IsNullOrEmpty(put.VersionId));

        var read = await client.OpenReadAsync(_bucket, key);
        Assert.NotNull(read);
        Assert.Equal(content.Length, read!.Info.SizeBytes);
        Assert.Equal(put.VersionId, read.Info.VersionId);
        Assert.Equal(put.ETag, read.Info.ETag);
        Assert.Equal(put.Sha256, read.Info.Sha256);
        Assert.False(read.IsRange);
        Assert.Equal(content, await ReadAllAsync(read));
    }

    [Fact]
    public async Task FrameWrite_RestRead_ByteEquivalent()
    {
        const string key = "cross/frame-write.bin";
        byte[] content = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray();

        using (var frameClient = new SndbObjectStorageClient(ConnString("frame-http2")))
            await frameClient.PutObjectAsync(_bucket, key, new MemoryStream(content), "application/x-thing");

        using var restClient = new SndbObjectStorageClient(ConnString("rest"));
        var read = await restClient.OpenReadAsync(_bucket, key);
        Assert.NotNull(read);
        Assert.Equal(content, await ReadAllAsync(read!));
        Assert.Equal("application/x-thing", read!.Info.ContentType);
    }

    [Fact]
    public async Task RestWrite_FrameRead_ByteEquivalent()
    {
        const string key = "cross/rest-write.bin";
        byte[] content = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();

        using (var restClient = new SndbObjectStorageClient(ConnString("rest")))
            await restClient.PutObjectAsync(_bucket, key, new MemoryStream(content), "text/csv");

        using var frameClient = new SndbObjectStorageClient(ConnString("frame-http2"));
        var read = await frameClient.OpenReadAsync(_bucket, key);
        Assert.NotNull(read);
        Assert.Equal(content, await ReadAllAsync(read!));
        Assert.Equal("text/csv", read!.Info.ContentType);
    }

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public async Task LargeBlob_MultiChunk_RoundTrip(string protocol)
    {
        // 越过 256 KiB 默认分块，验证 get 多 data 帧拼接完整。
        using var client = new SndbObjectStorageClient(ConnString(protocol));
        string key = "big/" + protocol + ".bin";
        byte[] content = new byte[600 * 1024];
        for (int i = 0; i < content.Length; i++)
            content[i] = (byte)((i * 2654435761u) >> 24);

        var put = await client.PutObjectAsync(_bucket, key, new MemoryStream(content));
        Assert.Equal(content.Length, put.SizeBytes);

        var read = await client.OpenReadAsync(_bucket, key);
        Assert.NotNull(read);
        byte[] roundtrip = await ReadAllAsync(read!);
        Assert.Equal(content.Length, roundtrip.Length);
        Assert.Equal(content, roundtrip);
    }

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public async Task Get_MissingObject_ReturnsNull(string protocol)
    {
        using var client = new SndbObjectStorageClient(ConnString(protocol));
        var read = await client.OpenReadAsync(_bucket, "does/not/exist.bin");
        Assert.Null(read);
    }

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public async Task Get_MissingBucket_ReturnsNull(string protocol)
    {
        // GET 路径两传输都把「桶/对象不存在」归一化为 null（REST 404→null，帧 *_not_found→null）。
        using var client = new SndbObjectStorageClient(ConnString(protocol));
        var read = await client.OpenReadAsync("no-such-bucket", "k");
        Assert.Null(read);
    }

    [Fact]
    public async Task Put_MissingBucket_FrameSurfacesBucketNotFound()
    {
        // PUT 到不存在的桶：帧路径回带内错误帧 → bucket_not_found（干净的应用级错误码）。
        // REST PUT 因请求体未被消费而在 404 时重置连接，客户端拿不到错误体（历史 REST 行为），
        // 故 REST 侧只断言抛错、不断言具体码——帧路径的干净错误码正是 #262 的收益。
        using var frameClient = new SndbObjectStorageClient(ConnString("frame-http2"));
        var frameEx = await Assert.ThrowsAsync<SndbServerException>(
            () => frameClient.PutObjectAsync("no-such-bucket", "k", new MemoryStream([1, 2, 3])));
        Assert.Equal("bucket_not_found", frameEx.Error);

        using var restClient = new SndbObjectStorageClient(ConnString("rest"));
        await Assert.ThrowsAsync<SndbServerException>(
            () => restClient.PutObjectAsync("no-such-bucket", "k", new MemoryStream([1, 2, 3])));
    }

    [Fact]
    public async Task Put_ReadOnlyToken_Forbidden_AcrossTransports()
    {
        using var frameClient = new SndbObjectStorageClient(ConnString("frame-http2", _readOnlyToken));
        var frameEx = await Assert.ThrowsAsync<SndbServerException>(
            () => frameClient.PutObjectAsync(_bucket, "ro/k.bin", new MemoryStream([1, 2, 3])));
        Assert.Equal("forbidden", frameEx.Error);

        using var restClient = new SndbObjectStorageClient(ConnString("rest", _readOnlyToken));
        var restEx = await Assert.ThrowsAsync<SndbServerException>(
            () => restClient.PutObjectAsync(_bucket, "ro/k.bin", new MemoryStream([1, 2, 3])));
        Assert.Equal("forbidden", restEx.Error);
    }

    [Fact]
    public async Task RangeRead_FallsBackToRest_UnderFrameProtocol()
    {
        const string key = "range/partial.bin";
        byte[] content = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        using var client = new SndbObjectStorageClient(ConnString("frame-http2"));
        await client.PutObjectAsync(_bucket, key, new MemoryStream(content));

        // Range 读无帧路径 → 回落 REST，返回 partial content。
        var read = await client.OpenReadAsync(_bucket, key, new SndbObjectRange(100, 50));
        Assert.NotNull(read);
        Assert.True(read!.IsRange);
        byte[] slice = await ReadAllAsync(read);
        Assert.Equal(content.Skip(100).Take(50).ToArray(), slice);
    }

    private static async Task<bool> ProbeIsHttp11Async(string address)
    {
        using var client = new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            using var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}
