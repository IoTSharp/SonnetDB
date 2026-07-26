using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

var options = SampleOptions.Parse(args);
if (string.IsNullOrWhiteSpace(options.Token))
{
    Console.Error.WriteLine("请通过 --token 或 SONNETDB_TOKEN 提供具备数据库读写权限的 Token。");
    SampleOptions.PrintUsage();
    return 2;
}

Directory.CreateDirectory(options.ImageDirectory);
if (!EnumerateImages(options.ImageDirectory).Any())
{
    DemoImages.Generate(options.ImageDirectory);
    Console.WriteLine($"generated_demo_images={Path.GetFullPath(options.ImageDirectory)}");
}

using var client = new HttpClient { BaseAddress = new Uri(options.ServerUrl, UriKind.Absolute) };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);

var runtime = await GetAsync(
    client,
    "/v1/semantic-search/status",
    SampleJsonContext.Default.SemanticSearchStatusResponse);
Console.WriteLine(
    $"provider={runtime.Provider}; profile={runtime.Profile}; backend={runtime.EffectiveBackend}; ready={runtime.Ready}");
if (!runtime.Ready)
{
    Console.Error.WriteLine(runtime.Reason ?? "SigLIP2 provider 尚未就绪。");
    return 3;
}

await EnsureDatabaseAsync(client, options.Database);
await EnsureBucketAsync(client, options.Database, options.Bucket);
await EnableSemanticProcessingAsync(client, options.Database, options.Bucket);

var uploaded = new List<UploadedImage>();
foreach (string path in EnumerateImages(options.ImageDirectory))
{
    string fileName = Path.GetFileName(path);
    string objectKey = $"inspection/{fileName}";
    string imageClass = ImageClass(fileName);
    await UploadAsync(client, options.Database, options.Bucket, objectKey, path, imageClass);
    var processing = await WaitForProcessingAsync(
        client,
        options.Database,
        options.Bucket,
        objectKey,
        options.ProcessingTimeout);
    uploaded.Add(new UploadedImage(path, objectKey, processing.SemanticImageId));
    Console.WriteLine(
        $"processed={objectKey}; status={processing.Status}; attempts={processing.Attempts}; thumbnail={processing.ThumbnailUrl}");
}

await BackfillAsync(client, options.Database, options.Bucket);

var annText = await SearchTextAsync(client, options, filter: null);
PrintSearch("text-ann", annText);

var filteredText = await SearchTextAsync(
    client,
    options,
    new ImageSearchFilter(
        options.Bucket,
        "inspection/",
        ContentType: null,
        new Dictionary<string, string> { ["site"] = "demo-plant" },
        Tags: null));
PrintSearch("text-filtered", filteredText);

UploadedImage queryImage = uploaded[0];
var imageSearch = await SearchImageAsync(client, options, queryImage.Path);
PrintSearch("image-ann", imageSearch);

string? similarId = annText.Hits.FirstOrDefault()?.Id ?? queryImage.SemanticImageId;
if (!string.IsNullOrWhiteSpace(similarId))
{
    var similar = await SearchSimilarAsync(client, options, similarId);
    PrintSearch("similar-by-id", similar);
}

return 0;

/// <summary>
/// 枚举样例支持的常见图片格式，保证上传顺序稳定。
/// </summary>
static IEnumerable<string> EnumerateImages(string directory)
{
    string[] extensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];
    return Directory.EnumerateFiles(directory)
        .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 创建数据库；已存在时保留原实例继续执行样例。
/// </summary>
static async Task EnsureDatabaseAsync(HttpClient client, string database)
{
    using var response = await client.PostAsJsonAsync(
        "/v1/db",
        new CreateDatabaseRequest(database),
        SampleJsonContext.Default.CreateDatabaseRequest);
    await EnsureSuccessAsync(response, HttpStatusCode.Conflict);
}

/// <summary>
/// 创建用于工业图片的对象桶。
/// </summary>
static async Task EnsureBucketAsync(HttpClient client, string database, string bucket)
{
    using var response = await client.PutAsJsonAsync(
        BucketUrl(database, bucket),
        new BucketCreateRequest("industrial-semantic-images"),
        SampleJsonContext.Default.BucketCreateRequest);
    await EnsureSuccessAsync(response);
}

