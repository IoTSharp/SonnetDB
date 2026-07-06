using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Internal;
using SonnetDB.Ingest;
using SonnetDB.Protocol;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Data.Remote;

/// <summary>
/// 远程连接实现：通过 HTTP 调用 <c>SonnetDB</c>。
/// </summary>
internal sealed class RemoteConnectionImpl : IConnectionImpl
{
    private readonly SndbConnectionStringBuilder _builder;
    private HttpClient? _http;
    private FrameChannel? _frames;
    private string _baseUrl = string.Empty;
    private string _database = string.Empty;
    private ConnectionState _state = ConnectionState.Closed;

    public RemoteConnectionImpl(SndbConnectionStringBuilder builder)
    {
        _builder = builder;
    }

    public string DataSource => _baseUrl;

    public string Database => _database;

    public string ServerVersion => _http is null ? "unknown" : "SonnetDB";

    public ConnectionState State => _state;

    public void Open()
    {
        if (_state == ConnectionState.Open) return;

        var (baseUrl, dbFromUrl) = ParseEndpoint(_builder.DataSource);
        _baseUrl = baseUrl;
        _database = !string.IsNullOrWhiteSpace(_builder.Database) ? _builder.Database! : dbFromUrl;

        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException(
                "远程连接缺少数据库名：请在 Data Source URL 路径中提供（如 sonnetdb+http://host/db），或显式设置 'Database='。");

        _http = RemoteHttpClientFactory.Create(
            new Uri(baseUrl, UriKind.Absolute),
            _builder.Token,
            TimeSpan.FromSeconds(_builder.Timeout));
        _frames = new FrameChannel(_http, _builder.ResolveProtocol());

        _state = ConnectionState.Open;
    }

