using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.SemanticContent;

namespace SonnetDB.Cli;

/// <summary>通过预计算向量 bundle 执行有界的本地 RAG 摄取与持久任务续跑。</summary>
internal sealed class RagCommandRunner(TextWriter output, Func<HttpMessageHandler>? handlerFactory = null)
{
    private const int MaxInputBytes = 16 * 1024 * 1024;
    private const int MaxVectors = 100_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal int Run(IReadOnlyList<string> args)
        => RunAsync(Parse(args)).GetAwaiter().GetResult();

    private async Task<int> RunAsync(Options options)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            CancellationToken token = deadline.Token;
            RagCliBundle bundle = await ReadBundleAsync(options.Input, token).ConfigureAwait(false);
            if (bundle.SchemaVersion != 1 || bundle.Profile is null || bundle.Vectors is null)
                throw new CliUsageException("RAG bundle 必须使用 schemaVersion=1，并提供 profile；vectors 必须为数组。");
            if (bundle.Profile.SupportedModalities is null || bundle.Profile.DataEgressPolicy is null
                || SemanticContentValidator.ValidateProfile(bundle.Profile).Count != 0
                || bundle.Profile.Dimensions > 32_768)
                throw new CliUsageException("RAG bundle profile 无效。");
            using var online = options.Endpoint is null ? null : new RagOnlineEmbeddingClient(
                bundle.Profile, options.Endpoint, options.ApiKeyEnvironment!, options.AllowEgress,
                options.AuditPath, options.DryRun, handlerFactory);
            if (online is not null && bundle.Vectors.Count != 0)
                throw new CliUsageException("在线 provider 模式不能混入预计算 vectors。");
            if (bundle.Vectors.Count > MaxVectors)
                throw new CliUsageException("RAG bundle 超过 100000 个预计算向量。");
            var vectors = new Dictionary<(string Id, string Hash), float[]>();
            foreach (RagCliVector item in bundle.Vectors)
            {
                token.ThrowIfCancellationRequested();
                if (item is null || string.IsNullOrWhiteSpace(item.ChunkId) || item.ChunkId.Length > 4096
                    || item.TextSha256 is null || item.TextSha256.Length != 64
                    || !item.TextSha256.All(char.IsAsciiHexDigit)
                    || item.Values is null || item.Values.Length != bundle.Profile.Dimensions
                    || item.Values.Any(static value => !float.IsFinite(value)))
                    throw new CliUsageException("预计算向量必须含稳定 chunkId、正文 SHA-256 和匹配 profile 的有限数值。");
                if (!vectors.TryAdd((item.ChunkId, item.TextSha256.ToUpperInvariant()), item.Values))
                    throw new CliUsageException("预计算向量包含重复的 chunkId/textSha256。");
                if (bundle.Profile.Normalization == EmbeddingNormalization.L2
                    && Math.Abs(item.Values.Sum(static value => (double)value * value) - 1) > 0.001)
                    throw new CliUsageException("预计算向量不满足 profile 的 L2 归一化合同。");
            }

            int chunkCount = 0;
            if (!options.Resume)
            {
                if (bundle.Snapshot is null || bundle.Snapshot.SchemaVersion != 1)
                    throw new CliUsageException("ingest 必须提供 schemaVersion=1 的完整 snapshot；空 manifests 表示删除当前内容。");
                RagIngestionPlan plan = RagIngestionPlanner.CreatePlan(null, bundle.Snapshot,
                    new RagIngestionPlanningOptions
                    {
                        MaxManifests = 10_000,
                        MaxTotalChunks = MaxVectors,
                        MaxTotalSegments = 1,
                        MaxTotalEmbeddings = 1,
                        MaxTotalTextCharacters = 4 * 1024 * 1024,
                    }, token);
                // 使用 planner 冻结后的清单，避免验证和执行使用不同对象。
                bundle = bundle with { Snapshot = new RagIngestionSnapshot(plan.Actions.Select(static action => action.Current!).ToArray()) };
                foreach (SemanticContentManifest manifest in bundle.Snapshot.Manifests)
                {
                    token.ThrowIfCancellationRequested();
                    if (manifest.SchemaVersion != 1 || manifest.Id.Length > 4096
                        || manifest.Modality is not (SemanticContentModality.Text or SemanticContentModality.Document)
                        || !bundle.Profile.Supports(manifest.Modality)
                        || manifest.Segments.Count != 0 || manifest.Embeddings.Count != 0
                        || manifest.EmbeddingProfileId is not null && manifest.EmbeddingProfileId != bundle.Profile.Id)
                        throw new CliUsageException("清单模态或 profile 与 bundle 不匹配。");
                    foreach (SemanticContentChunk chunk in manifest.Chunks)
                    {
                        token.ThrowIfCancellationRequested();
                        if (chunk.Id.Length > 4096)
                            throw new CliUsageException("chunk ID 超过 4096 字符。");
                        if (online is null) _ = LookupVector(chunk, token);
                        chunkCount++;
                    }
                }
            }

            if (options.DryRun)
            {
                output.WriteLine(JsonSerializer.Serialize(new RagCliReport(
                    "validated", options.Stream, null, null, 0, 0, 0, 0, 0, chunkCount, true),
                    RagCliJsonContext.Default.RagCliReport));
                return ExitCodes.Success;
            }