/// <summary>
/// 显式开启异步 embedding 与 WebP 缩略图，保持服务端默认关闭语义不变。
/// </summary>
static async Task EnableSemanticProcessingAsync(HttpClient client, string database, string bucket)
{
    using var response = await client.PutAsJsonAsync(
        $"{BucketUrl(database, bucket)}?semantic",
        new BucketSemanticOptionsRequest(true, true, 320, 320, 80),
        SampleJsonContext.Default.BucketSemanticOptionsRequest);
    await EnsureSuccessAsync(response);
}

/// <summary>
/// 上传图片，并写入可供过滤检索使用的现场 metadata 与分类标签。
/// </summary>
static async Task UploadAsync(
    HttpClient client,
    string database,
    string bucket,
    string key,
    string path,
    string imageClass)
{
    await using var stream = File.OpenRead(path);
    using var content = new StreamContent(stream);
    content.Headers.ContentType = new MediaTypeHeaderValue(ContentType(path));
    using var request = new HttpRequestMessage(HttpMethod.Put, ObjectUrl(database, bucket, key))
    {
        Content = content,
    };
    request.Headers.TryAddWithoutValidation("x-amz-meta-site", "demo-plant");
    request.Headers.TryAddWithoutValidation("x-amz-meta-line", "line-01");
    request.Headers.TryAddWithoutValidation(
        "x-amz-tagging",
        $"class={Uri.EscapeDataString(imageClass)}&source=sample");
    using var response = await client.SendAsync(request);
    await EnsureSuccessAsync(response);
}

/// <summary>
/// 等待持久化派生任务完成，并在终态失败时立即报告原因。
/// </summary>
static async Task<ObjectProcessingStatusResponse> WaitForProcessingAsync(
    HttpClient client,
    string database,
    string bucket,
    string key,
    TimeSpan timeout)
{
    DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
    string url = $"{ObjectUrl(database, bucket, key)}?processing";
    while (DateTimeOffset.UtcNow < deadline)
    {
        using var response = await client.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await Task.Delay(250);
            continue;
        }
        await EnsureSuccessAsync(response);
        var status = await response.Content.ReadFromJsonAsync(
            SampleJsonContext.Default.ObjectProcessingStatusResponse)
            ?? throw new InvalidDataException("处理状态响应为空。");
        if (string.Equals(status.Status, "completed", StringComparison.Ordinal))
            return status;
        if (status.Status is "failed" or "cancelled" or "superseded")
            throw new InvalidOperationException($"对象 {key} 派生失败：{status.Status} {status.Error}");
        await Task.Delay(250);
    }

    throw new TimeoutException($"等待对象 {key} 派生完成超时。");
}

/// <summary>
/// 对桶内当前对象发起幂等补录，展示存量图片接入流程。
/// </summary>
static async Task BackfillAsync(HttpClient client, string database, string bucket)
{
    using var response = await client.PostAsync($"{BucketUrl(database, bucket)}?semantic", content: null);
    await EnsureSuccessAsync(response);
    var result = await response.Content.ReadFromJsonAsync(
        SampleJsonContext.Default.BucketSemanticBackfillResponse)
        ?? throw new InvalidDataException("Backfill 响应为空。");
    Console.WriteLine(
        $"backfill_scanned={result.ScannedObjects}; queued={result.QueuedObjects}; skipped={result.SkippedObjects}");
}

/// <summary>
/// 执行文搜图；传入过滤器时服务端使用完整精确余弦扫描。
/// </summary>
static async Task<ImageSearchResponse> SearchTextAsync(
    HttpClient client,
    SampleOptions options,
    ImageSearchFilter? filter)
{
    using var response = await client.PostAsJsonAsync(
        $"/v1/db/{Uri.EscapeDataString(options.Database)}/images/search/text",
        new TextSearchRequest(options.TextQuery, 5, null, filter, Explain: true),
        SampleJsonContext.Default.TextSearchRequest);
    await EnsureSuccessAsync(response);
    return await response.Content.ReadFromJsonAsync(SampleJsonContext.Default.ImageSearchResponse)
        ?? throw new InvalidDataException("文搜图响应为空。");
}