    public ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open();
        return ValueTask.CompletedTask;
    }

    public void Close()
    {
        if (_state == ConnectionState.Closed) return;
        _state = ConnectionState.Closed;
        _http?.Dispose();
        _http = null;
        _frames = null;
    }

    public void Dispose() => Close();

    public IExecutionResult Execute(string sql, SndbParameterCollection parameters, CommandBehavior behavior, object? transactionState)
    {
        if (_http is null || _state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");
        var transaction = GetTransactionState(transactionState);

        // #213：远程线协议仅接受 SQL 字符串（无结构化参数字段），故命名参数仍在客户端做字面量替换后再发送，
        // 保留 byte[]/DateTime/GeoPoint 等类型的既有序列化保真度；SndbCommand 不再预替换，此处补上。
        sql = ParameterBinder.Bind(sql, parameters);

        // 客户端拦截 SQL Console 风格元命令：USE <db> 切换当前库；SELECT current_database() / SHOW CURRENT_DATABASE 返回当前库。
        // 这两类命令不会发往服务端，避免命中 SqlParser 的"未知关键字"错误。
        var meta = SqlMetaCommand.TryParse(sql, out var requestedDb);
        if (meta == MetaKind.CurrentDatabase)
        {
            return MaterializedExecutionResult.FromSelect(SqlMetaCommand.BuildCurrentDatabaseResult(_database));
        }
        if (meta == MetaKind.UseDatabase)
        {
            // 远程模式下 USE 直接修改当前 Database；后续 Execute 会走 /v1/db/<新库>/sql。
            // 不做服务端校验：若库不存在或当前用户无权限，下一条业务 SQL 会自然返回 404 / 403。
            if (transaction is not null)
                throw new InvalidOperationException("远程轻事务中不支持 USE 切换数据库。");
            _database = requestedDb;
            return MaterializedExecutionResult.FromSelect(SqlMetaCommand.BuildUseDatabaseResult(requestedDb));
        }

        if (transaction is not null)
        {
            transaction.Add(sql);
            return MaterializedExecutionResult.NonQuery(0);
        }

        return ExecuteSqlRequest(sql, CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task<IExecutionResult> ExecuteAsync(
        string sql,
        SndbParameterCollection parameters,
        CommandBehavior behavior,
        object? transactionState,
        CancellationToken cancellationToken)
    {
        if (_http is null || _state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = GetTransactionState(transactionState);

        // #213：远程仍在客户端做参数字面量替换（线协议只接受 SQL 字符串），SndbCommand 不再预替换。
        sql = ParameterBinder.Bind(sql, parameters);

        var meta = SqlMetaCommand.TryParse(sql, out var requestedDb);
        if (meta == MetaKind.CurrentDatabase)
        {
            return Task.FromResult<IExecutionResult>(
                MaterializedExecutionResult.FromSelect(SqlMetaCommand.BuildCurrentDatabaseResult(_database)));
        }
        if (meta == MetaKind.UseDatabase)
        {
            if (transaction is not null)
                throw new InvalidOperationException("远程轻事务中不支持 USE 切换数据库。");
            _database = requestedDb;
            return Task.FromResult<IExecutionResult>(
                MaterializedExecutionResult.FromSelect(SqlMetaCommand.BuildUseDatabaseResult(requestedDb)));
        }

        if (transaction is not null)
        {
            transaction.Add(sql);
            return Task.FromResult<IExecutionResult>(MaterializedExecutionResult.NonQuery(0));
        }

        return ExecuteSqlRequest(sql, cancellationToken);
    }

    private async Task<IExecutionResult> ExecuteSqlRequest(string sql, CancellationToken cancellationToken)
    {
        if (_http is null || _state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");

        // #241：只读语句优先走二进制帧（sql service）；写/控制面/解析失败回落 REST NDJSON。
        // 命名参数已由 ParameterBinder.Bind 内联进 sql 字面量，故帧编码 parameters:null。
        if (_frames is { } fx && fx.ShouldTryFrames() && IsFrameEligibleReadOnly(sql))
        {
            var w = new ArrayBufferWriter<byte>();
            SqlFrameCodec.EncodeQueryRequest(w, 1, _database, sql, null);
            var frames = await fx.TrySendAsync(w.WrittenMemory, cancellationToken).ConfigureAwait(false);
            if (frames is not null)
                return BuildSqlResult(frames);
        }

        var url = $"v1/db/{Uri.EscapeDataString(_database)}/sql";
        var body = new SqlRequestBody { Sql = sql };
        var json = JsonSerializer.Serialize(body, RemoteJsonContext.Default.SqlRequestBody);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!response.IsSuccessStatusCode)
                throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return RemoteExecutionResult.Create(response, stream);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 判别 SQL 是否为可走帧的只读数据面语句（SELECT / SHOW-数据面 / DESCRIBE / EXPLAIN）。
    /// 解析失败或写/控制面/ShowDatabases → false（回落 REST）。与服务端 sql 帧门禁一致。
    /// </summary>
    private static bool IsFrameEligibleReadOnly(string sql)
    {
        try
        {
            SqlStatement statement = SqlParser.Parse(sql);
            return statement is
                SelectStatement or
                ShowMeasurementsStatement or
                ShowTablesStatement or
                ShowTableIndexesStatement or
                ShowDocumentCollectionsStatement or
                ShowDocumentIndexesStatement or
                ShowFullTextIndexesStatement or
                DescribeMeasurementStatement or
                DescribeTableStatement or
                DescribeDocumentCollectionStatement or
                ExplainStatement;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把 sql service 的响应帧序列（meta → rows × N → end）物化为 <see cref="IExecutionResult"/>。
    /// end 前收到错误帧则中止查询并上抛。
    /// </summary>
    private static IExecutionResult BuildSqlResult(IReadOnlyList<FrameMessage> frames)
    {
        string[] columns = [];
        var rows = new List<IReadOnlyList<object?>>();

        foreach (FrameMessage frame in frames)
        {
            FrameChannel.ThrowIfError(frame.Header, frame.Payload);
            switch (SqlFrameCodec.PeekChunkKind(frame.Payload))
            {
                case SqlQueryChunkKind.Meta:
                    columns = SqlFrameCodec.DecodeQueryMetaFrame(frame.Payload);
                    break;
                case SqlQueryChunkKind.Rows:
                    foreach (object?[] row in SqlFrameCodec.DecodeQueryRowsFrame(frame.Payload))
                        rows.Add(row);
                    break;
                case SqlQueryChunkKind.End:
                    _ = SqlFrameCodec.DecodeQueryEndFrame(frame.Payload);
                    break;
            }
        }

        return MaterializedExecutionResult.FromSelect(new SelectExecutionResult(columns, rows));
    }

    private static SndbServerException BuildHttpError(HttpResponseMessage response)
        => BuildHttpErrorAsync(response, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<SndbServerException> BuildHttpErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { /* ignore */ }

        string error = "http_error";
        string message = response.ReasonPhrase ?? response.StatusCode.ToString();
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                var err = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.ServerErrorBody);
                if (err is not null && !string.IsNullOrEmpty(err.Error))
                {
                    error = err.Error;
                    message = err.Message;
                }
            }
            catch { /* 非 JSON 响应保留 raw body */ message = body; }
        }
        return new SndbServerException(error, message, response.StatusCode);
    }

    public IExecutionResult ExecuteBulk(string commandText, SndbParameterCollection parameters, object? transactionState)
    {
        if (_http is null || _state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");
        if (transactionState is not null)
            throw new NotSupportedException("远程轻事务当前不支持批量入库快路径。");
        ArgumentNullException.ThrowIfNull(commandText);

        // 1) 嗅探协议格式 + 切首行 measurement 前缀
        var format = BulkPayloadDetector.DetectWithPrefix(commandText, out var measurementFromPrefix, out var payload);

        // 2) measurement 优先级：参数 > 首行前缀 > 从 payload 内提取（JSON `m` / BulkValues `INSERT INTO <name>`）
        var measurement = TryGetParam(parameters, "measurement") ?? measurementFromPrefix;
        if (string.IsNullOrWhiteSpace(measurement))
        {
            measurement = format switch
            {
                BulkPayloadFormat.Json => TryPeekJsonMeasurement(payload.Span),
                BulkPayloadFormat.BulkValues => SafePeekBulkMeasurement(payload),
                _ => null,
            };
        }
        if (string.IsNullOrWhiteSpace(measurement))
            throw new InvalidOperationException(
                "远程批量入库必须指定 measurement：可在 payload 首行作为前缀（如 `cpu\\n...`）、" +
                "通过 cmd.Parameters[\"measurement\"] 提供，或在 JSON 中给出 `m` 字段、在 INSERT 中给出 `INTO <name>`。");

        // 3) 端点后缀
        string suffix = format switch
        {
            BulkPayloadFormat.LineProtocol => "lp",
            BulkPayloadFormat.Json => "json",
            BulkPayloadFormat.BulkValues => "bulk",
            _ => throw new InvalidOperationException($"未知协议格式 {format}。"),
        };

        // 4) query string：onerror / flush
        var url = new StringBuilder();
        url.Append("v1/db/").Append(Uri.EscapeDataString(_database))
           .Append("/measurements/").Append(Uri.EscapeDataString(measurement))
           .Append('/').Append(suffix);
        var qs = new List<string>();
        var onerror = TryGetParam(parameters, "onerror");
        if (!string.IsNullOrEmpty(onerror))
            qs.Add("onerror=" + Uri.EscapeDataString(onerror));
        var flush = TryGetParam(parameters, "flush");
        if (!string.IsNullOrEmpty(flush))
            qs.Add("flush=" + Uri.EscapeDataString(flush));
        if (qs.Count > 0)
            url.Append('?').Append(string.Join('&', qs));

        // 5) 构造请求体（payload 已通过 DetectWithPrefix 切掉首行）
        string contentType = format == BulkPayloadFormat.Json
            ? "application/json"
            : "text/plain";
        using var request = new HttpRequestMessage(HttpMethod.Post, url.ToString())
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, contentType),
        };

        using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead);
        if (!response.IsSuccessStatusCode)
            throw BuildHttpError(response);

        // 6) 解析响应 JSON
        var stream = response.Content.ReadAsStream();
        var body = JsonSerializer.Deserialize(stream, RemoteJsonContext.Default.BulkIngestResponseBody)
            ?? throw new SndbServerException("bulk_ingest_error", "服务端响应体为空。", response.StatusCode);
        return MaterializedExecutionResult.NonQuery((int)body.WrittenRows);
    }

    public Task<IExecutionResult> ExecuteBulkAsync(
        string commandText,
        SndbParameterCollection parameters,
        object? transactionState,
        CancellationToken cancellationToken)
    {
        if (_http is null || _state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");
        if (transactionState is not null)
            throw new NotSupportedException("远程轻事务当前不支持批量入库快路径。");
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteBulkRequestAsync(commandText, parameters, cancellationToken);
    }

    private async Task<IExecutionResult> ExecuteBulkRequestAsync(
        string commandText,
        SndbParameterCollection parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandText);

        var format = BulkPayloadDetector.DetectWithPrefix(commandText, out var measurementFromPrefix, out var payload);
        var measurement = TryGetParam(parameters, "measurement") ?? measurementFromPrefix;
        if (string.IsNullOrWhiteSpace(measurement))
        {
            measurement = format switch
            {
                BulkPayloadFormat.Json => TryPeekJsonMeasurement(payload.Span),
                BulkPayloadFormat.BulkValues => SafePeekBulkMeasurement(payload),
                _ => null,
            };
        }
        if (string.IsNullOrWhiteSpace(measurement))
            throw new InvalidOperationException(
                "远程批量入库必须指定 measurement：可在 payload 首行作为前缀（如 `cpu\\n...`）、" +
                "通过 cmd.Parameters[\"measurement\"] 提供，或在 JSON 中给出 `m` 字段、在 INSERT 中给出 `INTO <name>`。");

        string suffix = format switch
        {
            BulkPayloadFormat.LineProtocol => "lp",
            BulkPayloadFormat.Json => "json",
            BulkPayloadFormat.BulkValues => "bulk",
            _ => throw new InvalidOperationException($"未知协议格式 {format}。"),
        };

        var url = new StringBuilder();
        url.Append("v1/db/").Append(Uri.EscapeDataString(_database))
           .Append("/measurements/").Append(Uri.EscapeDataString(measurement))
           .Append('/').Append(suffix);
        var qs = new List<string>();
        var onerror = TryGetParam(parameters, "onerror");
        if (!string.IsNullOrEmpty(onerror))
            qs.Add("onerror=" + Uri.EscapeDataString(onerror));
        var flush = TryGetParam(parameters, "flush");
        if (!string.IsNullOrEmpty(flush))
            qs.Add("flush=" + Uri.EscapeDataString(flush));
        if (qs.Count > 0)
            url.Append('?').Append(string.Join('&', qs));

        string contentType = format == BulkPayloadFormat.Json
            ? "application/json"
            : "text/plain";
        using var request = new HttpRequestMessage(HttpMethod.Post, url.ToString())
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, contentType),
        };

        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var body = await JsonSerializer.DeserializeAsync(
                stream,
                RemoteJsonContext.Default.BulkIngestResponseBody,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SndbServerException("bulk_ingest_error", "服务端响应体为空。", response.StatusCode);
        return MaterializedExecutionResult.NonQuery((int)body.WrittenRows);
    }

    public object BeginTransaction(IsolationLevel isolationLevel)
    {
        if (_http is null || _state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");
        if (isolationLevel is not IsolationLevel.Unspecified and not IsolationLevel.ReadCommitted)
            throw new NotSupportedException("SonnetDB 轻事务当前仅支持默认隔离级别。");

        return new RemoteTransactionState();
    }

    public void CommitTransaction(object transactionState)
        => CommitTransactionAsync(transactionState, CancellationToken.None).GetAwaiter().GetResult();

    public async Task CommitTransactionAsync(object transactionState, CancellationToken cancellationToken)
    {
        var transaction = GetRequiredTransactionState(transactionState);
        transaction.ThrowIfCompleted();

        var statements = new List<SqlRequestBody>(transaction.Statements.Count + 2)
        {
            new() { Sql = "BEGIN" },
        };
        statements.AddRange(transaction.Statements.Select(static sql => new SqlRequestBody { Sql = sql }));
        statements.Add(new SqlRequestBody { Sql = "COMMIT" });

        await ExecuteBatchRequestAsync(statements, cancellationToken).ConfigureAwait(false);
        transaction.MarkCompleted();
    }

    public IReadOnlyList<TableSchema> SnapshotTables()
        => SnapshotTablesAsync(CancellationToken.None).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<TableSchema>> SnapshotTablesAsync(CancellationToken cancellationToken)
    {
        if (_http is null || _state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");

        var url = $"v1/db/{Uri.EscapeDataString(_database)}/schema";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var body = await JsonSerializer.DeserializeAsync(
                stream,
                RemoteJsonContext.Default.RemoteSchemaResponse,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("SonnetDB schema response body is empty.");

        return (body.Tables ?? [])
            .Select(ToTableSchema)
            .ToArray();
    }

    public void RollbackTransaction(object transactionState)
    {
        var transaction = GetRequiredTransactionState(transactionState);
        transaction.ThrowIfCompleted();
        transaction.MarkCompleted();
    }

    public Task RollbackTransactionAsync(object transactionState, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RollbackTransaction(transactionState);
        return Task.CompletedTask;
    }

    private async Task ExecuteBatchRequestAsync(
        IReadOnlyList<SqlRequestBody> statements,
        CancellationToken cancellationToken)
    {
        if (_http is null || _state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");

        var url = $"v1/db/{Uri.EscapeDataString(_database)}/sql/batch";
        var json = JsonSerializer.Serialize(
            new SqlBatchRequestBody { Statements = [.. statements] },
            RemoteJsonContext.Default.SqlBatchRequestBody);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
                continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var errProp)
                && errProp.ValueKind == JsonValueKind.String)
            {
                var error = errProp.GetString() ?? "sql_error";
                var message = root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String
                    ? msgProp.GetString() ?? string.Empty
                    : string.Empty;
                throw new SndbServerException(error, message, System.Net.HttpStatusCode.OK);
            }
        }
    }

    private static string? TryGetParam(SndbParameterCollection parameters, string name)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            if (string.Equals(p.ParameterName?.TrimStart('@', ':'), name, StringComparison.OrdinalIgnoreCase))
                return p.Value?.ToString();
        }
        return null;
    }

    /// <summary>
    /// 极简扫描 JSON 文本，提取顶层 <c>"m"</c> 字段。仅在远程客户端用于决定 endpoint 路径段，
    /// 真正的 JSON 解析仍由服务端 <see cref="JsonPointsReader"/> 完成。
    /// </summary>
    private static string? TryPeekJsonMeasurement(ReadOnlySpan<char> json)
    {
        // 寻找 "m" : "<value>" — 仅匹配第一处。容错有限，主要服务于按规范构造的 payload。
        int i = 0;
        while (i < json.Length)
        {
            // 找下一个 "
            int q1 = IndexOf(json, '"', i);
            if (q1 < 0) return null;
            int q2 = IndexOf(json, '"', q1 + 1);
            if (q2 < 0) return null;
            var key = json.Slice(q1 + 1, q2 - q1 - 1);
            i = q2 + 1;
            if (!key.Equals("m", StringComparison.Ordinal)) continue;
            // 跳过空白与冒号
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
            if (i >= json.Length || json[i] != ':') continue;
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
            if (i >= json.Length || json[i] != '"') return null;
            int v1 = i + 1;
            int v2 = IndexOf(json, '"', v1);
            if (v2 < 0) return null;
            return new string(json.Slice(v1, v2 - v1));
        }
        return null;
    }

    private static int IndexOf(ReadOnlySpan<char> s, char c, int start)
    {
        for (int i = start; i < s.Length; i++)
            if (s[i] == c) return i;
        return -1;
    }

    private static string? SafePeekBulkMeasurement(ReadOnlyMemory<char> payload)
    {
        try { return SchemaBoundBulkValuesReader.PeekMeasurementName(payload.ToString()); }
        catch (BulkIngestException) { return null; }
    }

    private static RemoteTransactionState? GetTransactionState(object? transactionState)
        => transactionState switch
        {
            null => null,
            RemoteTransactionState transaction => transaction,
            _ => throw new InvalidOperationException("事务状态不是远程 SonnetDB 轻事务。"),
        };

    private static RemoteTransactionState GetRequiredTransactionState(object transactionState)
        => GetTransactionState(transactionState)
            ?? throw new InvalidOperationException("事务状态为空。");

    private static TableSchema ToTableSchema(RemoteTableInfo table)
    {
        var columns = table.Columns
            .OrderBy(static column => column.Ordinal)
            .Select(static column => (
                column.Name,
                ParseTableColumnType(column.DataType),
                column.IsNullable))
            .ToArray();
        var indexes = table.Indexes
            .Select(static index => new TableIndexDefinition(
                index.Name,
                index.Columns,
                index.IsUnique,
                index.CreatedUtc.UtcDateTime.Ticks,
                index.JsonPath))
            .ToArray();

        return TableSchema.Create(
            table.Name,
            columns,
            table.PrimaryKey,
            indexes,
            createdAtUtcTicks: table.CreatedUtc.UtcDateTime.Ticks);
    }

    private static TableColumnType ParseTableColumnType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToUpperInvariant() switch
        {
            "INT" or "INT64" or "INTEGER" or "BIGINT" => TableColumnType.Int64,
            "FLOAT" or "FLOAT64" or "DOUBLE" or "REAL" => TableColumnType.Float64,
            "BOOL" or "BOOLEAN" => TableColumnType.Boolean,
            "STRING" or "TEXT" => TableColumnType.String,
            "DATETIME" or "TIMESTAMP" => TableColumnType.DateTime,
            "BLOB" or "BINARY" => TableColumnType.Blob,
            "JSON" => TableColumnType.Json,
            _ => Enum.TryParse<TableColumnType>(value, ignoreCase: true, out var parsed)
                ? parsed
                : throw new InvalidDataException($"远程 schema 返回未知表列类型 '{value}'。"),
        };
    }

    /// <summary>
    /// 解析连接字符串中的 <c>Data Source</c>，返回 (baseUrl, databaseFromPath)。
    /// 支持 <c>sonnetdb+http://host:port/dbname</c> / <c>http://host:port/dbname</c>。
    /// </summary>
    internal static (string BaseUrl, string DatabaseFromPath) ParseEndpoint(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
            throw new InvalidOperationException("远程连接缺少 'Data Source'。");

        var ds = dataSource.Trim();
        if (ds.StartsWith("sonnetdb+http://", StringComparison.OrdinalIgnoreCase))
            ds = "http://" + ds["sonnetdb+http://".Length..];
        else if (ds.StartsWith("sonnetdb+https://", StringComparison.OrdinalIgnoreCase))
            ds = "https://" + ds["sonnetdb+https://".Length..];

        if (!Uri.TryCreate(ds, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"远程 Data Source 不是合法 URL: {dataSource}");
        if (uri.Scheme != "http" && uri.Scheme != "https")
            throw new InvalidOperationException($"不支持的远程 scheme: {uri.Scheme}");

        var baseUrl = $"{uri.Scheme}://{uri.Authority}/";
        var path = uri.AbsolutePath.Trim('/');
        return (baseUrl, path);
    }
}

internal sealed class RemoteTransactionState
{
    private readonly List<string> _statements = [];
    private bool _completed;

    public IReadOnlyList<string> Statements => _statements;

    public void Add(string sql)
    {
        ThrowIfCompleted();
        _statements.Add(sql);
    }

    public void MarkCompleted()
        => _completed = true;

    public void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("轻事务已结束。");
    }
}
