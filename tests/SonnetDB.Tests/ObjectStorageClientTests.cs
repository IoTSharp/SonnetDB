using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.Json;
using SonnetDB.ObjectStorage;
using Xunit;

namespace SonnetDB.Tests;

public sealed class ObjectStorageClientTests : IAsyncLifetime
{
    private const string AdminToken = "admin-object-client-token";
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-s3-client-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var create = await http.PostAsJsonAsync(
            "/v1/db",
            new CreateDatabaseRequest("objectsclient"),
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
    public async Task RemoteClient_WithDedicatedPool_PutReadDelete_RoundTrips()
    {
        var connectionString = $"Data Source=sonnetdb+http://{new Uri(_baseUrl!).Authority}/objectsclient;Token={AdminToken};Timeout=30";
        using var client = new SndbObjectStorageClient(connectionString, useDedicatedHttpHandler: true);
        await client.CreateBucketAsync("iotsharp-blob-storage", "iotsharp-blob-storage");

        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("blob payload"));
        var written = await client.PutObjectAsync("iotsharp-blob-storage", "attachments/a.txt", input, "text/plain");
        Assert.Equal(12, written.SizeBytes);

        var listed = await client.ListObjectsAsync("iotsharp-blob-storage", "attachments/");
        Assert.Single(listed.Objects);
        Assert.Equal("attachments/a.txt", listed.Objects[0].Key);

        var read = await client.OpenReadAsync("iotsharp-blob-storage", "attachments/a.txt");
        Assert.NotNull(read);
        await using (read!.Content)
        using (var reader = new StreamReader(read.Content, Encoding.UTF8))
        {
            Assert.Equal("blob payload", await reader.ReadToEndAsync());
        }

        await client.DeleteObjectAsync("iotsharp-blob-storage", "attachments/a.txt");
        Assert.Null(await client.OpenReadAsync("iotsharp-blob-storage", "attachments/a.txt"));
    }