/// <summary>
/// 使用本地图片内容执行图搜图，不经过 Base64 转换。
/// </summary>
static async Task<ImageSearchResponse> SearchImageAsync(
    HttpClient client,
    SampleOptions options,
    string imagePath)
{
    await using var stream = File.OpenRead(imagePath);
    using var content = new StreamContent(stream);
    content.Headers.ContentType = new MediaTypeHeaderValue(ContentType(imagePath));
    using var response = await client.PostAsync(
        $"/v1/db/{Uri.EscapeDataString(options.Database)}/images/search/image?topK=5&explain=true",
        content);
    await EnsureSuccessAsync(response);
    return await response.Content.ReadFromJsonAsync(SampleJsonContext.Default.ImageSearchResponse)
        ?? throw new InvalidDataException("图搜图响应为空。");
}

/// <summary>
/// 复用已持久化 embedding 执行 similar-by-id。
/// </summary>
static async Task<ImageSearchResponse> SearchSimilarAsync(
    HttpClient client,
    SampleOptions options,
    string id)
{
    using var response = await client.PostAsJsonAsync(
        $"/v1/db/{Uri.EscapeDataString(options.Database)}/images/{Uri.EscapeDataString(id)}/similar",
        new SimilarSearchRequest(5, null, null, Explain: true),
        SampleJsonContext.Default.SimilarSearchRequest);
    await EnsureSuccessAsync(response);
    return await response.Content.ReadFromJsonAsync(SampleJsonContext.Default.ImageSearchResponse)
        ?? throw new InvalidDataException("相似图片响应为空。");
}

/// <summary>
/// 输出后端、候选规模和按分数排序的命中，便于确认 USearch 与过滤模式。
/// </summary>
static void PrintSearch(string name, ImageSearchResponse response)
{
    Console.WriteLine(
        $"search={name}; backend={response.Backend}; mode={response.SearchMode}; candidates={response.FilteredCandidateCount}/{response.CandidateCount}");
    foreach (var hit in response.Hits)
    {
        Console.WriteLine(
            $"  score={hit.Score:F4}; id={hit.Id}; object={hit.SourceBucket}/{hit.SourceKey}; thumbnail={hit.ThumbnailUrl}");
    }
}

/// <summary>
/// 读取 source-generated JSON 响应。
/// </summary>
static async Task<T> GetAsync<T>(
    HttpClient client,
    string url,
    System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
{
    using var response = await client.GetAsync(url);
    await EnsureSuccessAsync(response);
    return await response.Content.ReadFromJsonAsync(typeInfo)
        ?? throw new InvalidDataException($"GET {url} 响应为空。");
}

/// <summary>
/// 校验 HTTP 结果，并保留服务端错误正文用于排障。
/// </summary>
static async Task EnsureSuccessAsync(HttpResponseMessage response, params HttpStatusCode[] allowed)
{
    if (response.IsSuccessStatusCode || allowed.Contains(response.StatusCode))
        return;
    string body = await response.Content.ReadAsStringAsync();
    throw new HttpRequestException(
        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
        inner: null,
        response.StatusCode);
}

static string BucketUrl(string database, string bucket)
    => $"/v1/db/{Uri.EscapeDataString(database)}/s3/{Uri.EscapeDataString(bucket)}";

static string ObjectUrl(string database, string bucket, string key)
    => $"{BucketUrl(database, bucket)}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}";

static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".jpg" or ".jpeg" => "image/jpeg",
    ".png" => "image/png",
    ".webp" => "image/webp",
    ".bmp" => "image/bmp",
    _ => "application/octet-stream",
};

static string ImageClass(string fileName)
{
    int separator = fileName.IndexOf('-', StringComparison.Ordinal);
    return (separator > 0 ? fileName[..separator] : Path.GetFileNameWithoutExtension(fileName))
        .ToLowerInvariant();
}

