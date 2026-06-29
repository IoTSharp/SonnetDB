using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Data;
using SonnetDB.Data.Documents;
using SonnetDB.Data.Kv;
using SonnetDB.Data.Mq;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.Model;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Native;

internal enum NativeValueType
{
    Null = 0,
    Int64 = 1,
    Float64 = 2,
    Boolean = 3,
    Text = 4,
}

internal sealed class NativeConnection : IDisposable
{
    private SndbConnection? _connection;

    public NativeConnection(string connectionStringOrDataSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringOrDataSource);
        ConnectionString = BuildConnectionString(connectionStringOrDataSource);
        _connection = new SndbConnection(ConnectionString);
        _connection.Open();
    }

    public string ConnectionString { get; }

    public NativeResult Execute(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var connection = _connection ?? throw new ObjectDisposedException(nameof(NativeConnection));
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        return NativeResult.From(reader);
    }

    public NativeResult ExecuteBulk(NativeBulk bulk)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        var connection = _connection ?? throw new ObjectDisposedException(nameof(NativeConnection));
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.TableDirect;
        command.CommandText = bulk.Payload;
        AddBulkParameter(command, "measurement", bulk.Measurement);
        AddBulkParameter(command, "onerror", bulk.OnError);
        AddBulkParameter(command, "flush", bulk.Flush);
        return NativeResult.NonQuery(command.ExecuteNonQuery());
    }

    public void Flush()
    {
        var connection = _connection ?? throw new ObjectDisposedException(nameof(NativeConnection));
        if (connection.UnderlyingTsdb is not { } tsdb)
            throw new NotSupportedException("sonnetdb_flush is only available for embedded connections.");

        tsdb.FlushNow();
    }

    public void Dispose()
    {
        var connection = _connection;
        _connection = null;
        connection?.Dispose();
    }

    private static string BuildConnectionString(string connectionStringOrDataSource)
    {
        if (LooksLikeConnectionString(connectionStringOrDataSource))
            return connectionStringOrDataSource;

        var builder = new SndbConnectionStringBuilder
        {
            DataSource = connectionStringOrDataSource,
        };
        return builder.ConnectionString;
    }

    private static bool LooksLikeConnectionString(string value)
    {
        if (!value.Contains('=', StringComparison.Ordinal))
            return false;

        try
        {
            var builder = new SndbConnectionStringBuilder(value);
            return builder.ContainsKey("Data Source")
                || builder.ContainsKey("Mode")
                || builder.ContainsKey("Database")
                || builder.ContainsKey("Token")
                || builder.ContainsKey("Timeout");
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void AddBulkParameter(SndbCommand command, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        command.Parameters.AddWithValue(name, value);
    }
}

internal sealed class NativeResult : IDisposable
{
    private readonly IReadOnlyList<string> _columns;
    private readonly IReadOnlyList<IReadOnlyList<object?>> _rows;
    private readonly Dictionary<int, IntPtr> _columnNamePointers = new();
    private readonly Dictionary<int, IntPtr> _valueTextPointers = new();
    private int _rowIndex = -1;
    private bool _disposed;

    private NativeResult(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int recordsAffected)
    {
        _columns = columns;
        _rows = rows;
        RecordsAffected = recordsAffected;
    }

    public int RecordsAffected { get; }

    public int ColumnCount
    {
        get
        {
            ThrowIfDisposed();
            return _columns.Count;
        }
    }

    public static NativeResult From(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var columns = new string[reader.FieldCount];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = reader.GetName(i);

        var rows = new List<IReadOnlyList<object?>>();
        while (reader.Read())
        {
            var values = new object[columns.Length];
            reader.GetValues(values);
            var row = new object?[columns.Length];
            for (int i = 0; i < values.Length; i++)
            {
                row[i] = values[i] is DBNull ? null : values[i];
            }

            rows.Add(row);
        }

        return new NativeResult(columns, rows, reader.RecordsAffected);
    }

    public IntPtr GetColumnName(int ordinal)
    {
        ThrowIfDisposed();
        ValidateColumnOrdinal(ordinal);

        if (_columnNamePointers.TryGetValue(ordinal, out var ptr))
            return ptr;

        ptr = Marshal.StringToCoTaskMemUTF8(_columns[ordinal]);
        _columnNamePointers.Add(ordinal, ptr);
        return ptr;
    }

    public int MoveNext()
    {
        ThrowIfDisposed();
        ReleaseValueTextPointers();

        if (_rowIndex + 1 >= _rows.Count)
            return 0;

        _rowIndex++;
        return 1;
    }

    public NativeValueType GetValueType(int ordinal)
    {
        var value = GetCurrentValue(ordinal);
        return value switch
        {
            null => NativeValueType.Null,
            byte or sbyte or short or ushort or int or uint or long => NativeValueType.Int64,
            ulong => NativeValueType.Int64,
            float or double or decimal => NativeValueType.Float64,
            bool => NativeValueType.Boolean,
            _ => NativeValueType.Text,
        };
    }

    public long GetInt64(int ordinal)
    {
        var value = GetCurrentValue(ordinal);
        return value switch
        {
            byte v => v,
            sbyte v => v,
            short v => v,
            ushort v => v,
            int v => v,
            uint v => v,
            long v => v,
            ulong v when v <= long.MaxValue => (long)v,
            _ => throw new InvalidOperationException($"Column {ordinal} is not an int64 value."),
        };
    }

    public double GetDouble(int ordinal)
    {
        var value = GetCurrentValue(ordinal);
        return value switch
        {
            byte v => v,
            sbyte v => v,
            short v => v,
            ushort v => v,
            int v => v,
            uint v => v,
            long v => v,
            ulong v => v,
            float v => v,
            double v => v,
            decimal v => (double)v,
            _ => throw new InvalidOperationException($"Column {ordinal} is not a double value."),
        };
    }

    public int GetBoolean(int ordinal)
    {
        var value = GetCurrentValue(ordinal);
        return value switch
        {
            bool v => v ? 1 : 0,
            _ => throw new InvalidOperationException($"Column {ordinal} is not a boolean value."),
        };
    }

    public IntPtr GetText(int ordinal)
    {
        var value = GetCurrentValue(ordinal);
        if (value is null)
            return IntPtr.Zero;

        if (_valueTextPointers.TryGetValue(ordinal, out var ptr))
            return ptr;

        ptr = Marshal.StringToCoTaskMemUTF8(FormatText(value));
        _valueTextPointers.Add(ordinal, ptr);
        return ptr;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ReleaseValueTextPointers();
        foreach (var ptr in _columnNamePointers.Values)
            Marshal.FreeCoTaskMem(ptr);
        _columnNamePointers.Clear();
        _disposed = true;
    }

    public static NativeResult NonQuery(int recordsAffected)
        => new(Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>(), recordsAffected);

    private object? GetCurrentValue(int ordinal)
    {
        ThrowIfDisposed();
        ValidateColumnOrdinal(ordinal);
        if (_rowIndex < 0 || _rowIndex >= _rows.Count)
            throw new InvalidOperationException("Result is not positioned on a row.");
        return _rows[_rowIndex][ordinal];
    }

    private void ValidateColumnOrdinal(int ordinal)
    {
        if ((uint)ordinal >= (uint)_columns.Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Column ordinal is out of range.");
    }

    private void ReleaseValueTextPointers()
    {
        foreach (var ptr in _valueTextPointers.Values)
            Marshal.FreeCoTaskMem(ptr);
        _valueTextPointers.Clear();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string FormatText(object value)
        => value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            double d => d.ToString("G17", CultureInfo.InvariantCulture),
            float f => f.ToString("G9", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToBase64String(bytes),
            byte or sbyte or short or ushort or int or uint or long or ulong
                => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            GeoPoint geo => string.Create(
                CultureInfo.InvariantCulture,
                $"POINT({geo.Lat:G17},{geo.Lon:G17})"),
            float[] vector => FormatVector(vector),
            _ => value.ToString() ?? string.Empty,
        };

    private static string FormatVector(float[] vector)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < vector.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(vector[i].ToString("G9", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }
}

internal sealed class NativeBulk
{
    public NativeBulk(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        Payload = payload;
    }

    public string Payload { get; }

    public string? Measurement { get; private set; }

    public string? OnError { get; private set; }

    public string? Flush { get; private set; }

    public void SetMeasurement(string? measurement)
    {
        Measurement = NormalizeOptional(measurement);
    }

    public void SetOnError(string? onError)
    {
        OnError = NormalizeOptional(onError);
    }

    public void SetFlush(string? flush)
    {
        Flush = NormalizeOptional(flush);
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

internal sealed class NativeDocument : IDisposable
{
    private readonly SndbDocumentClient _client;
    private readonly string _collection;
    private bool _disposed;

    public NativeDocument(NativeConnection connection, string collection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);

        _client = new SndbDocumentClient(connection.ConnectionString);
        _collection = collection;
    }

    public NativeDocumentResult CreateCollection(string? optionsJson)
    {
        ThrowIfDisposed();
        var options = Read(optionsJson, NativeDocumentJsonContext.Default.NativeDocumentCollectionOptions)
            ?? new NativeDocumentCollectionOptions();
        string status = _client.CreateCollectionAsync(_collection, options.IfNotExists, options.Validator)
            .GetAwaiter()
            .GetResult();
        return NativeDocumentResult.FromCollectionOperation(_collection, status);
    }

    public bool DropCollection()
    {
        ThrowIfDisposed();
        return _client.DropCollectionAsync(_collection).GetAwaiter().GetResult();
    }

    public NativeDocumentResult Insert(string payloadJson)
    {
        ThrowIfDisposed();
        var request = ReadRequired(payloadJson, NativeDocumentJsonContext.Default.NativeDocumentInsertRequest);
        SndbDocumentWriteResult result;
        if (request.Documents is { Count: > 0 })
        {
            result = _client.InsertManyAsync(
                _collection,
                request.Documents.Select(static item => new KeyValuePair<string, string>(item.Id, item.Document.GetRawText())),
                request.Ordered).GetAwaiter().GetResult();
        }
        else
        {
            result = _client.InsertOneAsync(
                _collection,
                RequireText(request.Id, "document insert payload must include 'id'."),
                RequireDocument(request.Document, "document insert payload must include 'document'.").GetRawText())
                .GetAwaiter()
                .GetResult();
        }

        return NativeDocumentResult.FromWrite(result);
    }

    public NativeDocumentResult Update(string payloadJson)
    {
        ThrowIfDisposed();
        var request = ReadRequired(payloadJson, NativeDocumentJsonContext.Default.NativeDocumentUpdateRequest);
        SndbDocumentWriteResult result;
        if (request.Documents is { Count: > 0 })
        {
            result = _client.UpdateManyAsync(
                _collection,
                request.Documents.Select(static item => new KeyValuePair<string, string>(item.Id, item.Document.GetRawText())),
                request.Ordered).GetAwaiter().GetResult();
        }
        else if (request.Update is not null)
        {
            if (request.Multi)
            {
                result = _client.UpdateManyAsync(
                    _collection,
                    request.Filter,
                    request.Update,
                    request.Upsert,
                    request.UpsertId).GetAwaiter().GetResult();
            }
            else
            {
                result = _client.UpdateOneAsync(
                    _collection,
                    request.Filter,
                    request.Update,
                    request.Id,
                    request.Upsert,
                    request.UpsertId).GetAwaiter().GetResult();
            }
        }
        else
        {
            result = _client.UpdateOneAsync(
                _collection,
                RequireText(request.Id, "document update payload must include 'id' when replacing one document."),
                RequireDocument(request.Document, "document update payload must include 'document' when replacing one document.").GetRawText())
                .GetAwaiter()
                .GetResult();
        }

        return NativeDocumentResult.FromWrite(result);
    }

    public NativeDocumentResult Delete(string payloadJson)
    {
        ThrowIfDisposed();
        var request = ReadRequired(payloadJson, NativeDocumentJsonContext.Default.NativeDocumentDeleteRequest);
        SndbDocumentWriteResult result;
        if (request.Ids is { Count: > 0 })
        {
            result = _client.DeleteManyAsync(_collection, request.Ids, request.Ordered)
                .GetAwaiter()
                .GetResult();
        }
        else
        {
            result = _client.DeleteOneAsync(
                _collection,
                RequireText(request.Id, "document delete payload must include 'id' or 'ids'."))
                .GetAwaiter()
                .GetResult();
        }

        return NativeDocumentResult.FromWrite(result);
    }

    public NativeDocumentResult FindPage(string? payloadJson)
    {
        ThrowIfDisposed();
        var options = Read(payloadJson, NativeDocumentJsonContext.Default.SndbDocumentFindOptions)
            ?? new SndbDocumentFindOptions();
        var page = _client.FindPageAsync(_collection, options).GetAwaiter().GetResult();
        return NativeDocumentResult.FromPage(page);
    }

    public NativeDocumentResult Aggregate(string payloadJson)
    {
        ThrowIfDisposed();
        var pipeline = ReadAggregatePipeline(payloadJson);
        var result = _client.AggregateAsync(_collection, pipeline).GetAwaiter().GetResult();
        return NativeDocumentResult.FromAggregate(result);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.Dispose();
    }

    private static T? Read<T>(string? json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;
        return JsonSerializer.Deserialize(json, typeInfo);
    }

    private static T ReadRequired<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new InvalidDataException("document JSON payload is empty.");

    private static IReadOnlyList<SndbDocumentAggregateStage> ReadAggregatePipeline(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            var pipeline = JsonSerializer.Deserialize(
                json,
                NativeDocumentJsonContext.Default.ListSndbDocumentAggregateStage);
            return pipeline is { Count: > 0 }
                ? pipeline
                : throw new InvalidOperationException("document aggregate payload must contain at least one stage.");
        }

        var request = JsonSerializer.Deserialize(
            json,
            NativeDocumentJsonContext.Default.NativeDocumentAggregateRequest)
            ?? throw new InvalidDataException("document aggregate JSON payload is empty.");
        return request.Pipeline is { Count: > 0 }
            ? request.Pipeline
            : throw new InvalidOperationException("document aggregate payload must contain a non-empty 'pipeline'.");
    }

    private static string RequireText(string? value, string message)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;

    private static JsonElement RequireDocument(JsonElement? document, string message)
        => document is null || document.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? throw new InvalidOperationException(message)
            : document.Value;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class NativeDocumentResult
{
    private NativeDocumentResult(string json)
    {
        Json = json;
    }

    public string Json { get; }

    public int JsonLength => Encoding.UTF8.GetByteCount(Json);

    public int CopyJson(IntPtr buffer, int bufferLength)
        => SonnetDbNativeExports.CopyUtf8(Json, buffer, bufferLength);

    public static NativeDocumentResult FromCollectionOperation(string collection, string status)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("collection", collection);
            writer.WriteString("status", status);
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    public static NativeDocumentResult FromWrite(SndbDocumentWriteResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("collection", result.Collection);
            writer.WriteNumber("inserted", result.Inserted);
            writer.WriteNumber("matched", result.Matched);
            writer.WriteNumber("modified", result.Modified);
            writer.WriteNumber("deleted", result.Deleted);
            if (result.Errors is { Count: > 0 })
            {
                writer.WritePropertyName("errors");
                writer.WriteStartArray();
                foreach (var error in result.Errors)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("index", error.Index);
                    WriteNullableString(writer, "id", error.Id);
                    writer.WriteString("code", error.Code);
                    writer.WriteString("message", error.Message);
                    writer.WriteString("severity", error.Severity);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    public static NativeDocumentResult FromPage(SndbDocumentPage page)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("collection", page.Collection);
            writer.WritePropertyName("documents");
            writer.WriteStartArray();
            foreach (var document in page.Documents)
                WriteDocument(writer, document);
            writer.WriteEndArray();
            writer.WriteNumber("count", page.Documents.Count);
            WriteNullableString(writer, "continuationToken", page.ContinuationToken);
            writer.WriteBoolean("hasMore", page.HasMore);
            writer.WriteNumber("batchSize", page.BatchSize);
            WriteNullableNumber(writer, "snapshotVersion", page.SnapshotVersion);
            WriteNullableDateTimeOffset(writer, "cursorExpiresAtUtc", page.CursorExpiresAtUtc);
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    public static NativeDocumentResult FromAggregate(SndbDocumentAggregateResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("collection", result.Collection);
            writer.WritePropertyName("documents");
            writer.WriteStartArray();
            foreach (var document in result.Documents)
                writer.WriteRawValue(document);
            writer.WriteEndArray();
            writer.WriteNumber("count", result.Count);
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    private static Utf8JsonWriter CreateWriter(Stream stream)
        => new(stream, new JsonWriterOptions { Indented = false });

    private static NativeDocumentResult FromStream(MemoryStream stream)
        => new(Encoding.UTF8.GetString(stream.ToArray()));

    private static void WriteDocument(Utf8JsonWriter writer, SndbDocument document)
    {
        writer.WriteStartObject();
        writer.WriteString("id", document.Id);
        writer.WritePropertyName("document");
        writer.WriteRawValue(document.Json);
        writer.WriteNumber("version", document.Version);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteNumber(name, value.Value);
    }

    private static void WriteNullableDateTimeOffset(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }
}

internal sealed record NativeDocumentCollectionOptions(
    bool IfNotExists = true,
    SndbDocumentValidator? Validator = null);

internal sealed record NativeDocumentWriteItem(string Id, JsonElement Document);

internal sealed record NativeDocumentInsertRequest(
    string? Id = null,
    JsonElement? Document = null,
    IReadOnlyList<NativeDocumentWriteItem>? Documents = null,
    bool Ordered = true);

internal sealed record NativeDocumentUpdateRequest(
    string? Id = null,
    JsonElement? Document = null,
    IReadOnlyList<NativeDocumentWriteItem>? Documents = null,
    SndbDocumentFilter? Filter = null,
    SndbDocumentUpdate? Update = null,
    bool Upsert = false,
    string? UpsertId = null,
    bool Ordered = true,
    bool Multi = false);

internal sealed record NativeDocumentDeleteRequest(
    string? Id = null,
    IReadOnlyList<string>? Ids = null,
    bool Ordered = true);

internal sealed record NativeDocumentAggregateRequest(
    IReadOnlyList<SndbDocumentAggregateStage> Pipeline);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(NativeDocumentCollectionOptions))]
[JsonSerializable(typeof(NativeDocumentInsertRequest))]
[JsonSerializable(typeof(NativeDocumentUpdateRequest))]
[JsonSerializable(typeof(NativeDocumentDeleteRequest))]
[JsonSerializable(typeof(NativeDocumentAggregateRequest))]
[JsonSerializable(typeof(NativeDocumentWriteItem))]
[JsonSerializable(typeof(SndbDocumentFindOptions))]
[JsonSerializable(typeof(SndbDocumentFilter))]
[JsonSerializable(typeof(SndbDocumentProjection))]
[JsonSerializable(typeof(SndbDocumentSort))]
[JsonSerializable(typeof(SndbDocumentUpdate))]
[JsonSerializable(typeof(SndbDocumentValidator))]
[JsonSerializable(typeof(SndbDocumentValidatorRule))]
[JsonSerializable(typeof(SndbDocumentAggregateStage))]
[JsonSerializable(typeof(SndbDocumentAggregateGroup))]
[JsonSerializable(typeof(SndbDocumentAggregateGroupKey))]
[JsonSerializable(typeof(SndbDocumentAggregateAccumulator))]
[JsonSerializable(typeof(SndbDocumentAggregateUnwind))]
[JsonSerializable(typeof(SndbDocumentAggregateDistinct))]
[JsonSerializable(typeof(List<NativeDocumentWriteItem>))]
[JsonSerializable(typeof(List<SndbDocumentFilter>))]
[JsonSerializable(typeof(List<SndbDocumentProjection>))]
[JsonSerializable(typeof(List<SndbDocumentSort>))]
[JsonSerializable(typeof(List<SndbDocumentValidatorRule>))]
[JsonSerializable(typeof(List<SndbDocumentAggregateStage>))]
[JsonSerializable(typeof(List<SndbDocumentAggregateGroupKey>))]
[JsonSerializable(typeof(List<SndbDocumentAggregateAccumulator>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal sealed partial class NativeDocumentJsonContext : JsonSerializerContext;

internal sealed class NativeObjectStorage : IDisposable
{
    private readonly SndbObjectStorageClient _client;
    private readonly string _bucket;
    private bool _disposed;

    public NativeObjectStorage(NativeConnection connection, string bucket)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        _client = new SndbObjectStorageClient(connection.ConnectionString);
        _bucket = bucket;
    }

    public string Bucket => _bucket;

    public NativeObjectResult ListBuckets()
    {
        ThrowIfDisposed();
        var buckets = _client.ListBucketsAsync().GetAwaiter().GetResult();
        return NativeObjectResult.FromBuckets(buckets);
    }

    public NativeObjectResult CreateBucket(string? purpose)
    {
        ThrowIfDisposed();
        return NativeObjectResult.FromBucket(_client.CreateBucketAsync(_bucket, NormalizeOptional(purpose)).GetAwaiter().GetResult());
    }

    public bool DeleteBucket()
    {
        ThrowIfDisposed();
        return _client.DeleteBucketAsync(_bucket).GetAwaiter().GetResult();
    }

    public NativeObjectResult PutObject(string key, NativeObjectWriteBuffer writeBuffer)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(writeBuffer);

        using var content = writeBuffer.OpenRead();
        return NativeObjectResult.FromObject(_client.PutObjectAsync(
            _bucket,
            key,
            content,
            writeBuffer.ContentType,
            writeBuffer.Metadata,
            writeBuffer.Tags).GetAwaiter().GetResult());
    }

    public NativeObjectReader? OpenReader(string key, long offset, long length)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be non-negative.");

        SndbObjectRange? range = offset > 0 || length >= 0
            ? new SndbObjectRange(offset, length < 0 ? null : length)
            : null;
        var result = _client.OpenReadAsync(_bucket, key, range).GetAwaiter().GetResult();
        return result is null ? null : new NativeObjectReader(result);
    }

    public NativeObjectResult? HeadObject(string key)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var info = _client.HeadObjectAsync(_bucket, key).GetAwaiter().GetResult();
        return info is null ? null : NativeObjectResult.FromObject(info);
    }

    public NativeObjectResult ListObjects(string? prefix, int maxKeys, string? continuationToken)
    {
        ThrowIfDisposed();
        var result = _client.ListObjectsAsync(
            _bucket,
            NormalizeOptional(prefix),
            maxKeys <= 0 ? 1000 : maxKeys,
            NormalizeOptional(continuationToken)).GetAwaiter().GetResult();
        return NativeObjectResult.FromList(result);
    }

    public NativeObjectResult DeleteObject(string key)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _client.DeleteObjectAsync(_bucket, key).GetAwaiter().GetResult();
        return NativeObjectResult.FromDelete(_bucket, key);
    }

    public NativeObjectResult DeleteObjects(string keysJson)
    {
        ThrowIfDisposed();
        var keys = JsonSerializer.Deserialize(keysJson, NativeObjectJsonContext.Default.ListString)
            ?? throw new InvalidDataException("object delete JSON payload is empty.");
        return NativeObjectResult.FromDeleteMany(_client.DeleteObjectsAsync(_bucket, keys).GetAwaiter().GetResult());
    }

    public NativeObjectResult InitiateMultipart(string key, string? contentType, string? metadataJson, string? tagsJson)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var upload = _client.InitiateMultipartUploadAsync(
            _bucket,
            key,
            NormalizeOptional(contentType),
            ReadStringMap(metadataJson),
            ReadStringMap(tagsJson)).GetAwaiter().GetResult();
        return NativeObjectResult.FromMultipartUpload(upload);
    }

    public NativeObjectResult UploadPart(string key, string uploadId, int partNumber, NativeObjectWriteBuffer writeBuffer)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        ArgumentNullException.ThrowIfNull(writeBuffer);

        using var content = writeBuffer.OpenRead();
        var part = _client.UploadPartAsync(_bucket, key, uploadId, partNumber, content).GetAwaiter().GetResult();
        return NativeObjectResult.FromMultipartPart(_bucket, key, uploadId, part);
    }

    public NativeObjectResult CompleteMultipart(string key, string uploadId, string partNumbersJson)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        var partNumbers = JsonSerializer.Deserialize(partNumbersJson, NativeObjectJsonContext.Default.ListInt32)
            ?? throw new InvalidDataException("multipart complete JSON payload is empty.");
        return NativeObjectResult.FromObject(_client.CompleteMultipartUploadAsync(_bucket, key, uploadId, partNumbers).GetAwaiter().GetResult());
    }

    public void AbortMultipart(string key, string uploadId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        _client.AbortMultipartUploadAsync(_bucket, key, uploadId).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.Dispose();
    }

    private static IReadOnlyDictionary<string, string>? ReadStringMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var map = JsonSerializer.Deserialize(json, NativeObjectJsonContext.Default.DictionaryStringString);
        return map is { Count: > 0 } ? map : null;
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class NativeObjectWriteBuffer : IDisposable
{
    private readonly string _path;
    private FileStream? _stream;
    private bool _completed;
    private bool _disposed;

    public NativeObjectWriteBuffer(string? contentType, string? metadataJson, string? tagsJson)
    {
        _path = Path.Combine(Path.GetTempPath(), "sonnetdb-native-object-" + Guid.NewGuid().ToString("N") + ".tmp");
        _stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
        ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType;
        Metadata = ReadStringMap(metadataJson);
        Tags = ReadStringMap(tagsJson);
    }

    public string? ContentType { get; }

    public IReadOnlyDictionary<string, string>? Metadata { get; }

    public IReadOnlyDictionary<string, string>? Tags { get; }

    public long Length
    {
        get
        {
            ThrowIfDisposed();
            return _stream?.Length ?? new FileInfo(_path).Length;
        }
    }

    public void Write(byte[] bytes)
    {
        ThrowIfDisposed();
        if (_completed)
            throw new InvalidOperationException("object write handle is already completed.");
        _stream!.Write(bytes, 0, bytes.Length);
    }

    public FileStream OpenRead()
    {
        ThrowIfDisposed();
        if (!_completed)
            Complete();
        return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, FileOptions.DeleteOnClose);
    }

    public void Complete()
    {
        ThrowIfDisposed();
        if (_completed)
            return;

        _stream!.Flush();
        _stream.Dispose();
        _stream = null;
        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _stream?.Dispose();
        TryDelete(_path);
        _disposed = true;
    }

    private static IReadOnlyDictionary<string, string>? ReadStringMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var map = JsonSerializer.Deserialize(json, NativeObjectJsonContext.Default.DictionaryStringString);
        return map is { Count: > 0 } ? map : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class NativeObjectReader : IDisposable
{
    private readonly SndbObjectReadResult _result;
    private readonly Dictionary<int, IntPtr> _textPointers = new();
    private bool _disposed;

    public NativeObjectReader(SndbObjectReadResult result)
    {
        _result = result;
    }

    public long Offset
    {
        get
        {
            ThrowIfDisposed();
            return _result.Offset;
        }
    }

    public long Length
    {
        get
        {
            ThrowIfDisposed();
            return _result.Length;
        }
    }

    public int IsRange
    {
        get
        {
            ThrowIfDisposed();
            return _result.IsRange ? 1 : 0;
        }
    }

    public IntPtr GetBucket() => GetTextPointer(0, _result.Info.Bucket);

    public IntPtr GetKey() => GetTextPointer(1, _result.Info.Key);

    public IntPtr GetContentType() => GetTextPointer(2, _result.Info.ContentType);

    public IntPtr GetETag() => GetTextPointer(3, _result.Info.ETag);

    public IntPtr GetSha256() => GetTextPointer(4, _result.Info.Sha256);

    public long GetSizeBytes()
    {
        ThrowIfDisposed();
        return _result.Info.SizeBytes;
    }

    public int Read(IntPtr buffer, int bufferLength)
    {
        ThrowIfDisposed();
        if (bufferLength < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferLength), "Buffer length cannot be negative.");
        if (buffer == IntPtr.Zero || bufferLength == 0)
            return 0;

        byte[] rented = ArrayPool<byte>.Shared.Rent(bufferLength);
        try
        {
            int read = _result.Content.Read(rented, 0, bufferLength);
            if (read > 0)
                Marshal.Copy(rented, 0, buffer, read);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var ptr in _textPointers.Values)
            Marshal.FreeCoTaskMem(ptr);
        _textPointers.Clear();
        _result.Content.Dispose();
        _disposed = true;
    }

    private IntPtr GetTextPointer(int key, string value)
    {
        ThrowIfDisposed();
        if (_textPointers.TryGetValue(key, out var ptr))
            return ptr;

        ptr = Marshal.StringToCoTaskMemUTF8(value);
        _textPointers.Add(key, ptr);
        return ptr;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class NativeObjectResult
{
    private NativeObjectResult(string json)
    {
        Json = json;
    }

    public string Json { get; }

    public int JsonLength => Encoding.UTF8.GetByteCount(Json);

    public int CopyJson(IntPtr buffer, int bufferLength)
        => SonnetDbNativeExports.CopyUtf8(Json, buffer, bufferLength);

    public static NativeObjectResult FromBuckets(IReadOnlyList<SndbBucketInfo> buckets)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("buckets");
            writer.WriteStartArray();
            foreach (var bucket in buckets)
                WriteBucket(writer, bucket);
            writer.WriteEndArray();
            writer.WriteNumber("count", buckets.Count);
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    public static NativeObjectResult FromBucket(SndbBucketInfo bucket)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("bucket");
            WriteBucket(writer, bucket);
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    public static NativeObjectResult FromObject(SndbObjectInfo info)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("object");
            WriteObject(writer, info);
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    public static NativeObjectResult FromList(SndbObjectListResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("bucket", result.Bucket);
            writer.WriteString("prefix", result.Prefix);
            writer.WriteNumber("maxKeys", result.MaxKeys);
            WriteNullableString(writer, "continuationToken", result.ContinuationToken);
            WriteNullableString(writer, "nextContinuationToken", result.NextContinuationToken);
            writer.WriteBoolean("isTruncated", result.IsTruncated);
            writer.WritePropertyName("objects");
            writer.WriteStartArray();
            foreach (var item in result.Objects)
                WriteObject(writer, item);
            writer.WriteEndArray();
            writer.WriteNumber("count", result.Objects.Count);
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    public static NativeObjectResult FromDelete(string bucket, string key)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("bucket", bucket);
            writer.WriteString("key", key);
            writer.WriteString("status", "deleted");
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    public static NativeObjectResult FromDeleteMany(SndbObjectDeleteManyResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("bucket", result.Bucket);
            writer.WritePropertyName("deleted");
            writer.WriteStartArray();
            foreach (var item in result.Deleted)
            {
                writer.WriteStartObject();
                writer.WriteString("key", item.Key);
                writer.WriteString("versionId", item.VersionId);
                writer.WriteBoolean("deleteMarker", item.DeleteMarker);
                WriteNullableString(writer, "errorCode", item.ErrorCode);
                WriteNullableString(writer, "errorMessage", item.ErrorMessage);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    public static NativeObjectResult FromMultipartUpload(SndbMultipartUploadInfo upload)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("bucket", upload.Bucket);
            writer.WriteString("key", upload.Key);
            writer.WriteString("uploadId", upload.UploadId);
            writer.WriteString("contentType", upload.ContentType);
            writer.WriteString("initiatedUtc", upload.InitiatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("expiresUtc", upload.ExpiresUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            WriteStringMap(writer, "metadata", upload.Metadata);
            WriteStringMap(writer, "tags", upload.Tags);
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    public static NativeObjectResult FromMultipartPart(string bucket, string key, string uploadId, SndbMultipartPartInfo part)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("bucket", bucket);
            writer.WriteString("key", key);
            writer.WriteString("uploadId", uploadId);
            writer.WriteNumber("partNumber", part.PartNumber);
            writer.WriteNumber("sizeBytes", part.SizeBytes);
            writer.WriteString("etag", part.ETag);
            writer.WriteString("sha256", part.Sha256);
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    public static NativeObjectResult FromMultipartAbort(string bucket, string key, string uploadId)
    {
        using var stream = new MemoryStream();
        using (var writer = CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("bucket", bucket);
            writer.WriteString("key", key);
            writer.WriteString("uploadId", uploadId);
            writer.WriteString("status", "aborted");
            writer.WriteEndObject();
        }

        return FromStream(stream);
    }

    private static Utf8JsonWriter CreateWriter(Stream stream)
        => new(stream, new JsonWriterOptions { Indented = false });

    private static NativeObjectResult FromStream(MemoryStream stream)
        => new(Encoding.UTF8.GetString(stream.ToArray()));

    private static void WriteBucket(Utf8JsonWriter writer, SndbBucketInfo bucket)
    {
        writer.WriteStartObject();
        writer.WriteString("name", bucket.Name);
        writer.WriteString("purpose", bucket.Purpose);
        writer.WriteString("createdUtc", bucket.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("updatedUtc", bucket.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteObject(Utf8JsonWriter writer, SndbObjectInfo info)
    {
        writer.WriteStartObject();
        writer.WriteString("bucket", info.Bucket);
        writer.WriteString("key", info.Key);
        writer.WriteString("versionId", info.VersionId);
        writer.WriteString("contentType", info.ContentType);
        writer.WriteNumber("sizeBytes", info.SizeBytes);
        writer.WriteString("etag", info.ETag);
        writer.WriteString("sha256", info.Sha256);
        writer.WriteBoolean("isDeleteMarker", info.IsDeleteMarker);
        writer.WriteString("createdUtc", info.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("updatedUtc", info.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        WriteStringMap(writer, "metadata", info.Metadata);
        WriteStringMap(writer, "tags", info.Tags);
        writer.WriteEndObject();
    }

    private static void WriteStringMap(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach (var pair in values)
            writer.WriteString(pair.Key, pair.Value);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<int>))]
internal sealed partial class NativeObjectJsonContext : JsonSerializerContext;

internal sealed class NativeMessageQueue : IDisposable
{
    private readonly SndbMqClient _client;
    private readonly string _topic;
    private bool _disposed;

    public NativeMessageQueue(NativeConnection connection, string topic)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        _client = new SndbMqClient(connection.ConnectionString);
        _topic = topic;
    }

    public long Publish(byte[] payload, string? headersJson)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(payload);
        return _client.PublishAsync(_topic, payload, ReadStringMap(headersJson)).GetAwaiter().GetResult();
    }

    public NativeMessageQueuePull Pull(string consumerGroup, int maxCount)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        var messages = _client.PullAsync(_topic, consumerGroup, maxCount <= 0 ? 100 : maxCount)
            .GetAwaiter()
            .GetResult()
            .Select(NativeMessageQueueMessage.From)
            .ToArray();
        return new NativeMessageQueuePull(messages);
    }

    public long Ack(string consumerGroup, long offset)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return _client.AckAsync(_topic, consumerGroup, offset).GetAwaiter().GetResult();
    }

    public NativeMessageQueueResult GetStats()
    {
        ThrowIfDisposed();
        var stats = _client.GetStatsAsync(_topic).GetAwaiter().GetResult();
        return NativeMessageQueueResult.FromStats(stats);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.Dispose();
    }

    private static IReadOnlyDictionary<string, string>? ReadStringMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var map = JsonSerializer.Deserialize(json, NativeMessageQueueJsonContext.Default.DictionaryStringString);
        return map is { Count: > 0 } ? map : null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class NativeMessageQueuePull : IDisposable
{
    private readonly IReadOnlyList<NativeMessageQueueMessage> _messages;
    private int _index = -1;
    private bool _disposed;

    public NativeMessageQueuePull(IReadOnlyList<NativeMessageQueueMessage> messages)
    {
        _messages = messages;
    }

    public int MessageCount
    {
        get
        {
            ThrowIfDisposed();
            return _messages.Count;
        }
    }

    public int MoveNext()
    {
        ThrowIfDisposed();
        if (_index + 1 >= _messages.Count)
            return 0;

        _index++;
        return 1;
    }

    public NativeMessageQueueMessage Current
    {
        get
        {
            ThrowIfDisposed();
            if (_index < 0 || _index >= _messages.Count)
                throw new InvalidOperationException("MQ pull cursor is not positioned on a message.");
            return _messages[_index];
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var message in _messages)
            message.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class NativeMessageQueueMessage : IDisposable
{
    private readonly Dictionary<int, IntPtr> _textPointers = new();
    private readonly string _headersJson;
    private bool _disposed;

    private NativeMessageQueueMessage(
        string topic,
        long offset,
        DateTimeOffset timestampUtc,
        IReadOnlyDictionary<string, string> headers,
        byte[] payload)
    {
        Topic = topic;
        Offset = offset;
        TimestampUtc = timestampUtc;
        Headers = headers;
        Payload = payload;
        _headersJson = JsonSerializer.Serialize(headers, NativeMessageQueueJsonContext.Default.IReadOnlyDictionaryStringString);
    }

    public string Topic { get; }

    public long Offset { get; }

    public DateTimeOffset TimestampUtc { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public byte[] Payload { get; }

    public static NativeMessageQueueMessage From(SndbMqMessage message)
        => new(message.Topic, message.Offset, message.TimestampUtc, message.Headers, message.Payload);

    public IntPtr GetTopic()
    {
        ThrowIfDisposed();
        return GetTextPointer(0, Topic);
    }

    public long GetTimestampUnixMs()
    {
        ThrowIfDisposed();
        return TimestampUtc.ToUnixTimeMilliseconds();
    }

    public int GetHeadersJsonLength()
    {
        ThrowIfDisposed();
        return Encoding.UTF8.GetByteCount(_headersJson);
    }

    public int CopyHeadersJson(IntPtr buffer, int bufferLength)
    {
        ThrowIfDisposed();
        return SonnetDbNativeExports.CopyUtf8(_headersJson, buffer, bufferLength);
    }

    public long GetPayloadLength()
    {
        ThrowIfDisposed();
        return Payload.LongLength;
    }

    public int CopyPayload(IntPtr buffer, int bufferLength)
    {
        ThrowIfDisposed();
        return SonnetDbNativeExports.CopyBytes(Payload, buffer, bufferLength);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var ptr in _textPointers.Values)
            Marshal.FreeCoTaskMem(ptr);
        _textPointers.Clear();
        _disposed = true;
    }

    private IntPtr GetTextPointer(int key, string value)
    {
        if (_textPointers.TryGetValue(key, out var ptr))
            return ptr;

        ptr = Marshal.StringToCoTaskMemUTF8(value);
        _textPointers.Add(key, ptr);
        return ptr;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class NativeMessageQueueResult
{
    private NativeMessageQueueResult(string json)
    {
        Json = json;
    }

    public string Json { get; }

    public int JsonLength => Encoding.UTF8.GetByteCount(Json);

    public int CopyJson(IntPtr buffer, int bufferLength)
        => SonnetDbNativeExports.CopyUtf8(Json, buffer, bufferLength);

    public static NativeMessageQueueResult FromStats(SndbMqStats stats)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("topic", stats.Topic);
            writer.WriteNumber("messageCount", stats.MessageCount);
            writer.WriteNumber("nextOffset", stats.NextOffset);
            writer.WritePropertyName("consumerOffsets");
            writer.WriteStartObject();
            foreach (var pair in stats.ConsumerOffsets)
                writer.WriteNumber(pair.Key, pair.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new NativeMessageQueueResult(Encoding.UTF8.GetString(stream.ToArray()));
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal sealed partial class NativeMessageQueueJsonContext : JsonSerializerContext;

internal sealed class NativeKv : IDisposable
{
    private readonly SndbKvClient _client;
    private readonly string _keyspace;
    private readonly string _namespace;
    private bool _disposed;

    public NativeKv(NativeConnection connection, string keyspace, string? @namespace)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);

        _client = new SndbKvClient(connection.ConnectionString);
        _keyspace = keyspace;
        _namespace = @namespace ?? string.Empty;
    }

    public NativeKvEntry? Get(string key)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        var entry = _client.GetAsync(_keyspace, _namespace, key).GetAwaiter().GetResult();
        return entry is null ? null : NativeKvEntry.From(entry);
    }

    public long Set(string key, byte[] value, DateTimeOffset? expiresAtUtc)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return _client.SetAsync(_keyspace, _namespace, key, value, expiresAtUtc).GetAwaiter().GetResult();
    }

    public bool Delete(string key)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        return _client.RemoveAsync(_keyspace, _namespace, key).GetAwaiter().GetResult();
    }

    public NativeKvScan ScanPrefix(string prefix, int limit)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(prefix);
        var entries = _client.ScanPrefixAsync(
            _keyspace,
            _namespace,
            prefix,
            limit <= 0 ? null : limit).GetAwaiter().GetResult();
        return new NativeKvScan(entries.Select(NativeKvEntry.From).ToArray());
    }

    public SndbKvTtlResult GetTimeToLive(string key)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        return _client.GetTimeToLiveAsync(_keyspace, _namespace, key).GetAwaiter().GetResult();
    }

    public (long Value, long Version) Increment(string key, long delta)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        return _client.IncrementAsync(_keyspace, _namespace, key, delta).GetAwaiter().GetResult();
    }

    public SndbKvCasResult CompareAndSet(string key, long expectedVersion, byte[] value, DateTimeOffset? expiresAtUtc)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return _client.CompareAndSetAsync(_keyspace, _namespace, key, expectedVersion, value, expiresAtUtc)
            .GetAwaiter()
            .GetResult();
    }

    public bool Expire(string key, DateTimeOffset expiresAtUtc)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        return _client.ExpireAsync(_keyspace, _namespace, key, expiresAtUtc).GetAwaiter().GetResult();
    }

    public bool Persist(string key)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        return _client.PersistAsync(_keyspace, _namespace, key).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class NativeKvEntry : IDisposable
{
    private readonly Dictionary<int, IntPtr> _textPointers = new();
    private bool _disposed;

    private NativeKvEntry(string key, byte[] value, long version, DateTimeOffset? expiresAtUtc)
    {
        Key = key;
        Value = value;
        Version = version;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string Key { get; }

    public byte[] Value { get; }

    public long Version { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public static NativeKvEntry From(SndbKvEntry entry)
        => new(entry.Key, entry.Value, entry.Version, entry.ExpiresAtUtc);

    public IntPtr GetKey()
    {
        ThrowIfDisposed();
        return GetTextPointer(0, Key);
    }

    public long GetValueLength()
    {
        ThrowIfDisposed();
        return Value.LongLength;
    }

    public int CopyValue(IntPtr buffer, int bufferLength)
    {
        ThrowIfDisposed();
        return SonnetDbNativeExports.CopyBytes(Value, buffer, bufferLength);
    }

    public long GetExpiresAtUnixMilliseconds()
    {
        ThrowIfDisposed();
        return ExpiresAtUtc?.ToUnixTimeMilliseconds() ?? -1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var ptr in _textPointers.Values)
            Marshal.FreeCoTaskMem(ptr);
        _textPointers.Clear();
        _disposed = true;
    }

    private IntPtr GetTextPointer(int key, string value)
    {
        if (_textPointers.TryGetValue(key, out var ptr))
            return ptr;

        ptr = Marshal.StringToCoTaskMemUTF8(value);
        _textPointers.Add(key, ptr);
        return ptr;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class NativeKvScan : IDisposable
{
    private readonly IReadOnlyList<NativeKvEntry> _entries;
    private int _index = -1;
    private bool _disposed;

    public NativeKvScan(IReadOnlyList<NativeKvEntry> entries)
    {
        _entries = entries;
    }

    public int MoveNext()
    {
        ThrowIfDisposed();
        if (_index + 1 >= _entries.Count)
            return 0;

        _index++;
        return 1;
    }

    public NativeKvEntry Current
    {
        get
        {
            ThrowIfDisposed();
            if (_index < 0 || _index >= _entries.Count)
                throw new InvalidOperationException("KV scan is not positioned on an entry.");
            return _entries[_index];
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var entry in _entries)
            entry.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal static class SonnetDbNativeExports
{
    [ThreadStatic]
    private static string? s_lastError;

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_open", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr Open(IntPtr dataSource)
    {
        try
        {
            ClearError();
            var path = ReadUtf8(dataSource, nameof(dataSource));
            var connection = new NativeConnection(path);
            return GCHandle.ToIntPtr(GCHandle.Alloc(connection));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_close", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Close(IntPtr connection)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (connection == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(connection);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_execute", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr Execute(IntPtr connection, IntPtr sql)
    {
        try
        {
            ClearError();
            var nativeConnection = GetTarget<NativeConnection>(connection, nameof(connection));
            var text = ReadUtf8(sql, nameof(sql));
            var result = nativeConnection.Execute(text);
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_create", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr BulkCreate(IntPtr payload)
    {
        try
        {
            ClearError();
            var text = ReadUtf8(payload, nameof(payload));
            var bulk = new NativeBulk(text);
            return GCHandle.ToIntPtr(GCHandle.Alloc(bulk));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_set_measurement", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int BulkSetMeasurement(IntPtr bulk, IntPtr measurement)
    {
        try
        {
            ClearError();
            GetTarget<NativeBulk>(bulk, nameof(bulk)).SetMeasurement(ReadOptionalUtf8(measurement));
            return 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_set_onerror", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int BulkSetOnError(IntPtr bulk, IntPtr onError)
    {
        try
        {
            ClearError();
            GetTarget<NativeBulk>(bulk, nameof(bulk)).SetOnError(ReadOptionalUtf8(onError));
            return 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_set_flush", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int BulkSetFlush(IntPtr bulk, IntPtr flush)
    {
        try
        {
            ClearError();
            GetTarget<NativeBulk>(bulk, nameof(bulk)).SetFlush(ReadOptionalUtf8(flush));
            return 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_execute", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr BulkExecute(IntPtr connection, IntPtr bulk)
    {
        try
        {
            ClearError();
            var nativeConnection = GetTarget<NativeConnection>(connection, nameof(connection));
            var nativeBulk = GetTarget<NativeBulk>(bulk, nameof(bulk));
            var result = nativeConnection.ExecuteBulk(nativeBulk);
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_bulk_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void BulkFree(IntPtr bulk)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (bulk == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(bulk);
            hasHandle = true;
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_open", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr DocumentOpen(IntPtr connection, IntPtr collection)
    {
        try
        {
            ClearError();
            var nativeConnection = GetTarget<NativeConnection>(connection, nameof(connection));
            var collectionText = ReadUtf8(collection, nameof(collection));
            var document = new NativeDocument(nativeConnection, collectionText);
            return GCHandle.ToIntPtr(GCHandle.Alloc(document));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_close", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void DocumentClose(IntPtr document)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (document == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(document);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_create_collection", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr DocumentCreateCollection(IntPtr document, IntPtr optionsJson)
    {
        try
        {
            ClearError();
            var nativeDocument = GetTarget<NativeDocument>(document, nameof(document));
            var result = nativeDocument.CreateCollection(ReadOptionalUtf8(optionsJson));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_drop_collection", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int DocumentDropCollection(IntPtr document)
    {
        try
        {
            ClearError();
            return GetTarget<NativeDocument>(document, nameof(document)).DropCollection() ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_insert", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr DocumentInsert(IntPtr document, IntPtr payloadJson)
    {
        try
        {
            ClearError();
            var nativeDocument = GetTarget<NativeDocument>(document, nameof(document));
            var result = nativeDocument.Insert(ReadUtf8(payloadJson, nameof(payloadJson)));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_update", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr DocumentUpdate(IntPtr document, IntPtr payloadJson)
    {
        try
        {
            ClearError();
            var nativeDocument = GetTarget<NativeDocument>(document, nameof(document));
            var result = nativeDocument.Update(ReadUtf8(payloadJson, nameof(payloadJson)));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_delete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr DocumentDelete(IntPtr document, IntPtr payloadJson)
    {
        try
        {
            ClearError();
            var nativeDocument = GetTarget<NativeDocument>(document, nameof(document));
            var result = nativeDocument.Delete(ReadUtf8(payloadJson, nameof(payloadJson)));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_find_page", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr DocumentFindPage(IntPtr document, IntPtr payloadJson)
    {
        try
        {
            ClearError();
            var nativeDocument = GetTarget<NativeDocument>(document, nameof(document));
            var result = nativeDocument.FindPage(ReadOptionalUtf8(payloadJson));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_aggregate", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr DocumentAggregate(IntPtr document, IntPtr payloadJson)
    {
        try
        {
            ClearError();
            var nativeDocument = GetTarget<NativeDocument>(document, nameof(document));
            var result = nativeDocument.Aggregate(ReadUtf8(payloadJson, nameof(payloadJson)));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_result_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void DocumentResultFree(IntPtr result)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (result == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(result);
            hasHandle = true;
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_result_json_length", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int DocumentResultJsonLength(IntPtr result)
        => InvokeDocumentResult(result, static r => r.JsonLength, -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_doc_result_copy_json", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int DocumentResultCopyJson(IntPtr result, IntPtr buffer, int bufferLength)
        => InvokeDocumentResult(result, r => r.CopyJson(buffer, bufferLength), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_open", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectOpen(IntPtr connection, IntPtr bucket)
    {
        try
        {
            ClearError();
            var nativeConnection = GetTarget<NativeConnection>(connection, nameof(connection));
            var bucketText = ReadUtf8(bucket, nameof(bucket));
            var storage = new NativeObjectStorage(nativeConnection, bucketText);
            return GCHandle.ToIntPtr(GCHandle.Alloc(storage));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_close", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ObjectClose(IntPtr storage)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (storage == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(storage);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_list_buckets", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectListBuckets(IntPtr storage)
    {
        try
        {
            ClearError();
            var result = GetTarget<NativeObjectStorage>(storage, nameof(storage)).ListBuckets();
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_create_bucket", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectCreateBucket(IntPtr storage, IntPtr purpose)
    {
        try
        {
            ClearError();
            var result = GetTarget<NativeObjectStorage>(storage, nameof(storage))
                .CreateBucket(ReadOptionalUtf8(purpose));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_delete_bucket", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ObjectDeleteBucket(IntPtr storage)
    {
        try
        {
            ClearError();
            return GetTarget<NativeObjectStorage>(storage, nameof(storage)).DeleteBucket() ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_writer_create", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectWriterCreate(IntPtr contentType, IntPtr metadataJson, IntPtr tagsJson)
    {
        try
        {
            ClearError();
            var writer = new NativeObjectWriteBuffer(
                ReadOptionalUtf8(contentType),
                ReadOptionalUtf8(metadataJson),
                ReadOptionalUtf8(tagsJson));
            return GCHandle.ToIntPtr(GCHandle.Alloc(writer));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_writer_write", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ObjectWriterWrite(IntPtr writer, IntPtr buffer, int bufferLength)
    {
        try
        {
            ClearError();
            var nativeWriter = GetTarget<NativeObjectWriteBuffer>(writer, nameof(writer));
            nativeWriter.Write(ReadBytes(buffer, bufferLength, nameof(buffer)));
            return 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_writer_length", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long ObjectWriterLength(IntPtr writer)
        => InvokeObjectWriter(writer, static w => w.Length, -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_writer_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ObjectWriterFree(IntPtr writer)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (writer == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(writer);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_put", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectPut(IntPtr storage, IntPtr key, IntPtr writer)
    {
        try
        {
            ClearError();
            var nativeStorage = GetTarget<NativeObjectStorage>(storage, nameof(storage));
            var nativeWriter = GetTarget<NativeObjectWriteBuffer>(writer, nameof(writer));
            var result = nativeStorage.PutObject(ReadUtf8(key, nameof(key)), nativeWriter);
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_get", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectGet(IntPtr storage, IntPtr key, long offset, long length)
    {
        try
        {
            ClearError();
            var nativeStorage = GetTarget<NativeObjectStorage>(storage, nameof(storage));
            var reader = nativeStorage.OpenReader(ReadUtf8(key, nameof(key)), offset, length);
            return reader is null ? IntPtr.Zero : GCHandle.ToIntPtr(GCHandle.Alloc(reader));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_head", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectHead(IntPtr storage, IntPtr key)
    {
        try
        {
            ClearError();
            var nativeStorage = GetTarget<NativeObjectStorage>(storage, nameof(storage));
            var result = nativeStorage.HeadObject(ReadUtf8(key, nameof(key)));
            return result is null ? IntPtr.Zero : GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_list", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectList(IntPtr storage, IntPtr prefix, int maxKeys, IntPtr continuationToken)
    {
        try
        {
            ClearError();
            var nativeStorage = GetTarget<NativeObjectStorage>(storage, nameof(storage));
            var result = nativeStorage.ListObjects(
                ReadOptionalUtf8(prefix),
                maxKeys,
                ReadOptionalUtf8(continuationToken));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_delete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectDelete(IntPtr storage, IntPtr key)
    {
        try
        {
            ClearError();
            var nativeStorage = GetTarget<NativeObjectStorage>(storage, nameof(storage));
            var result = nativeStorage.DeleteObject(ReadUtf8(key, nameof(key)));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_delete_many", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectDeleteMany(IntPtr storage, IntPtr keysJson)
    {
        try
        {
            ClearError();
            var nativeStorage = GetTarget<NativeObjectStorage>(storage, nameof(storage));
            var result = nativeStorage.DeleteObjects(ReadUtf8(keysJson, nameof(keysJson)));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_multipart_initiate", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectMultipartInitiate(IntPtr storage, IntPtr key, IntPtr contentType, IntPtr metadataJson, IntPtr tagsJson)
    {
        try
        {
            ClearError();
            var nativeStorage = GetTarget<NativeObjectStorage>(storage, nameof(storage));
            var result = nativeStorage.InitiateMultipart(
                ReadUtf8(key, nameof(key)),
                ReadOptionalUtf8(contentType),
                ReadOptionalUtf8(metadataJson),
                ReadOptionalUtf8(tagsJson));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_multipart_upload_part", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectMultipartUploadPart(IntPtr storage, IntPtr key, IntPtr uploadId, int partNumber, IntPtr writer)
    {
        try
        {
            ClearError();
            var nativeStorage = GetTarget<NativeObjectStorage>(storage, nameof(storage));
            var nativeWriter = GetTarget<NativeObjectWriteBuffer>(writer, nameof(writer));
            var result = nativeStorage.UploadPart(
                ReadUtf8(key, nameof(key)),
                ReadUtf8(uploadId, nameof(uploadId)),
                partNumber,
                nativeWriter);
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_multipart_complete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectMultipartComplete(IntPtr storage, IntPtr key, IntPtr uploadId, IntPtr partNumbersJson)
    {
        try
        {
            ClearError();
            var nativeStorage = GetTarget<NativeObjectStorage>(storage, nameof(storage));
            var result = nativeStorage.CompleteMultipart(
                ReadUtf8(key, nameof(key)),
                ReadUtf8(uploadId, nameof(uploadId)),
                ReadUtf8(partNumbersJson, nameof(partNumbersJson)));
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_multipart_abort", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectMultipartAbort(IntPtr storage, IntPtr key, IntPtr uploadId)
    {
        try
        {
            ClearError();
            var nativeStorage = GetTarget<NativeObjectStorage>(storage, nameof(storage));
            var keyText = ReadUtf8(key, nameof(key));
            var uploadIdText = ReadUtf8(uploadId, nameof(uploadId));
            nativeStorage.AbortMultipart(keyText, uploadIdText);
            var result = NativeObjectResult.FromMultipartAbort(
                nativeStorage.Bucket,
                keyText,
                uploadIdText);
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_result_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ObjectResultFree(IntPtr result)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (result == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(result);
            hasHandle = true;
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_result_json_length", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ObjectResultJsonLength(IntPtr result)
        => InvokeObjectResult(result, static r => r.JsonLength, -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_result_copy_json", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ObjectResultCopyJson(IntPtr result, IntPtr buffer, int bufferLength)
        => InvokeObjectResult(result, r => r.CopyJson(buffer, bufferLength), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_reader_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ObjectReaderFree(IntPtr reader)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (reader == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(reader);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_reader_read", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ObjectReaderRead(IntPtr reader, IntPtr buffer, int bufferLength)
        => InvokeObjectReader(reader, r => r.Read(buffer, bufferLength), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_reader_bucket", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectReaderBucket(IntPtr reader)
        => InvokeObjectReader(reader, static r => r.GetBucket(), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_reader_key", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectReaderKey(IntPtr reader)
        => InvokeObjectReader(reader, static r => r.GetKey(), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_reader_content_type", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectReaderContentType(IntPtr reader)
        => InvokeObjectReader(reader, static r => r.GetContentType(), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_reader_etag", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectReaderETag(IntPtr reader)
        => InvokeObjectReader(reader, static r => r.GetETag(), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_reader_sha256", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ObjectReaderSha256(IntPtr reader)
        => InvokeObjectReader(reader, static r => r.GetSha256(), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_reader_size_bytes", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long ObjectReaderSizeBytes(IntPtr reader)
        => InvokeObjectReader(reader, static r => r.GetSizeBytes(), -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_reader_offset", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long ObjectReaderOffset(IntPtr reader)
        => InvokeObjectReader(reader, static r => r.Offset, -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_reader_length", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long ObjectReaderLength(IntPtr reader)
        => InvokeObjectReader(reader, static r => r.Length, -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_obj_reader_is_range", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ObjectReaderIsRange(IntPtr reader)
        => InvokeObjectReader(reader, static r => r.IsRange, -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_open", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr MessageQueueOpen(IntPtr connection, IntPtr topic)
    {
        try
        {
            ClearError();
            var nativeConnection = GetTarget<NativeConnection>(connection, nameof(connection));
            var topicText = ReadUtf8(topic, nameof(topic));
            var queue = new NativeMessageQueue(nativeConnection, topicText);
            return GCHandle.ToIntPtr(GCHandle.Alloc(queue));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_close", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void MessageQueueClose(IntPtr queue)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (queue == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(queue);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_publish", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long MessageQueuePublish(IntPtr queue, IntPtr payload, int payloadLength, IntPtr headersJson)
    {
        try
        {
            ClearError();
            var nativeQueue = GetTarget<NativeMessageQueue>(queue, nameof(queue));
            return nativeQueue.Publish(
                ReadBytes(payload, payloadLength, nameof(payload)),
                ReadOptionalUtf8(headersJson));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_pull", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr MessageQueuePull(IntPtr queue, IntPtr consumerGroup, int maxCount)
    {
        try
        {
            ClearError();
            var nativeQueue = GetTarget<NativeMessageQueue>(queue, nameof(queue));
            var pull = nativeQueue.Pull(ReadUtf8(consumerGroup, nameof(consumerGroup)), maxCount);
            return GCHandle.ToIntPtr(GCHandle.Alloc(pull));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_ack", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long MessageQueueAck(IntPtr queue, IntPtr consumerGroup, long offset)
    {
        try
        {
            ClearError();
            var nativeQueue = GetTarget<NativeMessageQueue>(queue, nameof(queue));
            return nativeQueue.Ack(ReadUtf8(consumerGroup, nameof(consumerGroup)), offset);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_stats", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr MessageQueueStats(IntPtr queue)
    {
        try
        {
            ClearError();
            var result = GetTarget<NativeMessageQueue>(queue, nameof(queue)).GetStats();
            return GCHandle.ToIntPtr(GCHandle.Alloc(result));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_pull_result_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void MessageQueuePullResultFree(IntPtr pull)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (pull == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(pull);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_pull_result_message_count", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int MessageQueuePullResultMessageCount(IntPtr pull)
        => InvokeMessageQueuePull(pull, static p => p.MessageCount, -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_pull_next", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int MessageQueuePullNext(IntPtr pull)
        => InvokeMessageQueuePull(pull, static p => p.MoveNext(), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_pull_topic", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr MessageQueuePullTopic(IntPtr pull)
        => InvokeMessageQueueMessage(pull, static m => m.GetTopic(), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_pull_offset", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long MessageQueuePullOffset(IntPtr pull)
        => InvokeMessageQueueMessage(pull, static m => m.Offset, -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_pull_timestamp_unix_ms", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long MessageQueuePullTimestampUnixMs(IntPtr pull)
        => InvokeMessageQueueMessage(pull, static m => m.GetTimestampUnixMs(), -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_pull_headers_json_length", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int MessageQueuePullHeadersJsonLength(IntPtr pull)
        => InvokeMessageQueueMessage(pull, static m => m.GetHeadersJsonLength(), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_pull_copy_headers_json", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int MessageQueuePullCopyHeadersJson(IntPtr pull, IntPtr buffer, int bufferLength)
        => InvokeMessageQueueMessage(pull, m => m.CopyHeadersJson(buffer, bufferLength), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_pull_payload_length", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long MessageQueuePullPayloadLength(IntPtr pull)
        => InvokeMessageQueueMessage(pull, static m => m.GetPayloadLength(), -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_pull_copy_payload", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int MessageQueuePullCopyPayload(IntPtr pull, IntPtr buffer, int bufferLength)
        => InvokeMessageQueueMessage(pull, m => m.CopyPayload(buffer, bufferLength), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_result_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void MessageQueueResultFree(IntPtr result)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (result == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(result);
            hasHandle = true;
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_result_json_length", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int MessageQueueResultJsonLength(IntPtr result)
        => InvokeMessageQueueResult(result, static r => r.JsonLength, -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_mq_result_copy_json", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int MessageQueueResultCopyJson(IntPtr result, IntPtr buffer, int bufferLength)
        => InvokeMessageQueueResult(result, r => r.CopyJson(buffer, bufferLength), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_open", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr KvOpen(IntPtr connection, IntPtr keyspace, IntPtr @namespace)
    {
        try
        {
            ClearError();
            var nativeConnection = GetTarget<NativeConnection>(connection, nameof(connection));
            var keyspaceText = ReadUtf8(keyspace, nameof(keyspace));
            var namespaceText = ReadOptionalUtf8(@namespace);
            var kv = new NativeKv(nativeConnection, keyspaceText, namespaceText);
            return GCHandle.ToIntPtr(GCHandle.Alloc(kv));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_close", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void KvClose(IntPtr kv)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (kv == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(kv);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_get", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr KvGet(IntPtr kv, IntPtr key)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var entry = nativeKv.Get(ReadUtf8(key, nameof(key)));
            return entry is null ? IntPtr.Zero : GCHandle.ToIntPtr(GCHandle.Alloc(entry));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_set", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvSet(IntPtr kv, IntPtr key, IntPtr value, int valueLength, long expiresAtUnixMs)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var keyText = ReadUtf8(key, nameof(key));
            var valueBytes = ReadBytes(value, valueLength, nameof(value));
            return nativeKv.Set(keyText, valueBytes, FromOptionalUnixMilliseconds(expiresAtUnixMs));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_delete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvDelete(IntPtr kv, IntPtr key)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            return nativeKv.Delete(ReadUtf8(key, nameof(key))) ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_prefix", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr KvScanPrefix(IntPtr kv, IntPtr prefix, int limit)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var scan = nativeKv.ScanPrefix(ReadUtf8(prefix, nameof(prefix)), limit);
            return GCHandle.ToIntPtr(GCHandle.Alloc(scan));
        }
        catch (Exception ex)
        {
            SetError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_ttl", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvTimeToLive(IntPtr kv, IntPtr key, IntPtr expiresAtUnixMs)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var ttl = nativeKv.GetTimeToLive(ReadUtf8(key, nameof(key)));
            WriteInt64(expiresAtUnixMs, ttl.ExpiresAtUtc?.ToUnixTimeMilliseconds() ?? -1);
            return ttl.Milliseconds;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -3;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_expire_at", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvExpireAt(IntPtr kv, IntPtr key, long expiresAtUnixMs)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            if (expiresAtUnixMs < 0)
                throw new ArgumentOutOfRangeException(nameof(expiresAtUnixMs), "expires_at_unix_ms must be non-negative.");

            return nativeKv.Expire(ReadUtf8(key, nameof(key)), DateTimeOffset.FromUnixTimeMilliseconds(expiresAtUnixMs)) ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_persist", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvPersist(IntPtr kv, IntPtr key)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            return nativeKv.Persist(ReadUtf8(key, nameof(key))) ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_incr", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvIncrement(IntPtr kv, IntPtr key, long delta, IntPtr value, IntPtr version)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var result = nativeKv.Increment(ReadUtf8(key, nameof(key)), delta);
            WriteInt64(value, result.Value);
            WriteInt64(version, result.Version);
            return 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_cas", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvCompareAndSet(
        IntPtr kv,
        IntPtr key,
        long expectedVersion,
        IntPtr value,
        int valueLength,
        long expiresAtUnixMs,
        IntPtr currentVersion,
        IntPtr newVersion)
    {
        try
        {
            ClearError();
            var nativeKv = GetTarget<NativeKv>(kv, nameof(kv));
            var result = nativeKv.CompareAndSet(
                ReadUtf8(key, nameof(key)),
                expectedVersion,
                ReadBytes(value, valueLength, nameof(value)),
                FromOptionalUnixMilliseconds(expiresAtUnixMs));
            WriteInt64(currentVersion, result.CurrentVersion);
            WriteInt64(newVersion, result.NewVersion ?? -1);
            return result.Succeeded ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void KvEntryFree(IntPtr entry)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (entry == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(entry);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_key", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr KvEntryKey(IntPtr entry)
        => InvokeKvEntry(entry, static e => e.GetKey(), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_value_length", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvEntryValueLength(IntPtr entry)
        => InvokeKvEntry(entry, static e => e.GetValueLength(), -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_copy_value", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvEntryCopyValue(IntPtr entry, IntPtr buffer, int bufferLength)
        => InvokeKvEntry(entry, e => e.CopyValue(buffer, bufferLength), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_version", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvEntryVersion(IntPtr entry)
        => InvokeKvEntry(entry, static e => e.Version, -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_entry_expires_at_unix_ms", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvEntryExpiresAtUnixMs(IntPtr entry)
        => InvokeKvEntry(entry, static e => e.GetExpiresAtUnixMilliseconds(), -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_next", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvScanNext(IntPtr scan)
    {
        try
        {
            ClearError();
            return GetTarget<NativeKvScan>(scan, nameof(scan)).MoveNext();
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_key", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr KvScanKey(IntPtr scan)
        => InvokeKvScan(scan, static s => s.Current.GetKey(), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_value_length", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvScanValueLength(IntPtr scan)
        => InvokeKvScan(scan, static s => s.Current.GetValueLength(), -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_copy_value", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int KvScanCopyValue(IntPtr scan, IntPtr buffer, int bufferLength)
        => InvokeKvScan(scan, s => s.Current.CopyValue(buffer, bufferLength), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_version", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvScanVersion(IntPtr scan)
        => InvokeKvScan(scan, static s => s.Current.Version, -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_expires_at_unix_ms", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long KvScanExpiresAtUnixMs(IntPtr scan)
        => InvokeKvScan(scan, static s => s.Current.GetExpiresAtUnixMilliseconds(), -1L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_kv_scan_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void KvScanFree(IntPtr scan)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (scan == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(scan);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ResultFree(IntPtr result)
    {
        GCHandle handle = default;
        bool hasHandle = false;

        try
        {
            ClearError();
            if (result == IntPtr.Zero)
                return;

            handle = GCHandle.FromIntPtr(result);
            hasHandle = true;
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            if (hasHandle)
                handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_records_affected", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ResultRecordsAffected(IntPtr result)
        => Invoke(result, static r => r.RecordsAffected, -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_column_count", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ResultColumnCount(IntPtr result)
        => Invoke(result, static r => r.ColumnCount, -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_column_name", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ResultColumnName(IntPtr result, int ordinal)
        => Invoke(result, r => r.GetColumnName(ordinal), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_next", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ResultNext(IntPtr result)
        => Invoke(result, static r => r.MoveNext(), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_value_type", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ResultValueType(IntPtr result, int ordinal)
        => Invoke(result, r => (int)r.GetValueType(ordinal), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_value_int64", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long ResultValueInt64(IntPtr result, int ordinal)
        => Invoke(result, r => r.GetInt64(ordinal), 0L);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_value_double", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static double ResultValueDouble(IntPtr result, int ordinal)
        => Invoke(result, r => r.GetDouble(ordinal), 0d);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_value_bool", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ResultValueBool(IntPtr result, int ordinal)
        => Invoke(result, r => r.GetBoolean(ordinal), -1);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_result_value_text", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ResultValueText(IntPtr result, int ordinal)
        => Invoke(result, r => r.GetText(ordinal), IntPtr.Zero);

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_flush", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Flush(IntPtr connection)
    {
        try
        {
            ClearError();
            GetTarget<NativeConnection>(connection, nameof(connection)).Flush();
            return 0;
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_version", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Version(IntPtr buffer, int bufferLength)
    {
        try
        {
            ClearError();
            var version = typeof(SndbConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(SndbConnection).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
            return CopyUtf8(version, buffer, bufferLength);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "sonnetdb_last_error", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int LastError(IntPtr buffer, int bufferLength)
        => CopyUtf8(s_lastError ?? string.Empty, buffer, bufferLength);

    private static TReturn Invoke<TReturn>(IntPtr result, Func<NativeResult, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativeResult = GetTarget<NativeResult>(result, nameof(result));
            return action(nativeResult);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static TReturn InvokeKvEntry<TReturn>(IntPtr entry, Func<NativeKvEntry, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativeEntry = GetTarget<NativeKvEntry>(entry, nameof(entry));
            return action(nativeEntry);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static TReturn InvokeDocumentResult<TReturn>(IntPtr result, Func<NativeDocumentResult, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativeResult = GetTarget<NativeDocumentResult>(result, nameof(result));
            return action(nativeResult);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static TReturn InvokeObjectResult<TReturn>(IntPtr result, Func<NativeObjectResult, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativeResult = GetTarget<NativeObjectResult>(result, nameof(result));
            return action(nativeResult);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static TReturn InvokeObjectReader<TReturn>(IntPtr reader, Func<NativeObjectReader, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativeReader = GetTarget<NativeObjectReader>(reader, nameof(reader));
            return action(nativeReader);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static TReturn InvokeObjectWriter<TReturn>(IntPtr writer, Func<NativeObjectWriteBuffer, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativeWriter = GetTarget<NativeObjectWriteBuffer>(writer, nameof(writer));
            return action(nativeWriter);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static TReturn InvokeMessageQueuePull<TReturn>(IntPtr pull, Func<NativeMessageQueuePull, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativePull = GetTarget<NativeMessageQueuePull>(pull, nameof(pull));
            return action(nativePull);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static TReturn InvokeMessageQueueMessage<TReturn>(IntPtr pull, Func<NativeMessageQueueMessage, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativePull = GetTarget<NativeMessageQueuePull>(pull, nameof(pull));
            return action(nativePull.Current);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static TReturn InvokeMessageQueueResult<TReturn>(IntPtr result, Func<NativeMessageQueueResult, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativeResult = GetTarget<NativeMessageQueueResult>(result, nameof(result));
            return action(nativeResult);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static TReturn InvokeKvScan<TReturn>(IntPtr scan, Func<NativeKvScan, TReturn> action, TReturn errorValue)
    {
        try
        {
            ClearError();
            var nativeScan = GetTarget<NativeKvScan>(scan, nameof(scan));
            return action(nativeScan);
        }
        catch (Exception ex)
        {
            SetError(ex);
            return errorValue;
        }
    }

    private static T GetTarget<T>(IntPtr handle, string parameterName)
        where T : class
    {
        if (handle == IntPtr.Zero)
            throw new ArgumentNullException(parameterName);

        var gcHandle = GCHandle.FromIntPtr(handle);
        return gcHandle.Target as T
            ?? throw new InvalidOperationException($"Native handle '{parameterName}' has an unexpected target type.");
    }

    private static string ReadUtf8(IntPtr pointer, string parameterName)
    {
        if (pointer == IntPtr.Zero)
            throw new ArgumentNullException(parameterName);

        return Marshal.PtrToStringUTF8(pointer)
            ?? throw new ArgumentException("UTF-8 string pointer is invalid.", parameterName);
    }

    private static string? ReadOptionalUtf8(IntPtr pointer)
        => pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);

    private static byte[] ReadBytes(IntPtr pointer, int length, string parameterName)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Byte length cannot be negative.");
        if (length == 0)
            return Array.Empty<byte>();
        if (pointer == IntPtr.Zero)
            throw new ArgumentNullException(parameterName);

        byte[] bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return bytes;
    }

    private static DateTimeOffset? FromOptionalUnixMilliseconds(long value)
        => value < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static void WriteInt64(IntPtr pointer, long value)
    {
        if (pointer != IntPtr.Zero)
            Marshal.WriteInt64(pointer, value);
    }

    public static int CopyUtf8(string value, IntPtr buffer, int bufferLength)
    {
        if (buffer == IntPtr.Zero || bufferLength <= 0)
            return Encoding.UTF8.GetByteCount(value);

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        int copyLength = Math.Min(bytes.Length, bufferLength - 1);
        if (copyLength > 0)
            Marshal.Copy(bytes, 0, buffer, copyLength);
        Marshal.WriteByte(buffer, copyLength, 0);
        return bytes.Length;
    }

    public static int CopyBytes(byte[] value, IntPtr buffer, int bufferLength)
    {
        if (bufferLength < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferLength), "Buffer length cannot be negative.");
        if (buffer == IntPtr.Zero || bufferLength == 0)
            return value.Length;

        int copyLength = Math.Min(value.Length, bufferLength);
        if (copyLength > 0)
            Marshal.Copy(value, 0, buffer, copyLength);
        return value.Length;
    }

    private static void ClearError()
    {
        s_lastError = null;
    }

    private static void SetError(Exception exception)
    {
        s_lastError = exception.Message;
    }
}