            token.ThrowIfCancellationRequested();
            using var database = Tsdb.Open(new TsdbOptions { RootDirectory = options.Path });
            var writer = new RagIngestionWriter(database, options.Stream, bundle.Profile,
                new RagIngestionWriterOptions
                {
                    MaxDuration = TimeSpan.FromSeconds(options.TimeoutSeconds),
                    MaxEmbeddingAttempts = online is null ? 1 : 3,
                });
            RagIngestionWriteResult? result = options.Resume
                ? await writer.ResumeAsync(ResolveVector, token).ConfigureAwait(false)
                : await writer.WriteAsync(bundle.Snapshot!, ResolveVector, token).ConfigureAwait(false);
            output.WriteLine(JsonSerializer.Serialize(new RagCliReport(
                result is null ? "no_pending_task" : "published", options.Stream,
                result?.Generation.Revision, result?.Generation.GenerationId,
                result?.AddedContents ?? 0, result?.UpdatedContents ?? 0, result?.DeletedContents ?? 0,
                result?.EmbeddedChunks ?? 0, result?.ReusedChunks ?? 0, chunkCount, false),
                RagCliJsonContext.Default.RagCliReport));
            return ExitCodes.Success;

            ValueTask<float[]> ResolveVector(SemanticContentChunk chunk, CancellationToken cancellationToken)
                => online is null ? ValueTask.FromResult(LookupVector(chunk, cancellationToken))
                    : online.EmbedAsync(chunk, cancellationToken);

            float[] LookupVector(SemanticContentChunk chunk, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string hash = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(chunk.Text)));
                if (!vectors.TryGetValue((chunk.Id, hash), out float[]? vector))
                    throw new InvalidDataException("缺少与冻结 chunk ID 及正文 SHA-256 匹配的预计算向量。");
                return vector;
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task<RagCliBundle> ReadBundleAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaxInputBytes)
            throw new CliUsageException("RAG bundle 文件必须非空且不超过 16 MiB。");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
            throw new CliUsageException("RAG bundle 在读取时发生变化。");
        token.ThrowIfCancellationRequested();
        return JsonSerializer.Deserialize(bytes, RagCliJsonContext.Default.RagCliBundle)
            ?? throw new CliUsageException("RAG bundle 不能为空。");
    }

    private static Options Parse(IReadOnlyList<string> args)
    {
        if (args.Count is < 2 or > 30 || args[1] is not ("ingest" or "resume"))
            throw new CliUsageException("用法：sndb rag ingest|resume --input bundle.json --path DB --stream NAME [--replace-snapshot] [--dry-run] [--timeout 120]");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool replace = false;
        bool dryRun = false;
        bool allowEgress = false;
        for (int index = 2; index < args.Count; index++)
        {
            string name = args[index];
            if (name == "--replace-snapshot" && !replace) { replace = true; continue; }
            if (name == "--dry-run" && !dryRun) { dryRun = true; continue; }
            if (name == "--allow-egress" && !allowEgress) { allowEgress = true; continue; }
            if (name is not ("--input" or "--path" or "--stream" or "--timeout" or "--endpoint" or "--api-key-env" or "--audit")
                || index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(name, args[++index]))
                throw new CliUsageException($"未知、重复或缺值的 RAG 参数：{name}。");
        }
        bool resume = args[1] == "resume";
        if (resume && (replace || dryRun))
            throw new CliUsageException("resume 只续跑持久任务，不能使用 --replace-snapshot 或 --dry-run。");
        if (!resume && !replace && !dryRun)
            throw new CliUsageException("ingest 使用完整快照，省略旧内容会删除；执行时必须显式传入 --replace-snapshot。");
        int timeout = 120;
        if (values.TryGetValue("--timeout", out string? raw)
            && (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out timeout) || timeout is < 1 or > 600))
            throw new CliUsageException("--timeout 必须为 1 到 600 秒。");
        string stream = Required("--stream");
        try
        {
            if (StrictUtf8.GetByteCount(stream) > 4096)
                throw new CliUsageException("--stream 的严格 UTF-8 编码不得超过 4096 字节。");
        }
        catch (EncoderFallbackException)
        {
            throw new CliUsageException("--stream 必须可无损编码为 UTF-8。");
        }
        values.TryGetValue("--endpoint", out string? endpoint);
        values.TryGetValue("--api-key-env", out string? keyEnvironment);
        values.TryGetValue("--audit", out string? auditPath);
        if (endpoint is null && (keyEnvironment is not null || auditPath is not null || allowEgress)
            || endpoint is not null && string.IsNullOrWhiteSpace(keyEnvironment))
            throw new CliUsageException("在线模式需要 --endpoint 和 --api-key-env；外发与审计选项仅用于在线模式。");
        return new(Required("--input"), Required("--path"), stream, resume, dryRun, timeout,
            endpoint, keyEnvironment, allowEgress, auditPath);

        string Required(string name) => values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new CliUsageException($"缺少 RAG 参数 {name}。");
    }

    private sealed record Options(string Input, string Path, string Stream, bool Resume, bool DryRun, int TimeoutSeconds,
        string? Endpoint, string? ApiKeyEnvironment, bool AllowEgress, string? AuditPath);
}