internal sealed record SampleOptions(
    string ServerUrl,
    string Token,
    string Database,
    string Bucket,
    string ImageDirectory,
    string TextQuery,
    TimeSpan ProcessingTimeout)
{
    /// <summary>
    /// 解析命令行和环境变量，命令行值优先。
    /// </summary>
    internal static SampleOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                continue;
            values[args[i][2..]] = args[++i];
        }

        string Value(string key, string environment, string fallback)
            => values.TryGetValue(key, out string? value)
                ? value
                : Environment.GetEnvironmentVariable(environment) ?? fallback;

        int timeoutSeconds = int.TryParse(
            Value("timeout-seconds", "SONNETDB_PROCESSING_TIMEOUT_SECONDS", "120"),
            out int parsedTimeout)
            ? Math.Clamp(parsedTimeout, 5, 3600)
            : 120;
        return new SampleOptions(
            Value("server", "SONNETDB_URL", "http://127.0.0.1:5080"),
            Value("token", "SONNETDB_TOKEN", string.Empty),
            Value("database", "SONNETDB_DATABASE", "industrial-images"),
            Value("bucket", "SONNETDB_BUCKET", "inspection-media"),
            Value(
                "images",
                "SONNETDB_IMAGE_DIR",
                Path.Combine("samples", "SonnetDB.SemanticImages", "demo-images")),
            Value("text", "SONNETDB_TEXT_QUERY", "red forklift in an industrial warehouse"),
            TimeSpan.FromSeconds(timeoutSeconds));
    }

    /// <summary>
    /// 输出样例的最小运行参数。
    /// </summary>
    internal static void PrintUsage()
        => Console.WriteLine(
            "dotnet run --project samples/SonnetDB.SemanticImages -- --token <token> [--server http://127.0.0.1:5080] [--images <directory>]");
}

internal static class DemoImages
{
    private const int Width = 512;
    private const int Height = 320;

    /// <summary>
    /// 生成三张无外部版权依赖的确定性工业场景 PNG。
    /// </summary>
    internal static void Generate(string directory)
    {
        Directory.CreateDirectory(directory);
        GenerateForklift(Path.Combine(directory, "forklift-red-01.png"));
        GeneratePump(Path.Combine(directory, "pump-blue-01.png"));
        GenerateConveyor(Path.Combine(directory, "conveyor-yellow-01.png"));
    }

    private static void GenerateForklift(string path)
    {
        using var image = CreateFactoryBackground();
        FillRect(image, 70, 185, 205, 70, new Rgb24(190, 36, 42));
        FillRect(image, 95, 125, 105, 70, new Rgb24(214, 54, 58));
        FillRect(image, 110, 138, 72, 48, new Rgb24(82, 124, 145));
        FillRect(image, 285, 90, 18, 170, new Rgb24(48, 57, 65));
        FillRect(image, 300, 235, 130, 12, new Rgb24(48, 57, 65));
        FillRect(image, 360, 190, 70, 45, new Rgb24(156, 104, 52));
        FillCircle(image, 115, 255, 31, new Rgb24(31, 36, 41));
        FillCircle(image, 235, 255, 31, new Rgb24(31, 36, 41));
        FillCircle(image, 115, 255, 13, new Rgb24(165, 171, 176));
        FillCircle(image, 235, 255, 13, new Rgb24(165, 171, 176));
        image.SaveAsPng(path);
    }

    private static void GeneratePump(string path)
    {
        using var image = CreateFactoryBackground();
        FillRect(image, 105, 155, 230, 92, new Rgb24(34, 103, 173));
        FillCircle(image, 105, 201, 46, new Rgb24(50, 126, 194));
        FillCircle(image, 335, 201, 46, new Rgb24(25, 78, 132));
        FillRect(image, 185, 120, 72, 35, new Rgb24(84, 94, 102));
        FillRect(image, 65, 90, 32, 85, new Rgb24(40, 142, 98));
        FillRect(image, 65, 75, 225, 25, new Rgb24(40, 142, 98));
        FillRect(image, 355, 75, 28, 126, new Rgb24(40, 142, 98));
        FillCircle(image, 369, 110, 27, new Rgb24(201, 45, 48));
        FillRect(image, 75, 247, 310, 18, new Rgb24(65, 72, 78));
        image.SaveAsPng(path);
    }

    private static void GenerateConveyor(string path)
    {
        using var image = CreateFactoryBackground();
        FillRect(image, 55, 160, 400, 42, new Rgb24(225, 169, 39));
        FillRect(image, 70, 145, 370, 14, new Rgb24(58, 64, 69));
        for (int x = 78; x <= 430; x += 44)
            FillCircle(image, x, 181, 14, new Rgb24(85, 92, 99));
        FillRect(image, 82, 100, 65, 45, new Rgb24(166, 105, 49));
        FillRect(image, 205, 92, 78, 53, new Rgb24(181, 120, 56));
        FillRect(image, 350, 108, 58, 37, new Rgb24(147, 91, 43));
        FillRect(image, 85, 202, 18, 70, new Rgb24(65, 72, 78));
        FillRect(image, 408, 202, 18, 70, new Rgb24(65, 72, 78));
        image.SaveAsPng(path);
    }

