using System.Buffers;
using System.Data;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Data.Documents;
using SonnetDB.Data.Kv;
using SonnetDB.Data.Mq;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.ObjectStorage;
using SonnetDB.Protocol;
using SonnetDB.Query;
using Xunit;

namespace SonnetDB.Parity.Runner;

/// <summary>
/// M28 P5b #244 全模型接入收口测试：在真实 Kestrel 上分别通过 REST 与二进制帧路径执行同等数据面操作，
/// 覆盖 MQ、TSDB、SQL、Vector、KV、Object、Document 七个 service 的稳定语义等价。
/// </summary>
public sealed class FrameRestTransportParitySuite : IAsyncLifetime
{
    private const string AdminToken = "transport-parity-admin";
    private const string DbName = "transport_parity";
    private const string FrameContentType = "application/x-sonnetdb-frame";

    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-transport-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };

        _app = BuildTestServer(options);
        await _app.StartAsync();
        _baseUrl = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        using var http = CreateHttpClient();
        using var body = new StringContent($"{{\"name\":\"{DbName}\"}}", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/v1/db", body);
        response.EnsureSuccessStatusCode();
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
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch
            {
                // 测试清理尽力而为，避免 Windows 上短暂文件句柄延迟影响断言结果。
            }
        }
    }

    [Fact]
    public async Task FrameHttp2_And_Rest_AreSemanticallyEquivalent_ForAllServices()
    {
        await AssertMqParityAsync();
        AssertTsdbParity();
        AssertSqlParity();
        await AssertVectorParityAsync();
        await AssertKvParityAsync();
        await AssertObjectParityAsync();
        await AssertDocumentParityAsync();
    }

    private async Task AssertMqParityAsync()
    {
        MqSnapshot frame = await ExerciseMqAsync("frame-http2", "mq_frame");
        MqSnapshot rest = await ExerciseMqAsync("rest", "mq_rest");

        Assert.Equal(rest, frame);
    }

    private async Task<MqSnapshot> ExerciseMqAsync(string protocol, string topic)
    {
        using var mq = new SndbMqClient(ConnString(protocol));
        byte[] firstPayload = [0x00, 0x7F, 0x80, 0xFF];
        var headers = new Dictionary<string, string> { ["src"] = "device-alpha", ["kind"] = "parity" };

        long firstOffset = await mq.PublishAsync(topic, firstPayload, headers);
        var batchOffsets = await mq.PublishManyAsync(topic,
        [
            new SndbMqPublishEntry(new byte[] { 1, 2, 3 }, new Dictionary<string, string> { ["seq"] = "2" }),
            new SndbMqPublishEntry(new byte[] { 4, 5, 6 }, new Dictionary<string, string> { ["seq"] = "3" }),
        ]);

        var pulled = await mq.PullAsync(topic, "group-a", 10);
        long nextOffset = await mq.AckAsync(topic, "group-a", pulled[^1].Offset);
        var stats = await mq.GetStatsAsync(topic);

        return new MqSnapshot(
            firstOffset,
            string.Join(',', batchOffsets),
            string.Join(',', pulled.Select(static m => Convert.ToHexString(m.Payload))),
            string.Join(';', pulled.Select(static m => string.Join('|', m.Headers.OrderBy(static h => h.Key).Select(static h => h.Key + "=" + h.Value)))),
            nextOffset,
            stats.NextOffset);
    }

    private void AssertTsdbParity()
    {
        WriteLineProtocol("frame-http2", "ts_frame");
        WriteLineProtocol("rest", "ts_rest");

        var frameRows = QueryRows("rest", "SELECT time, host, value FROM ts_frame ORDER BY time").Rows;
        var restRows = QueryRows("rest", "SELECT time, host, value FROM ts_rest ORDER BY time").Rows;
        AssertRowsEqual(restRows, frameRows);
    }

    private void WriteLineProtocol(string protocol, string measurement)
    {
        using var connection = Open(protocol);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.TableDirect;
        command.CommandText =
            $"{measurement}\n" +
            $"{measurement},host=a value=1.5 1000\n" +
            $"{measurement},host=a value=2.5 2000\n" +
            $"{measurement},host=b value=3.5 3000";
        Assert.Equal(3, command.ExecuteNonQuery());
    }

    private void AssertSqlParity()
    {
        ExecSql("rest", "CREATE TABLE sql_devices (id INT, name STRING, score FLOAT, PRIMARY KEY (id))");
        ExecSql("rest", "INSERT INTO sql_devices (id, name, score) VALUES (1, 'alpha', 9.5), (2, 'beta', 8.25)");

        string sql = "SELECT id, name, score FROM sql_devices ORDER BY id";
        var frame = QueryRows("frame-http2", sql);
        var rest = QueryRows("rest", sql);

        Assert.Equal(rest.Columns, frame.Columns);
        AssertRowsEqual(rest.Rows, frame.Rows);
    }

    private async Task AssertVectorParityAsync()
    {
        ExecSql("rest", "CREATE MEASUREMENT vec_docs (source TAG, embedding FIELD VECTOR(3))");
        ExecSql("rest", "INSERT INTO vec_docs (source, embedding, time) VALUES " +
                        "('a', [1, 0, 0], 1000), " +
                        "('b', [1, 1, 0], 2000), " +
                        "('c', [0, 1, 0], 3000)");

        var frame = await QueryVectorFrameAsync();
        var rest = QueryRows("rest", "SELECT * FROM knn(vec_docs, embedding, [1, 0, 0], 3)");

        Assert.Equal(rest.Columns, frame.Columns);
        int time = Array.IndexOf(frame.Columns, "time");
        int distance = Array.IndexOf(frame.Columns, "distance");
        int source = Array.IndexOf(frame.Columns, "source");
        int embedding = Array.IndexOf(frame.Columns, "embedding");
        Assert.True(time >= 0 && distance >= 0 && source >= 0 && embedding >= 0);

        Assert.Equal(rest.Rows.Count, frame.Rows.Count);
        for (int i = 0; i < frame.Rows.Count; i++)
        {
            Assert.Equal(rest.Rows[i][time], frame.Rows[i][time]);
            Assert.Equal(rest.Rows[i][source], frame.Rows[i][source]);
            Assert.Equal(Convert.ToDouble(rest.Rows[i][distance]), Convert.ToDouble(frame.Rows[i][distance]), 9);
            Assert.IsType<float[]>(frame.Rows[i][embedding]);
        }
    }

    private async Task<QuerySnapshot> QueryVectorFrameAsync()
    {
        using var http = CreateHttpClient();
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, 7, DbName, "vec_docs", "embedding", [1f, 0f, 0f], 3, KnnMetric.Cosine);
        var frames = await PostFramesAsync(http, writer.WrittenMemory.ToArray());

        Assert.All(frames, frame =>
        {
            Assert.Equal((byte)FrameService.Vector, frame.Header.Service);
            Assert.Equal((byte)VectorFrameOp.Search, frame.Header.Op);
            Assert.Equal(7u, frame.Header.StreamId);
            Assert.True(frame.Header.IsResponse);
            Assert.False(frame.Header.IsError, frame.Header.IsError ? FrameCodec.ReadErrorPayload(frame.Payload).Message : string.Empty);
        });

        string[] columns = SqlFrameCodec.DecodeQueryMetaFrame(frames[0].Payload);
        var rows = new List<object?[]>();
        for (int i = 1; i < frames.Count - 1; i++)
            rows.AddRange(SqlFrameCodec.DecodeQueryRowsFrame(frames[i].Payload));
        (long rowCount, _) = SqlFrameCodec.DecodeQueryEndFrame(frames[^1].Payload);
        Assert.Equal(rowCount, rows.Count);
        return new QuerySnapshot(columns, rows);
    }

    private async Task AssertKvParityAsync()
    {
        KvSnapshot frame = await ExerciseKvAsync("frame-http2", "kv_frame");
        KvSnapshot rest = await ExerciseKvAsync("rest", "kv_rest");

        Assert.Equal(rest, frame);
    }

    private async Task<KvSnapshot> ExerciseKvAsync(string protocol, string keyspace)
    {
        using var kv = new SndbKvClient(ConnString(protocol));
        const string ns = "devices";
        await kv.SetAsync(keyspace, ns, "a", [1, 2, 3]);
        await kv.SetAsync(keyspace, ns, "b", [0x80, 0xFF]);

        var got = await kv.GetAsync(keyspace, ns, "a");
        Assert.NotNull(got);
        var scan = await kv.ScanPrefixAsync(keyspace, ns, string.Empty);

        return new KvSnapshot(
            got!.Key,
            Convert.ToHexString(got.Value),
            string.Join(';', scan.OrderBy(static entry => entry.Key).Select(static entry => entry.Key + "=" + Convert.ToHexString(entry.Value))));
    }

    private async Task AssertObjectParityAsync()
    {
        using (var admin = new SndbObjectStorageClient(ConnString("rest")))
        {
            await admin.CreateBucketAsync("obj-frame", "parity");
            await admin.CreateBucketAsync("obj-rest", "parity");
        }

        ObjectSnapshot frame = await ExerciseObjectAsync("frame-http2", "obj-frame", "payload.bin");
        ObjectSnapshot rest = await ExerciseObjectAsync("rest", "obj-rest", "payload.bin");
        Assert.Equal(rest, frame);
    }

    private async Task<ObjectSnapshot> ExerciseObjectAsync(string protocol, string bucket, string key)
    {
        using var client = new SndbObjectStorageClient(ConnString(protocol));
        byte[] content = Enumerable.Range(0, 4096).Select(static i => (byte)((i * 17) & 0xFF)).ToArray();
        var put = await client.PutObjectAsync(bucket, key, new MemoryStream(content), "application/octet-stream",
            new Dictionary<string, string> { ["origin"] = "parity" });

        var read = await client.OpenReadAsync(bucket, key);
        Assert.NotNull(read);
        byte[] roundTrip = await ReadAllAsync(read!);
        Assert.Equal(content, roundTrip);

        return new ObjectSnapshot(put.SizeBytes, put.ETag, put.Sha256, read!.Info.ContentType, Convert.ToHexString(roundTrip));
    }

    private async Task AssertDocumentParityAsync()
    {
        DocumentSnapshot frame = await ExerciseDocumentAsync("frame-http2", "doc_frame");
        DocumentSnapshot rest = await ExerciseDocumentAsync("rest", "doc_rest");
        Assert.Equal(rest, frame);
    }

    private async Task<DocumentSnapshot> ExerciseDocumentAsync(string protocol, string collection)
    {
        using var doc = new SndbDocumentClient(ConnString(protocol));
        await doc.CreateCollectionAsync(collection);
        var result = await doc.InsertManyAsync(collection,
        [
            new KeyValuePair<string, string>("d1", "{\"name\":\"alpha\",\"n\":1}"),
            new KeyValuePair<string, string>("d2", "{\"name\":\"beta\",\"n\":2}"),
        ]);
        Assert.Equal(2, result.Inserted);

        var one = await doc.FindOneAsync(collection, "d1");
        Assert.NotNull(one);
        var many = await doc.FindAsync(collection, new SndbDocumentFindOptions(Ids: ["d1", "d2"]));

        return new DocumentSnapshot(
            one!.Id,
            CanonicalJson(one.Json),
            string.Join(';', many.OrderBy(static d => d.Id).Select(static d => d.Id + "=" + CanonicalJson(d.Json))));
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

    private static string CanonicalJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private void ExecSql(string protocol, string sql)
    {
        using var connection = Open(protocol);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private QuerySnapshot QueryRows(string protocol, string sql)
    {
        using var connection = Open(protocol);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        string[] columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var raw = new object[reader.FieldCount];
            reader.GetValues(raw);
            var values = new object?[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                values[i] = raw[i] is DBNull ? null : raw[i];
            }
            rows.Add(values);
        }
        return new QuerySnapshot(columns, rows);
    }

    private SndbConnection Open(string protocol)
    {
        var connection = new SndbConnection(ConnString(protocol));
        connection.Open();
        return connection;
    }

    private string ConnString(string protocol)
        => $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{DbName};Token={AdminToken};Timeout=30;Protocol={protocol}";

    private HttpClient CreateHttpClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    private static async Task<List<FramePacket>> PostFramesAsync(HttpClient client, byte[] requestBody)
    {
        using var content = new ByteArrayContent(requestBody);
        content.Headers.ContentType = new MediaTypeHeaderValue(FrameContentType);
        using var response = await client.PostAsync("/v1/frame", content);
        response.EnsureSuccessStatusCode();
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        var frames = new List<FramePacket>();
        var buffer = new ReadOnlySequence<byte>(body);
        while (FrameCodec.TryReadFrame(ref buffer, out FrameHeader header, out ReadOnlySequence<byte> payload))
            frames.Add(new FramePacket(header, payload.ToArray()));
        Assert.Equal(0, buffer.Length);
        Assert.NotEmpty(frames);
        return frames;
    }

    private static WebApplication BuildTestServer(ServerOptions options)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-parity-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var settings = JsonSerializer.Serialize(new AppSettings(options));
        File.WriteAllText(Path.Combine(contentRoot, "appsettings.json"), settings);

        var app = global::SonnetDB.Program.BuildApp(
            ["--contentRoot", contentRoot, "--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0"]);
        app.Lifetime.ApplicationStopped.Register(static state =>
        {
            var root = (string)state!;
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 测试宿主配置目录清理尽力而为。
            }
        }, contentRoot);
        return app;
    }

    private static void AssertRowsEqual(IReadOnlyList<object?[]> expected, IReadOnlyList<object?[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int r = 0; r < expected.Count; r++)
        {
            Assert.Equal(expected[r].Length, actual[r].Length);
            for (int c = 0; c < expected[r].Length; c++)
                AssertCellEqual(expected[r][c], actual[r][c]);
        }
    }

    private static void AssertCellEqual(object? expected, object? actual)
    {
        if (expected is null || actual is null)
        {
            Assert.Equal(expected, actual);
            return;
        }

        if (expected is byte[] eb && actual is byte[] ab)
        {
            Assert.Equal(eb, ab);
            return;
        }

        if (expected is float[] ef && actual is float[] af)
        {
            Assert.Equal(ef, af);
            return;
        }

        if (IsFloating(expected) || IsFloating(actual))
        {
            Assert.Equal(Convert.ToDouble(expected), Convert.ToDouble(actual), 9);
            return;
        }

        Assert.Equal(expected, actual);
    }

    private static bool IsFloating(object value)
        => value is float or double or decimal;

    private sealed record AppSettings(ServerOptions SonnetDBServer);

    private sealed record QuerySnapshot(string[] Columns, List<object?[]> Rows);

    private sealed record MqSnapshot(
        long FirstOffset,
        string BatchOffsets,
        string PayloadHex,
        string HeaderPairs,
        long AckNextOffset,
        long StatsNextOffset);

    private sealed record KvSnapshot(string Key, string ValueHex, string ScanEntries);

    private sealed record ObjectSnapshot(long SizeBytes, string ETag, string Sha256, string ContentType, string ContentHex);

    private sealed record DocumentSnapshot(string FirstId, string FirstJson, string FoundDocuments);

    private sealed record FramePacket(FrameHeader Header, byte[] Payload);
}