    [Fact]
    public async Task ObjectStorage_ConditionalRestRequests_ReturnExpectedStatusCodes()
    {
        const string bucket = "conditional-rest";
        const string key = "state.txt";
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        using var created = await client.PutAsJsonAsync(
            $"/v1/db/objectsclient/s3/{bucket}",
            new ObjectBucketCreateRequest(),
            ServerJsonContext.Default.ObjectBucketCreateRequest);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using var initial = await client.PutAsync(
            $"/v1/db/objectsclient/s3/{bucket}/{key}",
            new StringContent("one", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        string initialETag = Assert.IsType<string>(initial.Headers.ETag?.Tag);

        using (var ifNoneMatch = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objectsclient/s3/{bucket}/{key}")
        {
            Content = new StringContent("blocked", Encoding.UTF8, "text/plain"),
        })
        {
            ifNoneMatch.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var response = await client.SendAsync(ifNoneMatch);
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        }

        using (var wrongIfMatch = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objectsclient/s3/{bucket}/{key}")
        {
            Content = new StringContent("blocked", Encoding.UTF8, "text/plain"),
        })
        {
            wrongIfMatch.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");
            using var response = await client.SendAsync(wrongIfMatch);
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        }

        using (var matchingIfMatch = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objectsclient/s3/{bucket}/{key}")
        {
            Content = new StringContent("two", Encoding.UTF8, "text/plain"),
        })
        {
            matchingIfMatch.Headers.TryAddWithoutValidation("If-Match", initialETag);
            using var response = await client.SendAsync(matchingIfMatch);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            initialETag = Assert.IsType<string>(response.Headers.ETag?.Tag);
        }

        using (var matchingIfMatchList = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objectsclient/s3/{bucket}/{key}")
        {
            Content = new StringContent("three", Encoding.UTF8, "text/plain"),
        })
        {
            matchingIfMatchList.Headers.TryAddWithoutValidation("If-Match", "\"stale\", " + initialETag);
            using var response = await client.SendAsync(matchingIfMatchList);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            initialETag = Assert.IsType<string>(response.Headers.ETag?.Tag);
        }

        using (var weakIfMatchList = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objectsclient/s3/{bucket}/{key}")
        {
            Content = new StringContent("blocked", Encoding.UTF8, "text/plain"),
        })
        {
            weakIfMatchList.Headers.TryAddWithoutValidation("If-Match", "W/" + initialETag + ", \"stale\"");
            using var response = await client.SendAsync(weakIfMatchList);
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        }

        using (var wildcardIfMatch = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objectsclient/s3/{bucket}/{key}")
        {
            Content = new StringContent("three", Encoding.UTF8, "text/plain"),
        })
        {
            wildcardIfMatch.Headers.TryAddWithoutValidation("If-Match", "*");
            using var response = await client.SendAsync(wildcardIfMatch);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            initialETag = Assert.IsType<string>(response.Headers.ETag?.Tag);
        }

        using (var matchingIfNoneMatch = new HttpRequestMessage(HttpMethod.Get, $"/v1/db/objectsclient/s3/{bucket}/{key}"))
        {
            matchingIfNoneMatch.Headers.TryAddWithoutValidation("If-None-Match", initialETag);
            using var response = await client.SendAsync(matchingIfNoneMatch);
            Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
            Assert.Equal(initialETag, response.Headers.ETag?.Tag);
            Assert.NotNull(response.Content.Headers.LastModified);
        }

        using (var wrongReadIfMatch = new HttpRequestMessage(HttpMethod.Get, $"/v1/db/objectsclient/s3/{bucket}/{key}"))
        {
            wrongReadIfMatch.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");
            using var response = await client.SendAsync(wrongReadIfMatch);
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
            Assert.Equal(initialETag, response.Headers.ETag?.Tag);
            Assert.NotNull(response.Content.Headers.LastModified);
        }

        using (var headNotModified = new HttpRequestMessage(HttpMethod.Head, $"/v1/db/objectsclient/s3/{bucket}/{key}"))
        {
            headNotModified.Headers.TryAddWithoutValidation("If-None-Match", "\"other\", W/" + initialETag);
            using var response = await client.SendAsync(headNotModified);
            Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
            Assert.Equal(initialETag, response.Headers.ETag?.Tag);
            Assert.NotNull(response.Content.Headers.LastModified);
            Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
        }

        using (var headPreconditionFailed = new HttpRequestMessage(HttpMethod.Head, $"/v1/db/objectsclient/s3/{bucket}/{key}"))
        {
            headPreconditionFailed.Headers.TryAddWithoutValidation("If-Match", "W/" + initialETag + ", \"stale\"");
            using var response = await client.SendAsync(headPreconditionFailed);
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
            Assert.Equal(initialETag, response.Headers.ETag?.Tag);
            Assert.NotNull(response.Content.Headers.LastModified);
            Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
        }

        using (var matchingIfNoneMatchWrite = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objectsclient/s3/{bucket}/{key}")
        {
            Content = new StringContent("four", Encoding.UTF8, "text/plain"),
        })
        {
            matchingIfNoneMatchWrite.Headers.TryAddWithoutValidation("If-None-Match", "\"stale\", W/" + initialETag);
            using var response = await client.SendAsync(matchingIfNoneMatchWrite);
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        }

        using (var combinedConditions = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objectsclient/s3/{bucket}/{key}")
        {
            Content = new StringContent("four", Encoding.UTF8, "text/plain"),
        })
        {
            combinedConditions.Headers.TryAddWithoutValidation("If-Match", initialETag);
            combinedConditions.Headers.TryAddWithoutValidation("If-None-Match", "\"stale\"");
            using var response = await client.SendAsync(combinedConditions);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            initialETag = Assert.IsType<string>(response.Headers.ETag?.Tag);
        }

        using (var conflictingConditions = new HttpRequestMessage(HttpMethod.Put, $"/v1/db/objectsclient/s3/{bucket}/conflicting.txt")
        {
            Content = new StringContent("blocked", Encoding.UTF8, "text/plain"),
        })
        {
            conflictingConditions.Headers.TryAddWithoutValidation("If-Match", "*");
            conflictingConditions.Headers.TryAddWithoutValidation("If-None-Match", "*, \"stale\"");
            using var response = await client.SendAsync(conflictingConditions);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("unsupported_if_none_match", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        foreach ((HttpMethod method, string ifMatch) in new[]
        {
            (HttpMethod.Get, "*"),
            (HttpMethod.Head, "\"expected\""),
        })
        {
            using var missingIfMatch = new HttpRequestMessage(method, $"/v1/db/objectsclient/s3/{bucket}/missing.txt");
            missingIfMatch.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            using var response = await client.SendAsync(missingIfMatch);
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        }

        using (var matchingReadIfMatch = new HttpRequestMessage(HttpMethod.Get, $"/v1/db/objectsclient/s3/{bucket}/{key}"))
        {
            matchingReadIfMatch.Headers.TryAddWithoutValidation("If-Match", initialETag);
            using var response = await client.SendAsync(matchingReadIfMatch);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("four", await response.Content.ReadAsStringAsync());
        }

        using (var wildcardReadIfMatch = new HttpRequestMessage(HttpMethod.Get, $"/v1/db/objectsclient/s3/{bucket}/{key}"))
        {
            wildcardReadIfMatch.Headers.TryAddWithoutValidation("If-Match", "*");
            using var response = await client.SendAsync(wildcardReadIfMatch);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("four", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task RemoteClient_ConditionalOperations_MapPreconditionStatuses()
    {
        const string bucket = "conditional-sdk";
        const string key = "state.txt";
        string connection = $"Data Source=sonnetdb+http://{new Uri(_baseUrl!).Authority}/objectsclient;Token={AdminToken};Protocol=rest;Timeout=30";
        using var client = new SndbObjectStorageClient(connection);
        await client.CreateBucketAsync(bucket);

        await using var initialContent = new MemoryStream(Encoding.UTF8.GetBytes("one"));
        var initial = await client.PutObjectConditionalAsync(
            bucket,
            key,
            initialContent,
            new SndbObjectWriteCondition(IfNoneMatch: true),
            "text/plain");

        await using var existsContent = new MemoryStream(Encoding.UTF8.GetBytes("blocked"));
        var existsError = await Assert.ThrowsAsync<SndbServerException>(() => client.PutObjectConditionalAsync(
            bucket,
            key,
            existsContent,
            new SndbObjectWriteCondition(IfNoneMatch: true),
            "text/plain"));
        Assert.Equal(HttpStatusCode.PreconditionFailed, existsError.StatusCode);
        Assert.Equal("object_precondition_failed", existsError.Error);

        await using var staleContent = new MemoryStream(Encoding.UTF8.GetBytes("blocked"));
        var staleError = await Assert.ThrowsAsync<SndbServerException>(() => client.PutObjectConditionalAsync(
            bucket,
            key,
            staleContent,
            new SndbObjectWriteCondition("\"stale\""),
            "text/plain"));
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleError.StatusCode);
        Assert.Equal("object_precondition_failed", staleError.Error);

        await using var replacementContent = new MemoryStream(Encoding.UTF8.GetBytes("two"));
        var replacement = await client.PutObjectConditionalAsync(
            bucket,
            key,
            replacementContent,
            new SndbObjectWriteCondition(initial.ETag),
            "text/plain");

        var notModified = await client.OpenReadConditionalAsync(
            bucket,
            key,
            new SndbObjectReadCondition(IfNoneMatch: replacement.ETag));
        Assert.Equal(SndbObjectConditionalReadStatus.NotModified, notModified.Status);
        Assert.Null(notModified.Read);

        var preconditionFailed = await client.OpenReadConditionalAsync(
            bucket,
            key,
            new SndbObjectReadCondition(IfMatch: "\"stale\""));
        Assert.Equal(SndbObjectConditionalReadStatus.PreconditionFailed, preconditionFailed.Status);
        Assert.Null(preconditionFailed.Read);

        var missingPreconditionFailed = await client.OpenReadConditionalAsync(
            bucket,
            "missing.txt",
            new SndbObjectReadCondition(IfMatch: "*"));
        Assert.Equal(SndbObjectConditionalReadStatus.PreconditionFailed, missingPreconditionFailed.Status);
        Assert.Null(missingPreconditionFailed.Read);

        var missingNotFound = await client.OpenReadConditionalAsync(
            bucket,
            "missing.txt",
            new SndbObjectReadCondition());
        Assert.Equal(SndbObjectConditionalReadStatus.NotFound, missingNotFound.Status);
        Assert.Null(missingNotFound.Read);

        var matched = await client.OpenReadConditionalAsync(
            bucket,
            key,
            new SndbObjectReadCondition(IfMatch: replacement.ETag));
        Assert.Equal(SndbObjectConditionalReadStatus.Success, matched.Status);
        var read = Assert.IsType<SndbObjectReadResult>(matched.Read);
        await using (read.Content)
        using (var reader = new StreamReader(read.Content, Encoding.UTF8))
        {
            Assert.Equal("two", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task ListObjectsAsync_RemoteDelimiterPages_PreservesTokensAndTargets()
    {
        string connection = $"Data Source=sonnetdb+http://{new Uri(_baseUrl!).Authority}/objectsclient;Token={AdminToken};Protocol=rest;Timeout=30";
        using var client = new SndbObjectStorageClient(connection);
        await client.CreateBucketAsync("media");
        foreach (string key in new[] { "dir/a", "dir/sub/b", "dir/z" })
        {
            await using var input = new MemoryStream([1]);
            await client.PutObjectAsync("media", key, input);
        }
        var first = await client.ListObjectsAsync("media", "/dir/", 1, null, "/", CancellationToken.None);
        Assert.Equal("dir/a", Assert.Single(first.Objects).Key);
        Assert.True(first.IsTruncated);
        var second = await client.ListObjectsAsync("media", "dir/", 1, first.NextContinuationToken, "/", CancellationToken.None);
        Assert.Empty(second.Objects);
        Assert.Equal("dir/sub/", Assert.Single(second.CommonPrefixes));
        Assert.True(second.IsTruncated);
        Assert.Equal(first.NextContinuationToken, second.ContinuationToken);
        var last = await client.ListObjectsAsync("media", "dir/", 1, second.NextContinuationToken, "/", CancellationToken.None);
        Assert.Equal("dir/z", Assert.Single(last.Objects).Key);
        Assert.False(last.IsTruncated);
        Assert.Null(last.NextContinuationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateBucketAsync_Redirect_DoesNotReplayWrite(bool dedicatedPool)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        int requests = 0;
        app.MapPut("/{**path}", (HttpContext context) =>
        {
            Interlocked.Increment(ref requests);
            context.Response.Headers.Location = "/replayed";
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
        });
        await app.StartAsync();
        try
        {
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .First(static value => new Uri(value).Host == IPAddress.Loopback.ToString());
            using var client = new SndbObjectStorageClient($"Data Source=sonnetdb+http://{new Uri(address).Authority}/objectsclient;Protocol=rest;Timeout=10", dedicatedPool);
            var error = await Assert.ThrowsAsync<SndbServerException>(() => client.CreateBucketAsync("media"));
            Assert.Equal(HttpStatusCode.TemporaryRedirect, error.StatusCode);
            Assert.Equal(1, Volatile.Read(ref requests));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData("upload", false)]
    [InlineData("upload", true)]
    [InlineData("complete", false)]
    [InlineData("complete", true)]
    [InlineData("abort", false)]
    [InlineData("abort", true)]
    public async Task MultipartAsync_WrongRemoteTarget_RejectsBeforeMutation(string operation, bool wrongBucket)
    {
        string connection = $"Data Source=sonnetdb+http://{new Uri(_baseUrl!).Authority}/objectsclient;Token={AdminToken};Protocol=rest;Timeout=30";
        using var client = new SndbObjectStorageClient(connection);
        await client.CreateBucketAsync("media");
        await client.CreateBucketAsync("other");
        var upload = await client.InitiateMultipartUploadAsync("media", "original.bin");
        await using var input = new MemoryStream([1, 2, 3]);
        await client.UploadPartAsync("media", "original.bin", upload.UploadId, 1, input);
        await using var replacement = new MemoryStream([9]);
        string bucket = wrongBucket ? "other" : "media";
        string key = wrongBucket ? "original.bin" : "other.bin";
        Func<Task> rejected = operation switch
        {
            "upload" => () => client.UploadPartAsync(bucket, key, upload.UploadId, 1, replacement),
            "complete" => () => client.CompleteMultipartUploadAsync(bucket, key, upload.UploadId, [1]),
            _ => () => client.AbortMultipartUploadAsync(bucket, key, upload.UploadId),
        };
        var error = await Assert.ThrowsAsync<SndbServerException>(rejected);
        Assert.Equal("multipart_not_found", error.Error);
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);

        await client.CompleteMultipartUploadAsync("media", "original.bin", upload.UploadId, [1]);
        var read = (await client.OpenReadAsync("media", "original.bin"))!;
        await using var content = read.Content;
        using var actual = new MemoryStream();
        await content.CopyToAsync(actual);
        Assert.Equal(new byte[] { 1, 2, 3 }, actual.ToArray());
    }

    [Fact]
    public async Task RemoteClient_GovernanceMethods_Work()
    {
        var connectionString = $"Data Source=sonnetdb+http://{new Uri(_baseUrl!).Authority}/objectsclient;Token={AdminToken};Timeout=30;Protocol=rest";
        using var client = new SndbObjectStorageClient(connectionString);
        await client.CreateBucketAsync("iotsharp-governance-client", "artifact");

        var policy = await client.SetPolicyAsync("iotsharp-governance-client", """{"Statement":[]}""");
        Assert.Equal("""{"Statement":[]}""", policy.PolicyJson);

        var quota = await client.SetQuotaAsync("iotsharp-governance-client", maxSizeBytes: 8, maxObjectVersions: 1);
        Assert.Equal(8, quota.MaxSizeBytes);
        Assert.Equal(1, quota.MaxObjectVersions);

        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("12345678"));
        await client.PutObjectAsync("iotsharp-governance-client", "firmware/a.bin", input, "application/octet-stream");

        var versions = await client.ListObjectVersionsAsync("iotsharp-governance-client", "firmware/a.bin");
        Assert.Single(versions.Versions);

        var stats = await client.GetStatsAsync("iotsharp-governance-client");
        Assert.Equal(1, stats.CurrentObjectCount);
        Assert.Equal(8, stats.CurrentSizeBytes);
        Assert.Equal(0, stats.QuotaRemainingSizeBytes);

        await using var rejected = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var quotaError = await Assert.ThrowsAsync<SndbServerException>(() =>
            client.PutObjectAsync("iotsharp-governance-client", "firmware/b.bin", rejected, "application/octet-stream"));
        Assert.Equal("quota_exceeded", quotaError.Error);
        Assert.Equal(HttpStatusCode.Conflict, quotaError.StatusCode);

        await client.SetRetentionAsync("iotsharp-governance-client", retainCurrentForDays: 1);
        var retentionError = await Assert.ThrowsAsync<SndbServerException>(() =>
            client.DeleteObjectAsync("iotsharp-governance-client", "firmware/a.bin"));
        Assert.Equal("object_retained", retentionError.Error);

        await client.SetRetentionAsync("iotsharp-governance-client");
        var hold = await client.SetLegalHoldAsync("iotsharp-governance-client", "firmware/a.bin", enabled: true, reason: "client review");
        Assert.True(hold.Enabled);

        var holdError = await Assert.ThrowsAsync<SndbServerException>(() =>
            client.DeleteObjectAsync("iotsharp-governance-client", "firmware/a.bin"));
        Assert.Equal("object_legal_hold", holdError.Error);

        await client.SetLegalHoldAsync("iotsharp-governance-client", "firmware/a.bin", enabled: false);
        await client.DeleteObjectAsync("iotsharp-governance-client", "firmware/a.bin");

        var audit = await client.ListAuditAsync("iotsharp-governance-client", "firmware/");
        Assert.Contains(audit, entry => entry.Action == "object.legal_hold.enable");
        Assert.Contains(audit, entry => entry.Action == "object.delete_marker");
    }
}