    private static Image<Rgb24> CreateFactoryBackground()
    {
        var image = new Image<Rgb24>(Width, Height, new Rgb24(221, 229, 232));
        FillRect(image, 0, 250, Width, 70, new Rgb24(132, 139, 142));
        FillRect(image, 30, 35, 452, 12, new Rgb24(83, 91, 96));
        for (int x = 55; x < Width; x += 95)
            FillRect(image, x, 47, 10, 203, new Rgb24(170, 179, 183));
        return image;
    }

    private static void FillRect(Image<Rgb24> image, int x, int y, int width, int height, Rgb24 color)
    {
        int right = Math.Min(image.Width, x + width);
        int bottom = Math.Min(image.Height, y + height);
        for (int row = Math.Max(0, y); row < bottom; row++)
        {
            for (int column = Math.Max(0, x); column < right; column++)
                image[column, row] = color;
        }
    }

    private static void FillCircle(Image<Rgb24> image, int centerX, int centerY, int radius, Rgb24 color)
    {
        int radiusSquared = radius * radius;
        for (int y = Math.Max(0, centerY - radius); y < Math.Min(image.Height, centerY + radius + 1); y++)
        {
            for (int x = Math.Max(0, centerX - radius); x < Math.Min(image.Width, centerX + radius + 1); x++)
            {
                int dx = x - centerX;
                int dy = y - centerY;
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                    image[x, y] = color;
            }
        }
    }
}

internal sealed record CreateDatabaseRequest(string Name);
internal sealed record BucketCreateRequest(string Purpose);
internal sealed record BucketSemanticOptionsRequest(
    bool AsyncIngestionEnabled,
    bool ThumbnailEnabled,
    int ThumbnailMaxWidth,
    int ThumbnailMaxHeight,
    int ThumbnailQuality);
internal sealed record BucketSemanticBackfillResponse(
    string Bucket,
    int ScannedObjects,
    int QueuedObjects,
    int SkippedObjects);
internal sealed record SemanticSearchStatusResponse(
    bool Enabled,
    bool Ready,
    string Provider,
    string Profile,
    int Dimensions,
    string ConfiguredBackend,
    string EffectiveBackend,
    IReadOnlyList<string> Capabilities,
    string? Reason);
internal sealed record ObjectProcessingStatusResponse(
    string JobId,
    string Bucket,
    string Key,
    string VersionId,
    string Operation,
    string Status,
    bool SemanticRequested,
    bool ThumbnailRequested,
    int Attempts,
    string? Error,
    string? SemanticImageId,
    string? ThumbnailUrl,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? NextAttemptUtc);
internal sealed record ImageSearchFilter(
    string? SourceBucket,
    string? SourceKeyPrefix,
    string? ContentType,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyDictionary<string, string>? Tags);
internal sealed record TextSearchRequest(
    string Text,
    int? TopK,
    double? MinScore,
    ImageSearchFilter? Filter,
    bool Explain);
internal sealed record SimilarSearchRequest(
    int? TopK,
    double? MinScore,
    ImageSearchFilter? Filter,
    bool Explain);
internal sealed record ImageSearchHit(
    string Id,
    double Score,
    double Distance,
    string? FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string? SourceUri,
    string ContentUrl,
    DateTimeOffset UpdatedUtc,
    string? SourceBucket,
    string? SourceKey,
    string? SourceVersionId,
    string? ThumbnailUrl,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyDictionary<string, string>? Tags);
internal sealed record ImageSearchResponse(
    string QueryKind,
    string Profile,
    string Backend,
    IReadOnlyList<ImageSearchHit> Hits,
    string? SearchMode,
    int? CandidateCount,
    int? FilteredCandidateCount);
internal sealed record UploadedImage(string Path, string ObjectKey, string? SemanticImageId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateDatabaseRequest))]
[JsonSerializable(typeof(BucketCreateRequest))]
[JsonSerializable(typeof(BucketSemanticOptionsRequest))]
[JsonSerializable(typeof(BucketSemanticBackfillResponse))]
[JsonSerializable(typeof(SemanticSearchStatusResponse))]
[JsonSerializable(typeof(ObjectProcessingStatusResponse))]
[JsonSerializable(typeof(TextSearchRequest))]
[JsonSerializable(typeof(SimilarSearchRequest))]
[JsonSerializable(typeof(ImageSearchResponse))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;
