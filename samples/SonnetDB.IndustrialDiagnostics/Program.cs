using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

var options = IndustrialOptions.Parse(args);
if (string.IsNullOrWhiteSpace(options.Token))
{
    Console.Error.WriteLine("请通过 --token 或 SONNETDB_TOKEN 提供目标数据库写权限 Token。");
    IndustrialOptions.PrintUsage();
    return 2;
}

Directory.CreateDirectory(options.OutputDirectory);
using var client = new HttpClient { BaseAddress = new Uri(options.ServerUrl, UriKind.Absolute) };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);

var report = new DiagnosticReportBuilder(options);
try
{
    await EnsureDatabaseAsync(client, options.Database).ConfigureAwait(false);
    await EnsureSchemaAsync(client, options.Database).ConfigureAwait(false);

    var payloads = BuildPayloads();
    if (options.Transport is Transport.Http or Transport.Both)
    {
        foreach (var payload in payloads)
            report.AddHttpRows(await WriteHttpAsync(client, options.Database, payload).ConfigureAwait(false));
    }

    if (options.Transport is Transport.Mqtt or Transport.Both)
    {
        if (options.MqttPort is null)
        {
            report.MqttNotReady("未提供 --mqtt-port；MQTT 路径未执行。");
        }
        else
        {
            try
            {
                report.AddMqttRows(await WriteMqttAsync(options, payloads).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                report.MqttNotReady($"MQTT 连接/发布失败：{exception.Message}");
            }
        }
    }

    var query = await QueryAnomaliesAsync(client, options.Database).ConfigureAwait(false);
    report.SetQuery(query.Raw, query.MatchedDevices);

    if (options.RunCopilot)
        await RunCopilotAsync(client, options, report).ConfigureAwait(false);
    else
        report.CopilotNotReady("未请求 Copilot；工业数据 journey 未虚构模型调用。");
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    report.DataFailure(exception.Message);
}

var finalReport = report.Build();
var reportPath = Path.Combine(options.OutputDirectory, "industrial-diagnostics-report.json");
await File.WriteAllTextAsync(
    reportPath,
    JsonSerializer.Serialize(finalReport, IndustrialJsonContext.Default.DiagnosticReport),
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
Console.WriteLine($"status={finalReport.Status}; data={finalReport.DataStatus}; transport={finalReport.TransportStatus}; copilot={finalReport.Copilot.Status}; report={Path.GetFullPath(reportPath)}");
Console.WriteLine($"anomalies={finalReport.Anomalies.Count}; citations={finalReport.Citations.Count}; rows_written={finalReport.RowsWritten}");
return finalReport.Status == "PASS" ? 0 : 3;

static IReadOnlyList<string> BuildPayloads()
{
    const long firstTimestamp = 1_700_000_000_000;
    var rows = new List<string>();
    foreach (var sample in new[]
    {
        new SensorSample("pump-01", "assembly", 68.2, 6.1, 2.2),
        new SensorSample("pump-02", "assembly", 71.5, 7.3, 2.8),
        new SensorSample("pump-03", "assembly", 96.4, 14.2, 8.7),
    })
    {
        rows.Add($"temperature,device={sample.Device},line={sample.Line} value={sample.Temperature.ToString(CultureInfo.InvariantCulture)} {firstTimestamp}");
        rows.Add($"current,device={sample.Device},line={sample.Line} value={sample.Current.ToString(CultureInfo.InvariantCulture)} {firstTimestamp}");
        rows.Add($"vibration,device={sample.Device},line={sample.Line} value={sample.Vibration.ToString(CultureInfo.InvariantCulture)} {firstTimestamp}");
    }

    return rows;
}

static async Task EnsureDatabaseAsync(HttpClient client, string database)
{
    using var response = await client.PostAsJsonAsync(
        "/v1/db",
        new CreateDatabaseRequest(database),
        IndustrialJsonContext.Default.CreateDatabaseRequest).ConfigureAwait(false);
    if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
        return;
    await EnsureSuccessAsync(response).ConfigureAwait(false);
}

static async Task EnsureSchemaAsync(HttpClient client, string database)
{
    foreach (var sql in new[]
    {
        "CREATE MEASUREMENT temperature (device TAG, line TAG, value FIELD FLOAT)",
        "CREATE MEASUREMENT current (device TAG, line TAG, value FIELD FLOAT)",
        "CREATE MEASUREMENT vibration (device TAG, line TAG, value FIELD FLOAT)",
    })
    {
        using var response = await client.PostAsJsonAsync(
            $"/v1/db/{Uri.EscapeDataString(database)}/sql",
            new SqlRequest(sql),
            IndustrialJsonContext.Default.SqlRequest).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException($"schema failed: {(int)response.StatusCode} {body}");
        }
    }
}

static async Task<long> WriteHttpAsync(HttpClient client, string database, string payload)
{
    using var content = new StringContent(payload, Encoding.UTF8, "text/plain");
    using var response = await client.PostAsync(
        $"/write?db={Uri.EscapeDataString(database)}&precision=ms", content).ConfigureAwait(false);
    await EnsureSuccessAsync(response).ConfigureAwait(false);
    return payload.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
}

static async Task<long> WriteMqttAsync(IndustrialOptions options, IReadOnlyList<string> payloads)
{
    var factory = new MqttClientFactory();
    using var mqtt = factory.CreateMqttClient();
    var connect = await mqtt.ConnectAsync(
        new MqttClientOptionsBuilder()
            .WithTcpServer(options.MqttHost, options.MqttPort!.Value)
            .WithClientId("sonnetdb-industrial-demo-" + Guid.NewGuid().ToString("N"))
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .Build()).ConfigureAwait(false);
    if (connect.ResultCode != MqttClientConnectResultCode.Success)
        throw new InvalidOperationException($"MQTT broker rejected connection: {connect.ResultCode}");

    long rows = 0;
    foreach (var payload in payloads)
    {
        var measurement = payload[..payload.IndexOf(',', StringComparison.Ordinal)];
        var result = await mqtt.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic($"db/{options.Database}/m/{measurement}")
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build()).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"MQTT publish failed for {measurement}: {result.ReasonString}");
        rows++;
    }

    await mqtt.DisconnectAsync().ConfigureAwait(false);
    return rows;
}

