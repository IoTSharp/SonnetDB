using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Microsoft.AspNetCore.Http;
using Snappier;
using SonnetDB.Auth;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Ingest;
using SonnetDB.Model;

namespace SonnetDB.Endpoints;

/// <summary>
/// Prometheus Remote Write v1 兼容入站端点：
/// <c>POST /api/v1/prom/write?db=&lt;name&gt;</c>。请求体为
/// <c>snappy(block-format) + protobuf(prometheus.WriteRequest)</c>。
/// 让 Prometheus / VictoriaMetrics agent / Grafana Alloy / OpenTelemetry Collector
/// 可以无需改 URL 直接把指标写入 SonnetDB。
/// </summary>
/// <remarks>
/// <para>映射规则：</para>
/// <list type="bullet">
/// <item>每个 <c>TimeSeries</c> 中的 <c>__name__</c> label 当作 <c>measurement</c>。</item>
/// <item>其余 label 当作 <c>tags</c>。</item>
/// <item>每条 <c>Sample</c> 转换为一个 <see cref="Point"/>，时间戳按 ms（Prometheus 协议本就是 ms），
/// 字段固定为 <c>value:double</c>。</item>
/// <item>NaN 样本（Prometheus 用作 stale marker）与名称含 SonnetDB 保留字符（<c>, = \n \r \t "</c>）的 series
/// 会被跳过；解码错误返回 <c>400</c>，否则返回 <c>204 No Content</c>（Prometheus 约定）。</item>
/// </list>
/// <para>Exemplars 与 native histograms 暂不解码，仅识别后跳过 wire 字段。</para>
/// </remarks>
internal static class PrometheusRemoteWriteEndpointHandler
{
    /// <summary>protobuf 单条消息上限（Prometheus agent 默认 ~1.5MB；这里 64MB 已远超实际负载）。</summary>
    private const int _maxMessageBytes = 64 * 1024 * 1024;

    /// <summary>
    /// 处理一次 Prometheus Remote Write 请求。
    /// </summary>
    /// <param name="ctx">HTTP 上下文。</param>
    /// <param name="registry">Tsdb 注册表。</param>
    /// <param name="grants">授权存储。</param>
    /// <param name="metrics">服务端度量。</param>
    public static async Task HandleAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        ServerMetrics metrics)
    {
        // 1) ?db=<name>
        var dbName = ctx.Request.Query["db"].ToString();
        if (string.IsNullOrWhiteSpace(dbName))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                "缺少查询参数 'db'。").ConfigureAwait(false);
            return;
        }
        if (!TsdbRegistry.IsValidName(dbName))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                $"非法数据库名 '{dbName}'。").ConfigureAwait(false);
            return;
        }
        if (!registry.TryGet(dbName, out var tsdb))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "db_not_found",
                $"数据库 '{dbName}' 不存在。").ConfigureAwait(false);
            return;
        }

        // 2) 写权限校验
        var perm = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, dbName);
        if (!DatabaseAccessEvaluator.HasPermission(perm, DatabasePermission.Write))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden",
                $"当前凭据对数据库 '{dbName}' 没有 write 权限。").ConfigureAwait(false);
            return;
        }

        // 3) 读取请求体（snappy 压缩字节）。
        byte[]? compressed = null;
        int compressedLen = 0;
        byte[]? decompressed = null;
        int decompressedLen = 0;
        try
        {
            (compressed, compressedLen) = await EndpointIngestUtils.ReadBodyAsync(ctx, decompressGzip: false).ConfigureAwait(false);
            if (compressedLen == 0)
            {
                // 空请求体：按 Prometheus 习惯返回 204（无 series 也算成功）。
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            // 4) snappy 块格式解压。
            int uncompressedLen;
            try
            {
                uncompressedLen = Snappy.GetUncompressedLength(
                    new ReadOnlySpan<byte>(compressed, 0, compressedLen));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "snappy_error",
                    "请求体不是合法的 Snappy 块格式。").ConfigureAwait(false);
                return;
            }
            if (uncompressedLen < 0 || uncompressedLen > _maxMessageBytes)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                    $"解压后 protobuf 消息超过 {_maxMessageBytes} 字节上限。").ConfigureAwait(false);
                return;
            }

            decompressed = ArrayPool<byte>.Shared.Rent(Math.Max(1, uncompressedLen));
            try
            {
                decompressedLen = Snappy.Decompress(
                    new ReadOnlySpan<byte>(compressed, 0, compressedLen),
                    decompressed.AsSpan(0, uncompressedLen));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "snappy_error",
                    $"Snappy 解压失败：{ex.Message}").ConfigureAwait(false);
                return;
            }

            // 5) protobuf 解码 + 透传到 BulkIngestor。
            BulkIngestResult result;
            try
            {
                using var reader = new PrometheusRemoteWriteReader(
                    new ReadOnlyMemory<byte>(decompressed, 0, decompressedLen));
                result = BulkIngestor.Ingest(tsdb, reader, BulkErrorPolicy.Skip, BulkFlushMode.None);
            }
            catch (PrometheusProtoException ex)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "protobuf_error",
                    $"protobuf 解码失败：{ex.Message}").ConfigureAwait(false);
                return;
            }

            metrics.AddInsertedRows(result.Written);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        finally
        {
            if (decompressed is not null)
                ArrayPool<byte>.Shared.Return(decompressed);
            if (compressed is not null)
                ArrayPool<byte>.Shared.Return(compressed);
        }
    }

    /// <summary>以 InfluxDB 风格 <c>{ "error", "message" }</c> JSON 写出错误响应（共享工具）。</summary>
    private static Task WriteErrorAsync(HttpContext ctx, int statusCode, string code, string message)
        => EndpointIngestUtils.WriteJsonErrorAsync(ctx, statusCode, code, message);
}

