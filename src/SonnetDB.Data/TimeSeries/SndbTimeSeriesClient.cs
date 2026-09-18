using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Remote;
using SonnetDB.Engine;
using SonnetDB.Ingest;
using SonnetDB.Model;
using SonnetDB.Protocol;
using SonnetDB.Storage.Format;

namespace SonnetDB.Data.TimeSeries;

/// <summary>
/// 时序类型化写入客户端，同时支持嵌入式与远程连接。
/// </summary>
public sealed class SndbTimeSeriesClient : IDisposable
{
    private readonly SndbConnectionStringBuilder _builder;
    private HttpClient? _http;
    private FrameChannel? _frames;
    private Tsdb? _embedded;
    private string _database = string.Empty;
    private bool _disposed;

    /// <summary>使用 SonnetDB 连接字符串创建客户端。</summary>
    public SndbTimeSeriesClient(string connectionString)
    {
        _builder = new SndbConnectionStringBuilder(connectionString);
        Open();
    }

    /// <summary>当前连接模式。</summary>
    public SndbProviderMode ProviderMode => _builder.ResolveMode();

    /// <summary>当前数据库名或嵌入式数据目录。</summary>
    public string Database => _database;

    /// <summary>创建一个独立的时序 point builder。</summary>
    public SndbTimeSeriesPointBuilder Point(string measurement)
        => SndbTimeSeriesPoint.Create(measurement);

    /// <summary>为指定 measurement 创建有界写入器。</summary>
    public SndbTimeSeriesWriter CreateWriter(
        string measurement,
        SndbTimeSeriesWriteOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(measurement);
        return new SndbTimeSeriesWriter(this, measurement, options ?? new SndbTimeSeriesWriteOptions());
    }

    /// <summary>一次性写入一批点；返回逐项结果。</summary>
    public async Task<SndbTimeSeriesWriteResult> WriteAsync(
        string measurement,
        IEnumerable<SndbTimeSeriesPoint> points,
        SndbTimeSeriesWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(measurement);
        ArgumentNullException.ThrowIfNull(points);
        await using SndbTimeSeriesWriter writer = CreateWriter(measurement, options);
        return await writer.WriteBatchAsync(points, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>释放网络或嵌入式资源。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http?.Dispose();
        _http = null;
        _frames = null;
        Tsdb? embedded = _embedded;
        _embedded = null;
        if (embedded is not null)
            SharedSndbRegistry.Release(embedded);
    }

    internal Tsdb? Embedded => _embedded;
    internal HttpClient Http => _http ?? throw new ObjectDisposedException(nameof(SndbTimeSeriesClient));
    internal FrameChannel? Frames => _frames;

    internal Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _embedded?.FlushNow();
        return Task.CompletedTask;
    }

    private void Open()
    {
        if (_builder.ResolveMode() == SndbProviderMode.Embedded)
        {
            string dataSource = _builder.ResolveEmbeddedDataSource();
            if (string.IsNullOrWhiteSpace(dataSource))
                throw new InvalidOperationException("时序客户端缺少 Data Source。");
            _database = dataSource;
            _embedded = SharedSndbRegistry.Acquire(_builder.CreateEmbeddedOptions(dataSource));
            return;
        }

        _database = _builder.ResolveDatabase();
        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException("远程时序客户端缺少数据库名。");
        _http = RemoteHttpClientFactory.Create(
            new Uri(_builder.ResolveBaseUrl(), UriKind.Absolute),
            _builder.Username,
            _builder.Password,
            _builder.Token,
            TimeSpan.FromSeconds(_builder.Timeout),
            allowAutoRedirect: false);
        _frames = new FrameChannel(_http, _builder.ResolveProtocol());
    }