static async Task<QueryEvidence> QueryAnomaliesAsync(HttpClient client, string database)
{
    const string sql = "SELECT device, value FROM temperature WHERE value > 85";
    using var response = await client.PostAsJsonAsync(
        $"/v1/db/{Uri.EscapeDataString(database)}/sql",
        new SqlRequest(sql),
        IndustrialJsonContext.Default.SqlRequest).ConfigureAwait(false);
    await EnsureSuccessAsync(response).ConfigureAwait(false);
    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    if (lines.Length < 2)
        throw new InvalidDataException("SQL response did not contain meta/end rows.");

    using var meta = JsonDocument.Parse(lines[0]);
    var columns = meta.RootElement.GetProperty("columns").EnumerateArray()
        .Select(static item => item.GetString() ?? string.Empty)
        .ToArray();
    var deviceIndex = Array.FindIndex(columns, static column => string.Equals(column, "device", StringComparison.OrdinalIgnoreCase));
    if (deviceIndex < 0)
        throw new InvalidDataException("Anomaly query response is missing the device column.");

    var devices = new HashSet<string>(StringComparer.Ordinal);
    for (var index = 1; index < lines.Length - 1; index++)
    {
        using var row = JsonDocument.Parse(lines[index]);
        var values = row.RootElement;
        if (values.ValueKind == JsonValueKind.Array
            && deviceIndex < values.GetArrayLength()
            && values[deviceIndex].ValueKind == JsonValueKind.String)
        {
            devices.Add(values[deviceIndex].GetString() ?? string.Empty);
        }
    }

    return new QueryEvidence(body, devices.Order(StringComparer.Ordinal).ToArray());
}

