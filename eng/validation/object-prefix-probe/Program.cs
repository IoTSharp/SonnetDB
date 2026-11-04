using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Engine;
using SonnetDB.ObjectStorage;

// 只运行在独占元数据副本；列表会写派生索引和审计，绝不能指向生产目录或只读挂载的原库。
if (args.Length != 3 || !Path.GetFileName(Path.TrimEndingDirectorySeparator(args[0])).EndsWith("-db", StringComparison.Ordinal)
    || !File.Exists(Path.Combine(args[0], "object-prefix-probe-owned.txt")))
    throw new ArgumentException("需要带 object-prefix-probe-owned.txt 标记的独占 *-db 副本、桶名和前缀。");

using var deadline = new CancellationTokenSource();
var report = new ProbeReport(DateTimeOffset.UtcNow, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(Tsdb).Assembly.Location))));
long started = Stopwatch.GetTimestamp();
Tsdb? database = null;
try
{
    Console.Error.WriteLine("phase=open_begin utc=" + DateTimeOffset.UtcNow.ToString("O"));
    var db = database = Tsdb.Open(new TsdbOptions { RootDirectory = Path.GetFullPath(args[0]) });
    report.MetadataSequenceAtOpen = db.Keyspaces.Open("__object_storage").LastSequence;
    report.OpenMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    Console.Error.WriteLine("phase=metadata_opened elapsed_ms=" + report.OpenMilliseconds);
    // 冷打开受外部进程期限约束；既定八秒列表窗口在打开完成后开始，不把冷恢复混入请求耗时。
    deadline.CancelAfter(TimeSpan.FromSeconds(90));
    var store = new SndbObjectStore(db);
    // 测试钩子只计数，不改变被测路径；它不构成 NativeAOT 反射合同。
    typeof(SndbObjectStore).GetProperty("ListRebuildEntryTestHook", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(store, (Action)(() => report.BackfillCandidates++));
    typeof(SndbObjectStore).GetProperty("ListCandidateTestHook", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(store, (Action)(() => report.PageCandidates++));
    string? continuation = null;
    for (int pageNumber = 0; pageNumber < 3; pageNumber++)
    {
        deadline.Token.ThrowIfCancellationRequested();
        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        requestDeadline.CancelAfter(TimeSpan.FromSeconds(8));
        long pageStarted = Stopwatch.GetTimestamp();
        SndbObjectListResult page;
        try
        {
            page = store.ListObjects(args[1], args[2], 3, continuation, null, requestDeadline.Token);
        }
        finally
        {
            report.LastRequestMilliseconds = Stopwatch.GetElapsedTime(pageStarted).TotalMilliseconds;
            Console.Error.WriteLine("phase=page_attempted page=" + pageNumber + " elapsed_ms=" + report.LastRequestMilliseconds);
        }
        if (report.LastRequestMilliseconds > 8_000)
            throw new TimeoutException("列表请求超过既定 8 秒观察预算。");
        report.Pages.Add(new PageEvidence(Stopwatch.GetElapsedTime(pageStarted).TotalMilliseconds, page.IsTruncated,
            continuation, page.NextContinuationToken, page.Objects.Select(x => new ObjectIdentity(x.Key, x.VersionId)).ToArray()));
        if (!page.IsTruncated)
            break;
        if (page.NextContinuationToken is null || page.NextContinuationToken == continuation)
            throw new InvalidDataException("分页游标未推进。");
        continuation = page.NextContinuationToken;
    }

    // 独立 oracle 从整个权威 latest 按物理页读取，只保留最小 128 个原始键，不复用产品的前缀定位算法。
    // 这是隔离副本上的验证扫描，不计入被测列表耗时，也不回写生产数据库。
    var metadata = db.Keyspaces.Open("__object_storage");
    string latestPrefix = "latest:" + args[1] + "/";
    var smallest = new SortedDictionary<string, string>(StringComparer.Ordinal);
    string? after = null;
    bool complete = false;
    long oracleStarted = Stopwatch.GetTimestamp();
    for (int pageNumber = 0; pageNumber < 16384; pageNumber++)
    {
        deadline.Token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(oracleStarted) > TimeSpan.FromSeconds(45))
            throw new TimeoutException("Oracle 扫描达到 45 秒预算。");
        var entries = metadata.ScanPrefixAfter(latestPrefix, after, 256);
        foreach (var entry in entries)
        {
            string encoded = Encoding.UTF8.GetString(entry.Key.Span)[latestPrefix.Length..];
            string base64 = encoded.Replace('-', '+').Replace('_', '/');
            string key = Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
            report.OracleCandidates++;
            if (!key.StartsWith(args[2], StringComparison.Ordinal))
                continue;
            smallest[key] = Encoding.UTF8.GetString(entry.Value.Span);
            if (smallest.Count > 128)
                smallest.Remove(smallest.Last().Key);
        }
        if (entries.Count < 256) { complete = true; break; }
        after = Encoding.UTF8.GetString(entries[^1].Key.Span);
    }
    if (!complete)
        throw new InvalidDataException("Oracle 达到 4194304 个 latest 的候选预算，未取得完整验证。");
    var expected = new List<ObjectIdentity>();
    foreach (var pair in smallest)
    {
        deadline.Token.ThrowIfCancellationRequested();
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        byte[] bytes = metadata.Get("object:" + args[1] + "/" + encoded + "/" + pair.Value)
            ?? throw new InvalidDataException("Oracle 的 latest 引用了缺失版本。");
        using var json = JsonDocument.Parse(bytes);
        if (!json.RootElement.GetProperty("isDeleteMarker").GetBoolean())
            expected.Add(new ObjectIdentity(pair.Key, pair.Value));
    }
    ObjectIdentity[] actual = report.Pages.SelectMany(x => x.Objects).ToArray();
    if (actual.Length == 0 || expected.Count < actual.Length || !actual.SequenceEqual(expected.Take(actual.Length))
        || actual.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != actual.Length)
        throw new InvalidDataException("分页结果与独立 latest/版本 oracle 不一致或候选不足。");
    report.Status = "PASS";
}
catch (Exception exception)
{
    report.Status = exception is OperationCanceledException or TimeoutException ? "FAIL_TIMEOUT" : "FAIL";
    report.Error = exception.GetType().Name + ": " + exception.Message;
}
finally
{
    report.ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    Console.WriteLine(JsonSerializer.Serialize(report, ProbeJsonContext.Default.ProbeReport));
    Console.Error.WriteLine("phase=dispose_begin");
    database?.Dispose();
    Console.Error.WriteLine("phase=dispose_end");
}
return report.Status == "PASS" ? 0 : 1;

/// <summary>保存实际执行身份、各页耗时和独立对拍边界。</summary>
internal sealed record ProbeReport(DateTimeOffset ObservedAt, string Architecture, string CoreSha256)
{
    public string Status { get; set; } = "NOT_RUN";
    public string? Error { get; set; }
    public double OpenMilliseconds { get; set; }
    public double LastRequestMilliseconds { get; set; }
    public long MetadataSequenceAtOpen { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public long BackfillCandidates { get; set; }
    public long PageCandidates { get; set; }
    public long OracleCandidates { get; set; }
    public List<PageEvidence> Pages { get; } = [];
}

/// <summary>保存单页的请求游标、后续游标和实际对象版本。</summary>
internal sealed record PageEvidence(double Milliseconds, bool IsTruncated, string? ContinuationToken, string? NextContinuationToken, ObjectIdentity[] Objects);

/// <summary>仅输出对拍必需的对象身份，不输出业务元数据或凭据。</summary>
internal sealed record ObjectIdentity(string Key, string VersionId);

/// <summary>验证工具的固定 JSON 类型注册，不启用运行时反射序列化。</summary>
[JsonSerializable(typeof(ProbeReport))]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;
