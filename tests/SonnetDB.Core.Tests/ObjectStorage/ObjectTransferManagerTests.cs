using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Data.ObjectStorage;

namespace SonnetDB.Core.Tests.ObjectStorage;

public sealed class ObjectTransferManagerTests
{
    [Fact]
    public void Options_InvalidConcurrency_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SndbObjectTransferOptions(MaxConcurrency: 0).Validate());

    [Fact]
    public async Task UploadAsync_MultipartStreamsBoundedPartsAndVerifiesChecksum()
    {
        byte[] source = Enumerable.Range(0, 25).Select(static value => (byte)value).ToArray();
        using var handler = new TransferHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://object-transfer.test/") };
        using var client = new SndbObjectStorageClient(
            "Data Source=sonnetdb+http://object-transfer.test/testdb;Protocol=rest;Timeout=30", http);
        var progress = new List<SndbObjectTransferProgress>();
        var manager = new SndbObjectTransferManager(client, new SndbObjectTransferOptions(
            MultipartThresholdBytes: 8,
            PartSizeBytes: 5,
            MaxConcurrency: 2,
            MaxRetries: 1));

        SndbObjectTransferResult result = await manager.UploadAsync(
            "media",
            "large.bin",
            new MemoryStream(source),
            progress: new Progress<SndbObjectTransferProgress>(progress.Add));

        Assert.Equal(5, result.Parts);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(source, handler.CompletedContent.ToArray());
        Assert.Contains(progress, item => item.Operation == "upload" && item.CompletedParts == 5);
    }

    [Fact]
    public async Task DownloadAsync_StreamsAndVerifiesChecksum()
    {
        byte[] expected = Encoding.UTF8.GetBytes("streamed object");
        using var handler = new TransferHandler(expected);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://object-transfer.test/") };
        using var client = new SndbObjectStorageClient(
            "Data Source=sonnetdb+http://object-transfer.test/testdb;Protocol=rest;Timeout=30", http);
        var manager = new SndbObjectTransferManager(client);
        using var destination = new MemoryStream();

        SndbObjectTransferResult result = await manager.DownloadAsync("media", "small.txt", destination);

        Assert.Equal(expected, destination.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), result.Sha256);
    }

    [Fact]
    public async Task UploadAsync_WithManifest_ResumesExistingMultipartSession()
    {
        byte[] source = Enumerable.Range(0, 20).Select(static value => (byte)value).ToArray();
        string manifest = Path.Combine(Path.GetTempPath(), "sndb-transfer-" + Guid.NewGuid().ToString("N") + ".json");
        using var handler = new TransferHandler(failFirstPart: true);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://object-transfer.test/") };
        using var client = new SndbObjectStorageClient(
            "Data Source=sonnetdb+http://object-transfer.test/testdb;Protocol=rest;Timeout=30", http);
        var options = new SndbObjectTransferOptions(8, 5, 2, 0, manifest);
        var manager = new SndbObjectTransferManager(client, options);

        await Assert.ThrowsAsync<HttpRequestException>(() => manager.UploadAsync("media", "resume.bin", new MemoryStream(source)));
        Assert.True(File.Exists(manifest));
        SndbObjectTransferResult resumed = await manager.UploadAsync("media", "resume.bin", new MemoryStream(source));

        Assert.True(resumed.Resumed);
        Assert.False(File.Exists(manifest));
    }

    [Fact]
    public async Task UploadManyAsync_ReturnsPerObjectErrorsInInputOrder()
    {
        using var handler = new TransferHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://object-transfer.test/") };
        using var client = new SndbObjectStorageClient(
            "Data Source=sonnetdb+http://object-transfer.test/testdb;Protocol=rest;Timeout=30", http);
        var manager = new SndbObjectTransferManager(client, new SndbObjectTransferOptions(MultipartThresholdBytes: 100));
        var results = await manager.UploadManyAsync(
        [
            new("media", "ok.txt", new MemoryStream("ok"u8.ToArray())),
            new("media", "", new MemoryStream("bad"u8.ToArray())),
        ]);

        Assert.Equal("ok.txt", results[0].Key);
        Assert.NotNull(results[0].Result);
        Assert.Equal(string.Empty, results[1].Key);
        Assert.NotNull(results[1].Error);
    }

    private sealed class TransferHandler : HttpMessageHandler
    {
        private readonly Dictionary<int, byte[]> _parts = [];
        private readonly byte[] _download;
        private readonly bool _failFirstPart;
        private bool _failed;
        private string _key = "large.bin";
        public MemoryStream CompletedContent { get; } = new();

        public TransferHandler(byte[]? download = null, bool failFirstPart = false)
        {
            _download = download ?? [];
            _failFirstPart = failFirstPart;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.Query.Contains("uploads", StringComparison.Ordinal))
            {
                _key = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
                return Json(new { bucket = "media", key = _key, uploadId = "upload-1", contentType = "application/octet-stream", initiatedUtc = DateTimeOffset.UtcNow, expiresUtc = DateTimeOffset.UtcNow.AddHours(1), metadata = new Dictionary<string, string>(), tags = new Dictionary<string, string>() });
            }

            if (request.Method == HttpMethod.Put && request.RequestUri!.Query.Contains("uploadId", StringComparison.Ordinal))
            {
                if (_failFirstPart && !_failed)
                {
                    _failed = true;
                    throw new HttpRequestException("simulated part failure");
                }
                int partNumber = int.Parse(ParseQuery(request.RequestUri.Query, "partNumber"));
                byte[] bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                _parts[partNumber] = bytes;
                return Json(new { partNumber, sizeBytes = bytes.Length, eTag = "\"part-" + partNumber + "\"", sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.Query.Contains("uploadId", StringComparison.Ordinal))
            {
                CompletedContent.SetLength(0);
                foreach (byte[] part in _parts.OrderBy(static pair => pair.Key).Select(static pair => pair.Value))
                    CompletedContent.Write(part);
                return Json(ObjectInfo(CompletedContent.ToArray(), _key));
            }

            if (request.Method == HttpMethod.Put)
            {
                _key = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
                byte[] bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                return Json(ObjectInfo(bytes, _key));
            }

            if (request.Method == HttpMethod.Get)
            {
                byte[] bytes = _download;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                };
                response.Content.Headers.ContentType = new("application/octet-stream");
                response.Headers.TryAddWithoutValidation("ETag", "\"download\"");
                response.Headers.TryAddWithoutValidation("x-amz-meta-sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                return response;
            }

            throw new InvalidOperationException(request.Method + " " + request.RequestUri);
        }

        private static object ObjectInfo(byte[] bytes, string key) => new
        {
            bucket = "media", key, versionId = "v1", contentType = "application/octet-stream", sizeBytes = bytes.Length,
            eTag = "\"object\"", sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), isDeleteMarker = false,
            createdUtc = DateTimeOffset.UtcNow, updatedUtc = DateTimeOffset.UtcNow,
            metadata = new Dictionary<string, string>(), tags = new Dictionary<string, string>(),
        };

        private static HttpResponseMessage Json(object value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
            };

        private static string ParseQuery(string query, string name)
            => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(static pair => pair.Split('=', 2))
                .First(pair => pair[0] == name)[1];
    }
}