/// <summary>
/// protobuf 解码失败抛出的异常。
/// </summary>
internal sealed class PrometheusProtoException : Exception
{
    /// <summary>构造 protobuf 解码异常。</summary>
    public PrometheusProtoException(string message) : base(message) { }
}

/// <summary>
/// 把已解压的 Prometheus Remote Write protobuf 消息（<c>WriteRequest</c>）流式产出 <see cref="Point"/>。
/// 一个 <c>TimeSeries</c> 内的每条 <c>Sample</c> 都会被展开成一个独立的 <see cref="Point"/>。
/// NaN 样本与名称含保留字符的 series 会被静默跳过（让 BulkIngestor 在 Skip 策略下计入 skipped）。
/// </summary>
internal sealed class PrometheusRemoteWriteReader : IPointReader, IDisposable
{
    private readonly ReadOnlyMemory<byte> _payload;
    private int _cursor;

    // 当前 TimeSeries 的解码状态（按需推进）：
    private string? _currentMeasurement;
    private Dictionary<string, string>? _currentTags;
    private ReadOnlyMemory<byte> _samplesSlice; // 解码下一条 sample 时使用
    private int _samplesCursor;
    private bool _currentSeriesValid;

    /// <summary>构造一个 Prometheus Remote Write reader。</summary>
    /// <param name="writeRequestBytes">已解压的 protobuf 字节（<c>WriteRequest</c> 编码）。</param>
    public PrometheusRemoteWriteReader(ReadOnlyMemory<byte> writeRequestBytes)
    {
        _payload = writeRequestBytes;
    }

    /// <inheritdoc />
    public bool TryRead(out Point point)
    {
        while (true)
        {
            // 先尝试从当前 TimeSeries 取下一条 Sample。
            if (_currentSeriesValid && _samplesCursor < _samplesSlice.Length)
            {
                if (TryReadNextSampleFromCurrentSeries(out point))
                    return true;
                // 当前 series 的 samples 段读完或全被跳过：继续找下一个 TimeSeries。
                continue;
            }

            // 否则推进到下一个 TimeSeries。
            if (!TryAdvanceToNextTimeSeries())
            {
                point = null!;
                return false;
            }
        }
    }

