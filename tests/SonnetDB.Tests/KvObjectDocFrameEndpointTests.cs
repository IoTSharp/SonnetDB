using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Documents;
using SonnetDB.Json;
using SonnetDB.Kv;
using SonnetDB.ObjectStorage;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M28 P5b #240 kv / object / doc 帧端点端到端测试：帧写入 → REST 回读等价、原始字节零 Base64、
/// object get 流式分块、鉴权、错误隔离与批内混合 service。
/// </summary>
public sealed class KvObjectDocFrameEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _adminToken = "admin-kodframe-token";
    private const string _readOnlyToken = "ro-kodframe-token";
    private const string _dbName = "kodframe";
    private const string FrameContentType = "application/x-sonnetdb-frame";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-kodframe-tests-" + Guid.NewGuid().ToString("N"));
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

        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        _baseUrl = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        using var admin = CreateClient();
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(_dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
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

    private HttpClient CreateClient(string? token = _adminToken)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<List<(FrameHeader Header, byte[] Payload)>> PostFramesAsync(HttpClient client, byte[] body)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(FrameContentType);
        using var response = await client.PostAsync("/v1/frame", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] responseBody = await response.Content.ReadAsByteArrayAsync();

        var frames = new List<(FrameHeader, byte[])>();
        var buffer = new ReadOnlySequence<byte>(responseBody);
        while (FrameCodec.TryReadFrame(ref buffer, out FrameHeader header, out ReadOnlySequence<byte> payload))
            frames.Add((header, payload.ToArray()));
        Assert.Equal(0, buffer.Length);
        return frames;
    }

    private static byte[] Encode(Action<ArrayBufferWriter<byte>> encode)
    {
        var writer = new ArrayBufferWriter<byte>();
        encode(writer);
        return writer.WrittenMemory.ToArray();
    }

    // ────────────────────────────── KV ──────────────────────────────

    [Fact]
    public async Task Kv_PutThenGet_RoundTrip_RawBytes()
    {
        using var admin = CreateClient();
        byte[] key = "device:001"u8.ToArray();
        byte[] value = [0x00, 0x01, 0xFE, 0xFF, 0x80]; // 含高位字节，验证零 Base64 原样直传

        var putFrames = await PostFramesAsync(admin, Encode(w =>
            KvFrameCodec.EncodePutRequest(w, 1, _dbName, "cache", key, value)));
        Assert.Single(putFrames);
        Assert.False(putFrames[0].Header.IsError);
        long version = KvFrameCodec.DecodePutResponse(putFrames[0].Payload);
        Assert.True(version > 0);

        var getFrames = await PostFramesAsync(admin, Encode(w =>
            KvFrameCodec.EncodeGetRequest(w, 2, _dbName, "cache", key)));
        KvGetFrameResult? result = KvFrameCodec.DecodeGetResponse(getFrames[0].Payload);
        Assert.NotNull(result);
        Assert.Equal(value, result.Value);
        Assert.Equal(version, result.Version);
    }

    [Fact]
    public async Task Kv_FramePut_RestGet_Equivalence()
    {
        using var admin = CreateClient();
        byte[] key = "k-eq"u8.ToArray();
        byte[] value = [1, 2, 3, 4, 5];

        await PostFramesAsync(admin, Encode(w =>
            KvFrameCodec.EncodePutRequest(w, 1, _dbName, "cache", key, value)));

        var resp = await admin.PostAsync($"/v1/db/{_dbName}/kv/cache/get",
            JsonContent.Create(new KvGetRequest("k-eq"), ServerJsonContext.Default.KvGetRequest));
        Assert.True(resp.IsSuccessStatusCode);
        var restValue = await resp.Content.ReadFromJsonAsync(ServerJsonContext.Default.KvValueResponse);
        Assert.NotNull(restValue);
        Assert.True(restValue.Found);
        Assert.Equal(value, restValue.Value);
    }

    [Fact]
    public async Task Kv_Get_NotFound()
    {
        using var admin = CreateClient();
        var frames = await PostFramesAsync(admin, Encode(w =>
            KvFrameCodec.EncodeGetRequest(w, 1, _dbName, "cache", "missing"u8.ToArray())));
        Assert.Null(KvFrameCodec.DecodeGetResponse(frames[0].Payload));
    }

    [Fact]
    public async Task Kv_Scan_Prefix()
    {
        using var admin = CreateClient();
        for (int i = 0; i < 5; i++)
        {
            byte[] key = Encoding.UTF8.GetBytes($"user:{i:D3}");
            await PostFramesAsync(admin, Encode(w =>
                KvFrameCodec.EncodePutRequest(w, 1, _dbName, "scanks", key, [(byte)i])));
        }
        await PostFramesAsync(admin, Encode(w =>
            KvFrameCodec.EncodePutRequest(w, 1, _dbName, "scanks", "other:x"u8.ToArray(), [0xEE])));

        var frames = await PostFramesAsync(admin, Encode(w =>
            KvFrameCodec.EncodeScanRequest(w, 2, _dbName, "scanks", "user:"u8.ToArray())));
        KvScanFrameEntry[] entries = KvFrameCodec.DecodeScanResponse(frames[0].Payload);
        Assert.Equal(5, entries.Length);
        Assert.All(entries, e => Assert.StartsWith("user:", Encoding.UTF8.GetString(e.Key)));
    }

    [Fact]
    public async Task Kv_Put_ReadOnlyToken_Forbidden()
    {
        using var ro = CreateClient(_readOnlyToken);
        var frames = await PostFramesAsync(ro, Encode(w =>
            KvFrameCodec.EncodePutRequest(w, 1, _dbName, "cache", "k"u8.ToArray(), [1])));
        Assert.True(frames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("forbidden", code);
    }

    [Fact]
    public async Task Kv_Get_DbNotFound()
    {
        using var admin = CreateClient();
        var frames = await PostFramesAsync(admin, Encode(w =>
            KvFrameCodec.EncodeGetRequest(w, 1, "nope", "cache", "k"u8.ToArray())));
        Assert.True(frames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("db_not_found", code);
    }

    // ────────────────────────────── Object ──────────────────────────────

    [Fact]
    public async Task Object_PutThenGet_StreamingChunks()
    {
        using var admin = CreateClient();
        await CreateBucketAsync(admin, "assets");

        // 大于单个 data 分块（256 KiB）以验证多分块流式
        byte[] content = new byte[600_000];
        Random.Shared.NextBytes(content);

        var putFrames = await PostFramesAsync(admin, Encode(w =>
            ObjectFrameCodec.EncodePutRequest(w, 1, _dbName, "assets", "blob/big.bin", content, "application/octet-stream")));
        Assert.Single(putFrames);
        Assert.False(putFrames[0].Header.IsError);
        ObjectPutFrameResult putResult = ObjectFrameCodec.DecodePutResponse(putFrames[0].Payload);
        Assert.Equal(content.Length, putResult.SizeBytes);

        var getFrames = await PostFramesAsync(admin, Encode(w =>
            ObjectFrameCodec.EncodeGetRequest(w, 2, _dbName, "assets", "blob/big.bin")));
        // meta + data × N + end
        Assert.True(getFrames.Count >= 3);
        Assert.Equal(ObjectChunkKind.Meta, ObjectFrameCodec.PeekChunkKind(getFrames[0].Payload));
        ObjectGetFrameMeta meta = ObjectFrameCodec.DecodeGetMetaFrame(getFrames[0].Payload);
        Assert.Equal(content.Length, meta.SizeBytes);

        var assembled = new List<byte>();
        for (int i = 1; i < getFrames.Count - 1; i++)
        {
            Assert.Equal(ObjectChunkKind.Data, ObjectFrameCodec.PeekChunkKind(getFrames[i].Payload));
            assembled.AddRange(ObjectFrameCodec.DecodeGetDataFrame(getFrames[i].Payload).ToArray());
        }
        var last = getFrames[^1];
        Assert.Equal(ObjectChunkKind.End, ObjectFrameCodec.PeekChunkKind(last.Payload));
        Assert.Equal(content.Length, ObjectFrameCodec.DecodeGetEndFrame(last.Payload));
        Assert.Equal(content, assembled.ToArray());
    }

    [Fact]
    public async Task Object_FramePut_RestGet_Equivalence()
    {
        using var admin = CreateClient();
        await CreateBucketAsync(admin, "assets");
        byte[] content = [0x00, 0xFF, 0x10, 0x20];

        await PostFramesAsync(admin, Encode(w =>
            ObjectFrameCodec.EncodePutRequest(w, 1, _dbName, "assets", "eq/obj.bin", content)));

        using var getResp = await admin.GetAsync($"/v1/db/{_dbName}/s3/assets/eq/obj.bin");
        Assert.True(getResp.IsSuccessStatusCode);
        byte[] restContent = await getResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(content, restContent);
    }

    [Fact]
    public async Task Object_Get_NotFound_ErrorFrame()
    {
        using var admin = CreateClient();
        await CreateBucketAsync(admin, "assets");
        var frames = await PostFramesAsync(admin, Encode(w =>
            ObjectFrameCodec.EncodeGetRequest(w, 1, _dbName, "assets", "no/such.bin")));
        Assert.True(frames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("object_not_found", code);
    }

    [Fact]
    public async Task Object_Put_MissingBucket_ErrorFrame()
    {
        using var admin = CreateClient();
        var frames = await PostFramesAsync(admin, Encode(w =>
            ObjectFrameCodec.EncodePutRequest(w, 1, _dbName, "no-bucket", "k", [1])));
        Assert.True(frames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("bucket_not_found", code);
    }

    // ────────────────────────────── Doc ──────────────────────────────

    [Fact]
    public async Task Doc_InsertThenFind_RoundTrip_RawJson()
    {
        using var admin = CreateClient();
        await CreateCollectionAsync(admin, "users");

        var documents = new[]
        {
            new DocumentWriteRequest("u-1", """{"name":"张三","age":30}"""),
            new DocumentWriteRequest("u-2", """{"name":"lee","tags":["a","b"]}"""),
        };
        var insertFrames = await PostFramesAsync(admin, Encode(w =>
            DocFrameCodec.EncodeInsertRequest(w, 1, _dbName, "users", documents)));
        DocumentWriteResult writeResult = DocFrameCodec.DecodeInsertResponse(insertFrames[0].Payload);
        Assert.Equal(2, writeResult.Inserted);
        Assert.False(writeResult.HasErrors);

        var findFrames = await PostFramesAsync(admin, Encode(w =>
            DocFrameCodec.EncodeFindRequest(w, 2, _dbName, "users", ["u-1", "u-2"])));
        DocumentRow[] rows = DocFrameCodec.DecodeFindResponse(findFrames[0].Payload);
        Assert.Equal(2, rows.Length);
        using var doc0 = JsonDocument.Parse(rows[0].Json);
        Assert.Equal("张三", doc0.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Doc_FrameInsert_RestFindOne_Equivalence()
    {
        using var admin = CreateClient();
        await CreateCollectionAsync(admin, "users");
        await PostFramesAsync(admin, Encode(w =>
            DocFrameCodec.EncodeInsertRequest(w, 1, _dbName, "users",
                [new DocumentWriteRequest("eq-1", """{"v":123}""")])));

        var resp = await admin.PostAsync($"/v1/db/{_dbName}/documents/users/find-one",
            JsonContent.Create(new DocumentFindRequest(Id: "eq-1"), ServerJsonContext.Default.DocumentFindRequest));
        Assert.True(resp.IsSuccessStatusCode);
        var found = await resp.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentFindOneResponse);
        Assert.NotNull(found);
        Assert.True(found.Found);
        Assert.Equal(123, found.Document!.Document.GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task Doc_Find_ScanPaging()
    {
        using var admin = CreateClient();
        await CreateCollectionAsync(admin, "scanusers");
        var documents = Enumerable.Range(0, 10)
            .Select(i => new DocumentWriteRequest($"id-{i:D2}", $$"""{"n":{{i}}}"""))
            .ToArray();
        await PostFramesAsync(admin, Encode(w =>
            DocFrameCodec.EncodeInsertRequest(w, 1, _dbName, "scanusers", documents)));

        var frames = await PostFramesAsync(admin, Encode(w =>
            DocFrameCodec.EncodeFindRequest(w, 2, _dbName, "scanusers", ids: null, afterId: null, limit: 4)));
        DocumentRow[] rows = DocFrameCodec.DecodeFindResponse(frames[0].Payload);
        Assert.Equal(4, rows.Length);
    }

    [Fact]
    public async Task Doc_Insert_DuplicateKey_ErrorInResult()
    {
        using var admin = CreateClient();
        await CreateCollectionAsync(admin, "dups");
        await PostFramesAsync(admin, Encode(w =>
            DocFrameCodec.EncodeInsertRequest(w, 1, _dbName, "dups", [new DocumentWriteRequest("x", "{}")])));

        var frames = await PostFramesAsync(admin, Encode(w =>
            DocFrameCodec.EncodeInsertRequest(w, 2, _dbName, "dups", [new DocumentWriteRequest("x", "{}")], ordered: false)));
        DocumentWriteResult result = DocFrameCodec.DecodeInsertResponse(frames[0].Payload);
        Assert.Equal(0, result.Inserted);
        Assert.True(result.HasErrors);
        Assert.Equal(SonnetDB.Documents.DocumentWriteErrorCodes.DuplicateKey, result.Errors[0].Code);
    }

    [Fact]
    public async Task Doc_Find_CollectionNotFound_ErrorFrame()
    {
        using var admin = CreateClient();
        var frames = await PostFramesAsync(admin, Encode(w =>
            DocFrameCodec.EncodeFindRequest(w, 1, _dbName, "no-collection", ["x"])));
        Assert.True(frames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("collection_not_found", code);
    }

    // ────────────────────────────── 混合批 ──────────────────────────────

    [Fact]
    public async Task MixedBatch_KvDocBadFrame_IsolatedPerFrame()
    {
        using var admin = CreateClient();
        await CreateCollectionAsync(admin, "mixdocs");

        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodePutRequest(writer, 1, _dbName, "mixks", "k"u8.ToArray(), [1]);
        DocFrameCodec.EncodeInsertRequest(writer, 2, _dbName, "mixdocs", [new DocumentWriteRequest("d1", "{}")]);
        // 坏帧：doc find 引用不存在的集合
        DocFrameCodec.EncodeFindRequest(writer, 3, _dbName, "ghost", ["x"]);

        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Equal(3, frames.Count);
        Assert.False(frames[0].Header.IsError);
        Assert.Equal(1u, frames[0].Header.StreamId);
        Assert.False(frames[1].Header.IsError);
        Assert.Equal(2u, frames[1].Header.StreamId);
        Assert.True(frames[2].Header.IsError);
        Assert.Equal(3u, frames[2].Header.StreamId);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[2].Payload);
        Assert.Equal("collection_not_found", code);
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private async Task CreateBucketAsync(HttpClient client, string bucket)
    {
        using var resp = await client.PutAsync($"/v1/db/{_dbName}/s3/{bucket}", content: null);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
    }

    private async Task CreateCollectionAsync(HttpClient client, string collection)
    {
        var resp = await client.PostAsync($"/v1/db/{_dbName}/documents/{collection}",
            JsonContent.Create(new DocumentCollectionCreateRequest(), ServerJsonContext.Default.DocumentCollectionCreateRequest));
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
    }
}