    internal async Task<int> SendBatchAsync(
        string measurement,
        IReadOnlyList<Point> points,
        SndbTimeSeriesWriteOptions options,
        CancellationToken cancellationToken)
    {
        if (_embedded is not null)
        {
            int written = _embedded.WriteMany(points.ToArray());
            ApplyEmbeddedFlush(_embedded, options.FlushMode, written);
            return written;
        }

        FrameChannel? frames = _frames;
        if (frames is not null && frames.ShouldTryFrames())
        {
            IReadOnlyList<TsdbColumnarBlock> blocks = TsdbColumnarBlockBuilder.Build(points);
            var request = new ArrayBufferWriter<byte>();
            TsdbFrameCodec.EncodeWriteColumnarRequest(
                request,
                1,
                _database,
                measurement,
                options.FlushMode,
                blocks);
            IReadOnlyList<FrameMessage>? response = await frames.TrySendAsync(
                request.WrittenMemory,
                cancellationToken,
                allowFallback: false).ConfigureAwait(false);
            if (response is not null)
            {
                FrameMessage frame = response.Single();
                FrameChannel.ThrowIfError(frame.Header, frame.Payload);
                return TsdbFrameCodec.DecodeWriteColumnarResponse(frame.Payload);
            }
        }

        return await SendRestBatchAsync(measurement, points, options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> SendRestBatchAsync(
        string measurement,
        IReadOnlyList<Point> points,
        SndbTimeSeriesWriteOptions options,
        CancellationToken cancellationToken)
    {
        string payload = BuildLineProtocol(points);
        string url = $"v1/db/{Uri.EscapeDataString(_database)}/measurements/{Uri.EscapeDataString(measurement)}/lp";
        if (options.FlushMode != BulkFlushMode.None)
            url += "?flush=" + (options.FlushMode == BulkFlushMode.Sync ? "sync" : "async");

        using var content = new StringContent(payload, Encoding.UTF8, "text/plain");
        using HttpResponseMessage response = await Http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync(
            stream,
            RemoteJsonContext.Default.BulkIngestResponseBody,
            cancellationToken).ConfigureAwait(false);
        if (body is null || body.WrittenRows > int.MaxValue)
            throw new InvalidDataException("时序批量写响应缺少有效 writtenRows。");
        return (int)body.WrittenRows;
    }

    private static void ApplyEmbeddedFlush(Tsdb tsdb, BulkFlushMode flushMode, int written)
    {
        if (written == 0)
            return;
        switch (flushMode)
        {
            case BulkFlushMode.Sync:
                tsdb.FlushNow();
                break;
            case BulkFlushMode.Async:
                tsdb.SignalFlush();
                break;
        }
    }

    private static string BuildLineProtocol(IReadOnlyList<Point> points)
    {
        var builder = new StringBuilder(points.Count * 64);
        for (int i = 0; i < points.Count; i++)
        {
            Point point = points[i];
            builder.Append(EscapeIdentifier(point.Measurement));
            foreach (KeyValuePair<string, string> tag in point.Tags.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                builder.Append(',').Append(EscapeIdentifier(tag.Key)).Append('=').Append(EscapeIdentifier(tag.Value));
            }

            builder.Append(' ');
            bool first = true;
            foreach (KeyValuePair<string, FieldValue> field in point.Fields.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                if (!first)
                    builder.Append(',');
                first = false;
                builder.Append(EscapeIdentifier(field.Key)).Append('=').Append(FormatField(field.Value));
            }
            builder.Append(' ').Append(point.Timestamp.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return builder.ToString();
    }

    private static string FormatField(FieldValue value)
        => value.Type switch
        {
            FieldType.Float64 => value.AsDouble().ToString("G17", CultureInfo.InvariantCulture),
            FieldType.Int64 => value.AsLong().ToString(CultureInfo.InvariantCulture) + "i",
            FieldType.Boolean => value.AsBool() ? "true" : "false",
            FieldType.String => "\"" + value.AsString().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
            _ => throw new NotSupportedException($"REST Line Protocol 不支持字段类型 {value.Type}。"),
        };

    private static string EscapeIdentifier(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace(" ", "\\ ", StringComparison.Ordinal);

    private static async Task<SndbServerException> BuildHttpErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = string.Empty;
        try { body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); } catch { }
        string code = "http_error";
        string message = response.ReasonPhrase ?? response.StatusCode.ToString();
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                ServerErrorBody? error = JsonSerializer.Deserialize(
                    body,
                    RemoteJsonContext.Default.ServerErrorBody);
                if (error is not null)
                {
                    code = string.IsNullOrWhiteSpace(error.Error) ? code : error.Error;
                    message = string.IsNullOrWhiteSpace(error.Message) ? message : error.Message;
                }
            }
            catch (JsonException) { message = body; }
        }
        return new SndbServerException(code, message, response.StatusCode);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>带有界队列和 drain 语义的时序批量写入器。</summary>
public sealed class SndbTimeSeriesWriter : IDisposable, IAsyncDisposable
{
    private readonly SndbTimeSeriesClient _client;
    private readonly string _measurement;
    private readonly SndbTimeSeriesWriteOptions _options;
    private readonly Channel<WriteRequest> _queue;
    private readonly Task _pump;
    private int _completed;
    private int _disposed;

    internal SndbTimeSeriesWriter(SndbTimeSeriesClient client, string measurement, SndbTimeSeriesWriteOptions options)
    {
        _client = client;
        _measurement = measurement;
        _options = options;
        _options.Validate();
        _queue = Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(options.MaxPendingBatches)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>写入一个点；队列满时异步等待，形成有界背压。</summary>
    public async Task<SndbTimeSeriesWriteItemResult> WriteAsync(
        SndbTimeSeriesPoint point,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(point);
        SndbTimeSeriesWriteResult result = await EnqueueAsync([point], cancellationToken).ConfigureAwait(false);
        return result.Items[0];
    }

    /// <summary>写入一批点并返回逐项结果。</summary>
    public async Task<SndbTimeSeriesWriteResult> WriteBatchAsync(
        IEnumerable<SndbTimeSeriesPoint> points,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        var materialized = points.ToArray();
        if (materialized.Length == 0)
            return new SndbTimeSeriesWriteResult([]);
        return await EnqueueAsync(materialized, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>等待此前已入队的批次完成。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(WriteRequest.Flush(completion, cancellationToken), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _client.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>停止接收新批次并排空已接受的批次。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Interlocked.Exchange(ref _completed, 1);
        _queue.Writer.TryComplete();
        try { await _pump.ConfigureAwait(false); }
        catch { /* 每个请求已收到自身错误；drain 不重复抛出。 */ }
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task<SndbTimeSeriesWriteResult> EnqueueAsync(
        SndbTimeSeriesPoint[] points,
        CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        var completion = new TaskCompletionSource<SndbTimeSeriesWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(WriteRequest.Batch(points, completion, cancellationToken), cancellationToken).ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        await foreach (WriteRequest request in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (request.IsFlush)
            {
                if (request.CancellationToken.IsCancellationRequested)
                    request.FlushCompletion!.TrySetCanceled(request.CancellationToken);
                else
                    request.FlushCompletion!.TrySetResult(true);
                continue;
            }

            try
            {
                request.BatchCompletion!.TrySetResult(await ProcessAsync(request.Points!, request.CancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
            {
                request.BatchCompletion!.TrySetCanceled(request.CancellationToken);
                request.FlushCompletion?.TrySetCanceled(request.CancellationToken);
            }
            catch (Exception exception)
            {
                request.BatchCompletion!.TrySetResult(FailureForAll(request.Points!, ErrorCode(exception), exception.Message));
            }
        }
    }

    private async Task<SndbTimeSeriesWriteResult> ProcessAsync(
        SndbTimeSeriesPoint[] points,
        CancellationToken cancellationToken)
    {
        var results = new SndbTimeSeriesWriteItemResult[points.Length];
        var valid = new List<(int Index, Point Point)>(points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SndbTimeSeriesPoint? point = points[i];
            if (point is null)
            {
                results[i] = SndbTimeSeriesWriteItemResult.Failure(i, "null_point", "点不能为空。");
                continue;
            }
            try
            {
                if (!string.Equals(point.Measurement, _measurement, StringComparison.Ordinal))
                    throw new ArgumentException($"点 measurement '{point.Measurement}' 与 writer '{_measurement}' 不一致。");
                valid.Add((i, point.ToCorePoint(_options.Precision)));
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                results[i] = SndbTimeSeriesWriteItemResult.Failure(i, ErrorCode(exception), exception.Message);
            }
        }

        for (int offset = 0; offset < valid.Count; offset += _options.BatchSize)
        {
            int count = Math.Min(_options.BatchSize, valid.Count - offset);
            var chunk = valid.GetRange(offset, count);
            try
            {
                int written = await SendWithRetryAsync(
                    chunk.Select(static item => item.Point).ToArray(),
                    cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < count; i++)
                {
                    int index = chunk[i].Index;
                    results[index] = i < written
                        ? SndbTimeSeriesWriteItemResult.Success(index)
                        : SndbTimeSeriesWriteItemResult.Failure(index, "partial_write", "服务端仅确认了部分点。");
                }
            }
            catch (OperationCanceledException)
            {
                // 调用方取消必须停止当前批次，不能把取消伪装成逐项业务失败后继续发送后续块。
                throw;
            }
            catch (Exception exception)
            {
                foreach ((int index, _) in chunk)
                    results[index] = SndbTimeSeriesWriteItemResult.Failure(index, ErrorCode(exception), exception.Message);
            }
        }

        return new SndbTimeSeriesWriteResult(results);
    }

    private async Task<int> SendWithRetryAsync(
        IReadOnlyList<Point> points,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await _client.SendBatchAsync(_measurement, points, _options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryable(exception) && attempt < _options.MaxRetries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempt++;
                if (_options.RetryDelay > TimeSpan.Zero)
                    await Task.Delay(TimeSpan.FromTicks(_options.RetryDelay.Ticks * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryable(Exception exception)
        => exception is HttpRequestException
            || exception is TimeoutException
            || exception is TaskCanceledException;

    private void ThrowIfCompleted()
    {
        if (Volatile.Read(ref _completed) != 0)
            throw new ObjectDisposedException(nameof(SndbTimeSeriesWriter));
    }

    private static SndbTimeSeriesWriteResult FailureForAll(SndbTimeSeriesPoint[] points, string code, string message)
        => new(points.Select((_, index) => SndbTimeSeriesWriteItemResult.Failure(index, code, message)).ToArray());

    private static string ErrorCode(Exception exception)
        => exception switch
        {
            SndbServerException server => server.Error,
            NotSupportedException => "unsupported_field_type",
            ArgumentException => "invalid_point",
            OverflowException => "timestamp_overflow",
            HttpRequestException => "transport_error",
            TimeoutException => "timeout",
            _ => "write_failed",
        };

    private sealed class WriteRequest
    {
        private WriteRequest() { }
        public SndbTimeSeriesPoint[]? Points { get; private init; }
        public TaskCompletionSource<SndbTimeSeriesWriteResult>? BatchCompletion { get; private init; }
        public TaskCompletionSource<bool>? FlushCompletion { get; private init; }
        public CancellationToken CancellationToken { get; private init; }
        public bool IsFlush => FlushCompletion is not null;

        public static WriteRequest Batch(
            SndbTimeSeriesPoint[] points,
            TaskCompletionSource<SndbTimeSeriesWriteResult> completion,
            CancellationToken cancellationToken)
            => new() { Points = points, BatchCompletion = completion, CancellationToken = cancellationToken };

        public static WriteRequest Flush(TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
            => new() { FlushCompletion = completion, CancellationToken = cancellationToken };
    }
}