static async Task RunCopilotAsync(HttpClient client, IndustrialOptions options, DiagnosticReportBuilder report)
{
    try
    {
        using var response = await client.PostAsJsonAsync(
            "/v1/copilot/chat",
            new CopilotChatRequest(
                options.Database,
                "分析 pump-03 的温度、电流和振动异常，给出维修建议并附引用。",
                Mode: "read-only"),
            IndustrialJsonContext.Default.CopilotChatRequest).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            report.CopilotNotReady($"provider 未就绪：HTTP {(int)response.StatusCode}；{ExtractError(body)}");
            return;
        }

        var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var toolCalls = 0;
        var hasFinal = false;
        foreach (var line in events)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var type = document.RootElement.TryGetProperty("type", out var typeValue)
                    ? typeValue.GetString()
                    : null;
                toolCalls += string.Equals(type, "tool_call", StringComparison.Ordinal) ? 1 : 0;
                hasFinal |= string.Equals(type, "final", StringComparison.Ordinal);
            }
            catch (JsonException)
            {
                report.CopilotNotReady("provider 返回了不可解析的 NDJSON 事件。");
                return;
            }
        }

        report.CopilotPass(toolCalls, hasFinal, model: options.CopilotModel);
    }
    catch (HttpRequestException exception)
    {
        report.CopilotNotReady($"provider 请求失败：{exception.Message}");
    }
}

static string ExtractError(string body)
{
    if (string.IsNullOrWhiteSpace(body))
        return "empty response";
    try
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("message", out var message) ? message.GetString() ?? body : body;
    }
    catch (JsonException)
    {
        return body.Length > 240 ? body[..240] : body;
    }
}

static async Task EnsureSuccessAsync(HttpResponseMessage response)
{
    if (response.IsSuccessStatusCode)
        return;
    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}", null, response.StatusCode);
}

internal enum Transport { Http, Mqtt, Both }

internal sealed record IndustrialOptions(
    string ServerUrl,
    string Token,
    string Database,
    Transport Transport,
    string MqttHost,
    int? MqttPort,
    string OutputDirectory,
    bool RunCopilot,
    string? CopilotModel)
{
    internal static IndustrialOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
                values[args[i][2..]] = args[i + 1];
        }

        string Value(string key, string environment, string fallback)
            => values.TryGetValue(key, out var value) ? value : Environment.GetEnvironmentVariable(environment) ?? fallback;

        var transportText = Value("transport", "SONNETDB_DIAGNOSTICS_TRANSPORT", "http");
        var transport = Enum.TryParse<Transport>(transportText, ignoreCase: true, out var parsed) ? parsed : Transport.Http;
        var portText = Value("mqtt-port", "SONNETDB_MQTT_PORT", string.Empty);
        var port = int.TryParse(portText, CultureInfo.InvariantCulture, out var parsedPort) && parsedPort is > 0 and <= 65535
            ? (int?)parsedPort
            : null;
        return new IndustrialOptions(
            Value("server", "SONNETDB_URL", "http://127.0.0.1:5080"),
            Value("token", "SONNETDB_TOKEN", string.Empty),
            Value("database", "SONNETDB_DATABASE", "industrial-demo"),
            transport,
            Value("mqtt-host", "SONNETDB_MQTT_HOST", "127.0.0.1"),
            port,
            Value("output", "SONNETDB_DIAGNOSTICS_OUTPUT", Path.Combine("artifacts", "m27-industrial-diagnostics")),
            values.ContainsKey("copilot") || string.Equals(Environment.GetEnvironmentVariable("SONNETDB_RUN_COPILOT"), "1", StringComparison.Ordinal),
            values.GetValueOrDefault("copilot-model") ?? Environment.GetEnvironmentVariable("SONNETDB_COPILOT_MODEL"));
    }

    internal static void PrintUsage() => Console.WriteLine(
        "dotnet run --project samples/SonnetDB.IndustrialDiagnostics -- --token <token> "
        + "[--transport http|mqtt|both] [--mqtt-port 1883] [--copilot] [--output <dir>]");
}

internal sealed record SensorSample(string Device, string Line, double Temperature, double Current, double Vibration);
internal sealed record QueryEvidence(string Raw, IReadOnlyList<string> MatchedDevices);
internal sealed record CreateDatabaseRequest(string Name);
internal sealed record SqlRequest(string Sql);
internal sealed record CopilotChatRequest(string? Db, string? Message, string? Mode = null);
internal sealed record DiagnosticAnomaly(string Device, string Reason, string Severity, IReadOnlyList<string> SuggestedChecks);
internal sealed record DiagnosticCitation(string Id, string Kind, string Source, string Snippet);
internal sealed record TokenUsage(bool Reported, long? InputTokens, long? OutputTokens, long? TotalTokens, decimal? CostUsd);
internal sealed record CopilotReport(string Status, string Provider, string? Model, int ToolCalls, string? FailureReason, TokenUsage Usage);
internal sealed record DiagnosticReport(
    string Schema,
    DateTimeOffset GeneratedAtUtc,
    string Status,
    string DataStatus,
    string TransportStatus,
    string? TransportFailureReason,
    string Transport,
    string Database,
    long RowsWritten,
    string Query,
    IReadOnlyList<string> QueryMatchedDevices,
    IReadOnlyList<DiagnosticAnomaly> Anomalies,
    IReadOnlyList<DiagnosticCitation> Citations,
    CopilotReport Copilot);

