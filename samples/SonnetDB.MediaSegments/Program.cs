using System.Globalization;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Media;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticContent;

if (args.Length < 3)
{
    Console.Error.WriteLine("ingest <db> <media-file> <content-id> <mime-type> <segments.json> | import <db> <manifest.json> | query <db> <content-id> [text] [from-ms] [to-ms]");
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; deadline.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = args[1] });
    var store = new MediaSegmentStore(database);
    switch (args[0])
    {
        case "ingest" when args.Length == 6:
            {
                byte[] input = await ReadBounded(args[5], deadline.Token);
                SemanticContentSegment[] segments = JsonSerializer.Deserialize(input, MediaSegmentJsonContext.Default.SemanticContentSegmentArray)
                    ?? throw new InvalidDataException("segments 不能为 null。");
                var objects = new SndbObjectStore(database);
                objects.CreateBucket("media");
                await using var stream = File.OpenRead(args[2]);
                SndbObjectInfo source = await objects.PutObjectAsync("media", args[3], stream, args[4], cancellationToken: deadline.Token);
                var manifest = new SemanticContentManifest(args[3], new(source.Bucket, source.Key, source.VersionId, source.ETag),
                    source.Sha256, source.ContentType, args[4].StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                        ? SemanticContentModality.Audio : SemanticContentModality.Video, source.SizeBytes,
                    source: Path.GetFileName(args[2]))
                { Segments = segments };
                Console.WriteLine(JsonSerializer.Serialize(store.Import(manifest, deadline.Token), MediaSegmentJsonContext.Default.SemanticContentManifest));
                break;
            }
        case "import" when args.Length == 3:
            {
                byte[] input = await ReadBounded(args[2], deadline.Token);
                Console.WriteLine(JsonSerializer.Serialize(store.ImportJson(input, deadline.Token), MediaSegmentJsonContext.Default.SemanticContentManifest));
                break;
            }
        case "query" when args.Length is >= 3 and <= 6:
            {
                var query = new MediaSegmentQuery
                {
                    Text = args.Length > 3 ? args[3] : null,
                    FromMs = args.Length > 4 ? long.Parse(args[4], CultureInfo.InvariantCulture) : 0,
                    ToMs = args.Length > 5 ? long.Parse(args[5], CultureInfo.InvariantCulture) : long.MaxValue,
                };
                Console.WriteLine(JsonSerializer.Serialize(store.Query(args[2], query, deadline.Token), MediaSegmentJsonContext.Default.MediaSegmentQueryResult));
                break;
            }
        default:
            Console.Error.WriteLine("未知命令或参数数量错误。");
            return 2;
    }
    return 0;
}
finally
{
    Console.CancelKeyPress -= cancel;
}

static async Task<byte[]> ReadBounded(string path, CancellationToken token)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    if (stream.Length is <= 0 or > MediaSegmentStore.MaxManifestBytes)
        throw new InvalidDataException("JSON 输入必须非空且不超过 4 MiB。");
    byte[] bytes = new byte[(int)stream.Length];
    await stream.ReadExactlyAsync(bytes, token);
    byte[] tail = new byte[1];
    if (await stream.ReadAsync(tail, token) != 0)
        throw new InvalidDataException("读取期间 JSON 文件增长。");
    return bytes;
}