    /// <summary>从已解析好 measurement / tags / samples-slice 的当前 series 取下一条 Sample。</summary>
    private bool TryReadNextSampleFromCurrentSeries(out Point point)
    {
        var span = _samplesSlice.Span;
        // 解析一条 Sample 消息：value(1, double, fixed64) + timestamp(2, varint int64)
        double value = 0d;
        long timestampMs = 0;
        bool hasValue = false;
        bool hasTs = false;
        try
        {
            int end = span.Length;
            // 我们一次只解码一条嵌入式 Sample 消息。samplesSlice 指向多条 Sample 的 length-prefixed 拼接。
            // 但更简单：调用方在 TryAdvanceToNextTimeSeries 时已经把 samplesSlice 指向"剩余的所有 samples 字段（含每个的 LEN 前缀）的拼接"，
            // 这里我们只解一条 Sample 的内容。samplesSlice 的格式是：repeated LEN-delimited Sample（去掉外层 tag, 但保留 length），
            // 实际上为简洁起见我们把 samplesSlice 设成 "一连串 (tag+len+payload) ... " 即原始 TimeSeries 内的 samples 字段切片。
            // 见 TryAdvanceToNextTimeSeries 注释。
            int local = _samplesCursor;
            // 寻找下一条 tag=0x12 的 Sample：跳过其它 tag（labels=0x0A 在此切片不应出现，但也安全跳过）。
            while (local < end)
            {
                var tag = ReadVarint(span, ref local);
                int fieldNo = (int)(tag >> 3);
                int wireType = (int)(tag & 0x7);
                if (fieldNo == 2 && wireType == 2) // samples (LEN)
                {
                    var msgLen = ReadVarint(span, ref local);
                    if (msgLen > int.MaxValue || local + (int)msgLen > end)
                        throw new PrometheusProtoException("Sample 消息长度越界。");
                    int sampleEnd = local + (int)msgLen;
                    // 解析 Sample 内部
                    while (local < sampleEnd)
                    {
                        var sTag = ReadVarint(span, ref local);
                        int sField = (int)(sTag >> 3);
                        int sWire = (int)(sTag & 0x7);
                        switch (sField, sWire)
                        {
                            case (1, 1): // value, fixed64
                                if (local + 8 > sampleEnd) throw new PrometheusProtoException("Sample.value 截断。");
                                value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(span.Slice(local, 8)));
                                local += 8;
                                hasValue = true;
                                break;
                            case (2, 0): // timestamp, varint
                                timestampMs = (long)ReadVarint(span, ref local);
                                hasTs = true;
                                break;
                            default:
                                SkipField(span, ref local, sWire);
                                break;
                        }
                    }
                    _samplesCursor = local;
                    bool isMissingRequiredField = !hasValue || !hasTs;
                    bool isStaleOrInvalidSample = double.IsNaN(value) || timestampMs < 0;
                    if (isMissingRequiredField || isStaleOrInvalidSample)
                    {
                        // 跳过：让 BulkIngestor 在 Skip 策略下计 skipped。抛出后由 BulkIngestor 吞掉。
                        throw new BulkIngestException("Prometheus: 跳过 NaN / 缺字段 / 负时间戳样本。");
                    }
                    // 名称非法的也跳过（safety net；TryAdvanceToNextTimeSeries 已经预检过 measurement / tag 名）
                    var fields = new Dictionary<string, FieldValue>(StringComparer.Ordinal)
                    {
                        ["value"] = FieldValue.FromDouble(value),
                    };
                    point = Point.Create(_currentMeasurement!, timestampMs, _currentTags, fields);
                    return true;
                }
                else
                {
                    SkipField(span, ref local, wireType);
                }
            }
            _samplesCursor = end;
            point = null!;
            return false;
        }
        catch (BulkIngestException)
        {
            throw;
        }
        catch (PrometheusProtoException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PrometheusProtoException($"解析 Sample 时出错：{ex.Message}");
        }
    }

    /// <summary>
    /// 推进到下一条合法的 TimeSeries，把 measurement / tags 解析出来，
    /// 并把 samplesSlice 设为该 TimeSeries 内"原样的 samples 字段切片"。
    /// 如果当前 series 缺少 <c>__name__</c> 或名称含保留字符，跳过整个 series（不抛错）。
    /// </summary>
    private bool TryAdvanceToNextTimeSeries()
    {
        _currentSeriesValid = false;
        _currentMeasurement = null;
        _currentTags = null;

        var span = _payload.Span;
        int end = span.Length;
        while (_cursor < end)
        {
            ulong tag;
            try { tag = ReadVarint(span, ref _cursor); }
            catch (PrometheusProtoException) { throw; }

            int fieldNo = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);
            if (fieldNo == 1 && wireType == 2) // WriteRequest.timeseries
            {
                ulong tsLen = ReadVarint(span, ref _cursor);
                if (tsLen > int.MaxValue || _cursor + (int)tsLen > end)
                    throw new PrometheusProtoException("TimeSeries 消息长度越界。");
                int tsEnd = _cursor + (int)tsLen;

                // 在该 TimeSeries 内：扫描 labels（field=1）解析出 measurement+tags；
                // samplesSlice 我们存"该 TimeSeries 切片自身"，让 TryReadNextSampleFromCurrentSeries 自己 skip 非 samples 字段。
                bool ok = TryParseLabels(span, _cursor, tsEnd, out _currentMeasurement, out _currentTags);
                _samplesSlice = _payload.Slice(_cursor, tsEnd - _cursor);
                _samplesCursor = 0;
                _cursor = tsEnd;
                if (ok)
                {
                    _currentSeriesValid = true;
                    return true;
                }
                // 跳过该 series（措辞：跳过整段，不抛错）
                continue;
            }
            else
            {
                SkipField(span, ref _cursor, wireType);
            }
        }
        return false;
    }

    /// <summary>仅扫描指定 TimeSeries 切片中的 labels，提取 <c>__name__</c> 与其余 tags。</summary>
    private static bool TryParseLabels(
        ReadOnlySpan<byte> span,
        int start,
        int end,
        out string? measurement,
        out Dictionary<string, string>? tags)
    {
        measurement = null;
        tags = null;
        int local = start;
        while (local < end)
        {
            ulong tag = ReadVarint(span, ref local);
            int fieldNo = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);
            if (fieldNo == 1 && wireType == 2) // labels
            {
                ulong labLen = ReadVarint(span, ref local);
                if (labLen > int.MaxValue || local + (int)labLen > end)
                    throw new PrometheusProtoException("Label 消息长度越界。");
                int labEnd = local + (int)labLen;
                string? name = null;
                string? value = null;
                while (local < labEnd)
                {
                    ulong lTag = ReadVarint(span, ref local);
                    int lField = (int)(lTag >> 3);
                    int lWire = (int)(lTag & 0x7);
                    if (lField == 1 && lWire == 2) // name
                    {
                        ulong nLen = ReadVarint(span, ref local);
                        if (nLen > int.MaxValue || local + (int)nLen > labEnd)
                            throw new PrometheusProtoException("Label.name 截断。");
                        name = Encoding.UTF8.GetString(span.Slice(local, (int)nLen));
                        local += (int)nLen;
                    }
                    else if (lField == 2 && lWire == 2) // value
                    {
                        ulong vLen = ReadVarint(span, ref local);
                        if (vLen > int.MaxValue || local + (int)vLen > labEnd)
                            throw new PrometheusProtoException("Label.value 截断。");
                        value = Encoding.UTF8.GetString(span.Slice(local, (int)vLen));
                        local += (int)vLen;
                    }
                    else
                    {
                        SkipField(span, ref local, lWire);
                    }
                }
                if (string.IsNullOrEmpty(name) || value is null)
                    continue;

                if (name == "__name__")
                {
                    if (!IsValidName(value) || value.Length > 255)
                        return false;
                    measurement = value;
                }
                else
                {
                    if (!IsValidName(name) || !IsValidName(value))
                        return false;
                    tags ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    tags[name] = value;
                }
            }
            else
            {
                SkipField(span, ref local, wireType);
            }
        }
        return measurement is not null;
    }

    /// <summary>SonnetDB 名称合法性预检（与 <c>PointValidation</c> 一致：禁 <c>, = \n \r \t "</c> 与空白）。</summary>
    private static bool IsValidName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == ',' || c == '=' || c == '\n' || c == '\r' || c == '\t' || c == '"')
                return false;
        }
        return true;
    }

    /// <summary>读取 protobuf base-128 varint，最多 10 字节。</summary>
    private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int cursor)
    {
        ulong value = 0;
        int shift = 0;
        for (int i = 0; i < 10; i++)
        {
            if (cursor >= span.Length)
                throw new PrometheusProtoException("varint 读取越界。");
            byte b = span[cursor++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
        }
        throw new PrometheusProtoException("varint 长度超过 10 字节。");
    }

    /// <summary>按 wire type 跳过未识别字段。</summary>
    private static void SkipField(ReadOnlySpan<byte> span, ref int cursor, int wireType)
    {
        switch (wireType)
        {
            case 0: // VARINT
                _ = ReadVarint(span, ref cursor);
                break;
            case 1: // I64
                if (cursor + 8 > span.Length) throw new PrometheusProtoException("I64 跳过越界。");
                cursor += 8;
                break;
            case 2: // LEN
                ulong len = ReadVarint(span, ref cursor);
                if (len > int.MaxValue || cursor + (int)len > span.Length)
                    throw new PrometheusProtoException("LEN 跳过越界。");
                cursor += (int)len;
                break;
            case 5: // I32
                if (cursor + 4 > span.Length) throw new PrometheusProtoException("I32 跳过越界。");
                cursor += 4;
                break;
            default:
                throw new PrometheusProtoException($"不支持的 wire type {wireType}。");
        }
    }

    /// <summary>无外部资源；保留 Dispose 便于 using 风格调用。</summary>
    public void Dispose()
    {
        // intentionally empty — no native handles to release.
    }
}
