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
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

public sealed class ObjectStorageEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "admin-object-token";
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-s3-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };

        _app = Program.BuildApp(["--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0"], options);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var client = CreateClient();
        var create = await client.PostAsJsonAsync(
            "/v1/db",
            new CreateDatabaseRequest("objects"),
            ServerJsonContext.Default.CreateDatabaseRequest);
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ObjectStorage_PutRangeCopyTagsDeleteMarkerAndPresign_Works()
    {
        using var client = CreateClient();
        var bucket = "iotsharp-artifacts";
        var putBucket = await client.PutAsJsonAsync(
            $"/v1/db/objects/s3/{bucket}",
            new ObjectBucketCreateRequest("artifact"),
            ServerJsonContext.Default.ObjectBucketCreateRequest);
        Assert.Equal(HttpStatusCode.OK, putBucket.StatusCode);

        using var putObject = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objects/s3/{bucket}/firmware/v1.bin")
        {
            Content = new StringContent("0123456789", Encoding.UTF8, "application/octet-stream"),
        };
        putObject.Headers.TryAddWithoutValidation("x-amz-meta-owner", "iotsharp");
        putObject.Headers.TryAddWithoutValidation("x-amz-tagging", "kind=firmware&env=test");
        var put = await client.SendAsync(putObject);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True(put.Headers.ETag is not null);
        using (var json = JsonDocument.Parse(await put.Content.ReadAsStringAsync()))
        {
            Assert.Equal(10, json.RootElement.GetProperty("sizeBytes").GetInt64());
            Assert.Equal("iotsharp", json.RootElement.GetProperty("metadata").GetProperty("owner").GetString());
        }

        var list = await client.GetAsync($"/v1/db/objects/s3/{bucket}?list-type=2&prefix=firmware/");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            var objects = json.RootElement.GetProperty("objects");
            Assert.Single(objects.EnumerateArray());
            Assert.Equal("firmware/v1.bin", objects[0].GetProperty("key").GetString());
        }

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/db/objects/s3/{bucket}/firmware/v1.bin");
        rangeRequest.Headers.Range = new RangeHeaderValue(2, 5);
        var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("2345", await range.Content.ReadAsStringAsync());

        using var copyRequest = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objects/s3/{bucket}/backup/v1.bin");
        copyRequest.Headers.TryAddWithoutValidation("x-amz-copy-source", $"/{bucket}/firmware/v1.bin");
        var copy = await client.SendAsync(copyRequest);
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);

        var tags = await client.PutAsJsonAsync(
            $"/v1/db/objects/s3/{bucket}/backup/v1.bin?tagging",
            new ObjectTagsRequest(new Dictionary<string, string> { ["copied"] = "true" }),
            ServerJsonContext.Default.ObjectTagsRequest);
        Assert.Equal(HttpStatusCode.OK, tags.StatusCode);

        var presign = await client.PostAsJsonAsync(
            $"/v1/db/objects/s3/{bucket}/backup/v1.bin?presign",
            new PresignedObjectUrlCreateRequest("GET", 5),
            ServerJsonContext.Default.PresignedObjectUrlCreateRequest);
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var presignedBody = JsonSerializer.Deserialize(
            await presign.Content.ReadAsStringAsync(),
            ServerJsonContext.Default.PresignedObjectUrlResponse)!;

        using var anonymous = new HttpClient();
        var signed = await anonymous.GetAsync(presignedBody.Url);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        Assert.Equal("0123456789", await signed.Content.ReadAsStringAsync());

        var delete = await client.DeleteAsync($"/v1/db/objects/s3/{bucket}/firmware/v1.bin");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.True(delete.Headers.TryGetValues("x-amz-delete-marker", out var marker));
        Assert.Equal("true", marker.Single());

        var missing = await client.GetAsync($"/v1/db/objects/s3/{bucket}/firmware/v1.bin");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ObjectStorage_MultipartUpload_CompletesObject()
    {
        using var client = CreateClient();
        var bucket = "iotsharp-backups";
        var putBucket = await client.PutAsJsonAsync(
            $"/v1/db/objects/s3/{bucket}",
            new ObjectBucketCreateRequest("backup"),
            ServerJsonContext.Default.ObjectBucketCreateRequest);
        Assert.Equal(HttpStatusCode.OK, putBucket.StatusCode);

        var init = await client.PostAsJsonAsync(
            $"/v1/db/objects/s3/{bucket}/daily/001.bin?uploads",
            new MultipartUploadCreateRequest("application/octet-stream"),
            ServerJsonContext.Default.MultipartUploadCreateRequest);
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        var upload = JsonSerializer.Deserialize(
            await init.Content.ReadAsStringAsync(),
            ServerJsonContext.Default.MultipartUploadCreateResponse)!;

        await PutPartAsync(client, bucket, upload.UploadId, 1, "hello ");
        await PutPartAsync(client, bucket, upload.UploadId, 2, "world");

        var complete = await client.PostAsJsonAsync(
            $"/v1/db/objects/s3/{bucket}/daily/001.bin?uploadId={upload.UploadId}",
            new MultipartCompleteRequest([1, 2]),
            ServerJsonContext.Default.MultipartCompleteRequest);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var get = await client.GetAsync($"/v1/db/objects/s3/{bucket}/daily/001.bin");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("hello world", await get.Content.ReadAsStringAsync());
    }

    private static async Task PutPartAsync(HttpClient client, string bucket, string uploadId, int partNumber, string content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objects/s3/{bucket}/daily/001.bin?uploadId={uploadId}&partNumber={partNumber}")
        {
            Content = new StringContent(content, Encoding.UTF8, "application/octet-stream"),
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }
}