internal sealed class DiagnosticReportBuilder(IndustrialOptions options)
{
    private long _rowsWritten;
    private string _dataStatus = "PASS";
    private string? _dataFailure;
    private string _transportStatus = "PASS";
    private string? _transportFailure;
    private string _query = string.Empty;
    private IReadOnlyList<string> _matchedDevices = [];
    private string _copilotStatus = "NOT_READY";
    private string _copilotProvider = "not-configured";
    private string? _copilotModel;
    private string? _copilotFailure;
    private int _toolCalls;

    internal void AddHttpRows(long rows) => _rowsWritten += rows;
    internal void AddMqttRows(long rows) => _rowsWritten += rows;
    internal void SetQuery(string query, IReadOnlyList<string> matchedDevices)
    {
        _query = query;
        _matchedDevices = matchedDevices;
        if (!matchedDevices.Contains("pump-03", StringComparer.Ordinal))
            DataFailure("异常查询没有返回预期的 pump-03；请检查 schema、写入和时间戳。");
    }
    internal void MqttNotReady(string reason)
    {
        _transportStatus = "NOT_READY";
        _transportFailure = reason;
    }
    internal void DataFailure(string reason) { _dataStatus = "NOT_READY"; _dataFailure = reason; }
    internal void CopilotNotReady(string reason) { _copilotStatus = "NOT_READY"; _copilotFailure = reason; }
    internal void CopilotPass(int toolCalls, bool hasFinal, string? model)
    {
        _toolCalls = toolCalls;
        _copilotModel = model;
        _copilotProvider = "server-copilot-endpoint";
        if (!hasFinal) { CopilotNotReady("provider response had no final event"); return; }
        _copilotStatus = "PASS";
        _copilotFailure = null;
    }

    internal DiagnosticReport Build()
    {
        var anomalies = new[]
        {
            new DiagnosticAnomaly("pump-03", "temperature=96.4, current=14.2, vibration=8.7 exceed demo thresholds", "high", ["停机确认轴承温升", "检查泵入口滤网与联轴器", "维修后复采 15 分钟趋势"]),
        };
        var citations = new[]
        {
            new DiagnosticCitation("C1", "doc", "docs/industrial-ai-applications.md", "工业 Agent 应先查询 schema、指标和维护记录，再给出可审计建议。"),
            new DiagnosticCitation("C2", "tool", "SELECT device, value FROM temperature WHERE value > 85", "异常设备查询由 SonnetDB SQL 执行，报告不记录原始密钥。"),
            new DiagnosticCitation("C3", "source", "samples/SonnetDB.IndustrialDiagnostics/Program.cs", "演示数据明确将 pump-03 标记为温度、电流、振动联合异常。"),
        };
        var copilotFailure = _copilotFailure ?? _dataFailure;
        return new DiagnosticReport(
            "m27-industrial-diagnostics-v1",
            DateTimeOffset.UtcNow,
            _dataStatus == "PASS" && _transportStatus == "PASS" && (!options.RunCopilot || _copilotStatus == "PASS")
                ? "PASS"
                : "NOT_READY",
            _dataStatus,
            _transportStatus,
            _transportFailure,
            options.Transport.ToString().ToLowerInvariant(),
            options.Database,
            _rowsWritten,
            "SELECT device, value FROM temperature WHERE value > 85",
            _matchedDevices,
            anomalies,
            citations,
            new CopilotReport(_copilotStatus, _copilotProvider, _copilotModel, _toolCalls, copilotFailure, new TokenUsage(false, null, null, null, null)));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CreateDatabaseRequest))]
[JsonSerializable(typeof(SqlRequest))]
[JsonSerializable(typeof(CopilotChatRequest))]
[JsonSerializable(typeof(DiagnosticReport))]
internal sealed partial class IndustrialJsonContext : JsonSerializerContext;
