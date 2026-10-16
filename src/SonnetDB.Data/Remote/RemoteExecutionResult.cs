using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using SonnetDB.Data.Internal;
using SonnetDB.Model;
using SonnetDB.Protocol;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Data.Remote;

/// <summary>
/// 远程执行结果：把 ndjson 流逐行解析为列名 + 当前行值。
/// </summary>
/// <remarks>
/// <para>
/// 协议（来自 <c>SqlEndpointHandler</c>）：
/// 第一行 meta：<c>{"type":"meta","columns":[...]}</c>；
/// 中间若干行：JSON 数组 <c>[v0, v1, ...]</c>；
/// 末行 end：<c>{"type":"end","rowCount":N,"recordsAffected":M,"elapsedMilliseconds":...}</c>；
/// 任意位置可能出现错误行 <c>{"error":"...","message":"..."}</c>。
/// </para>
/// <para>对非 SELECT 语句，meta.columns 为空，仅末行 end 给出 recordsAffected。</para>
/// </remarks>
internal sealed class RemoteExecutionResult : IExecutionResult
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly string[] _columns;
    private readonly ExecutionFieldTypeKind[] _columnTypes;
    private readonly ExecutionColumnMetadata[]? _columnMetadata;
    private readonly bool _inferMissingColumnTypes;
    private readonly bool _nativeDateTime;
    private readonly bool _nativeDuration;
    private object?[] _currentRow;
    private bool _ended;
    private long _rowsRead;

    public int RecordsAffected { get; private set; }

    public bool Truncated { get; private set; }

    public IReadOnlyList<string> Columns => _columns;

    private RemoteExecutionResult(HttpResponseMessage response, Stream stream, StreamReader reader,
        string[] columns, ExecutionFieldTypeKind[] columnTypes,
        ExecutionColumnMetadata[]? columnMetadata, bool inferMissingColumnTypes)
    {
        _response = response;
        _stream = stream;
        _reader = reader;
        _columns = columns;
        _columnTypes = columnTypes;
        _columnMetadata = columnMetadata;
        _inferMissingColumnTypes = inferMissingColumnTypes;
        _nativeDateTime = HasNativeDateTimeResult(response);
        _nativeDuration = HasNativeDurationResult(response);
        if (!_nativeDuration && columnTypes.Contains(ExecutionFieldTypeKind.Duration))
            throw new InvalidDataException("远程 DURATION 声明缺少 SQL v5 协商确认。");
        _currentRow = new object?[columns.Length];
        RecordsAffected = -1; // SELECT 默认；非 SELECT 在末行被覆盖
    }

    public bool ReadNextRow()
    {
        if (_ended) return false;
        while (true)
        {
            var line = _reader.ReadLine();
            if (line is null)
            {
                _ended = true;
                throw new InvalidDataException("远程 SQL 响应缺少 end 标记，不能确认结果完整。");
            }
            if (line.Length == 0) continue;

            if (ProcessLine(line))
                return true;
            if (_ended)
                return false;
        }
    }

    public async ValueTask<bool> ReadNextRowAsync(CancellationToken cancellationToken)
    {
        if (_ended) return false;
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                _ended = true;
                throw new InvalidDataException("远程 SQL 响应缺少 end 标记，不能确认结果完整。");
            }
            if (line.Length == 0) continue;

            if (ProcessLine(line))
                return true;
            if (_ended)
                return false;
        }
    }

    public object? GetValue(int ordinal) => _currentRow[ordinal];

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
    public Type GetFieldType(int ordinal)
    {
        var kind = _columnTypes[ordinal];
        if (kind == ExecutionFieldTypeKind.Object && _inferMissingColumnTypes)
            kind = ExecutionFieldTypeResolver.Resolve(_currentRow[ordinal]);
        return ExecutionFieldTypeResolver.GetRuntimeType(kind);
    }

    public ExecutionColumnMetadata? GetColumnMetadata(int ordinal) => _columnMetadata?[ordinal];

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
        _response.Dispose();
    }

    private bool ProcessLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            int n = root.GetArrayLength();
            if (n != _columns.Length)
                throw new InvalidDataException($"ndjson 行列数 ({n}) 与 meta ({_columns.Length}) 不一致。");
            for (int i = 0; i < n; i++)
                _currentRow[i] = ReadTypedScalar(root[i], _columnTypes[i], _nativeDateTime, _nativeDuration);
            _rowsRead++;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
            {
                var type = typeProp.GetString();
                if (type == "end")
                {
                    if (!root.TryGetProperty("rowCount", out var rowCount)
                        || rowCount.ValueKind != JsonValueKind.Number
                        || !rowCount.TryGetInt64(out long reportedRows)
                        || reportedRows != _rowsRead)
                        throw new InvalidDataException("远程 SQL end 行数与实际结果不一致。");
                    if (root.TryGetProperty("recordsAffected", out var ra) && ra.ValueKind == JsonValueKind.Number)
                        RecordsAffected = ra.GetInt32();
                    Truncated = ReadTruncated(root);
                    _ended = true;
                    return false;
                }
                if (type == "meta")
                    return false;
            }

            if (root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String)
            {
                var error = errProp.GetString() ?? "sql_error";
                var message = root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String
                    ? msgProp.GetString() ?? string.Empty
                    : string.Empty;
                _ended = true;
                throw new SndbServerException(error, message, System.Net.HttpStatusCode.OK);
            }
        }

        return false;
    }

    /// <summary>
    /// 异步创建实例：先消费 meta 行（或直接读取 end 行用于非 SELECT），并允许命令超时取消首包等待。
    /// </summary>
    public static async Task<RemoteExecutionResult> CreateAsync(
        HttpResponseMessage response,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        try
        {
            string[] columns = Array.Empty<string>();
            ExecutionFieldTypeKind[] columnTypes = Array.Empty<ExecutionFieldTypeKind>();
            ExecutionColumnMetadata[]? columnMetadata = null;
            bool inferMissingColumnTypes = true;
            int recordsAffected = -1;
            bool pendingTruncated = false;
            bool sawMeta = false;
            bool ended = false;

            while (!ended)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) break;

                if (root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String)
                {
                    var error = errProp.GetString() ?? "sql_error";
                    var message = root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String
                        ? msgProp.GetString() ?? string.Empty
                        : string.Empty;
                    throw new SndbServerException(error, message, System.Net.HttpStatusCode.OK);
                }

                if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
                {
                    var type = typeProp.GetString();
                    if (type == "meta")
                    {
                        sawMeta = true;
                        if (root.TryGetProperty("columns", out var colsProp) && colsProp.ValueKind == JsonValueKind.Array)
                        {
                            var list = new List<string>(colsProp.GetArrayLength());
                            foreach (var c in colsProp.EnumerateArray())
                                list.Add(c.GetString() ?? string.Empty);
                            columns = [.. list];
                            columnTypes = new ExecutionFieldTypeKind[columns.Length];
                            if (root.TryGetProperty("columnTypes", out var typesProp))
                            {
                                inferMissingColumnTypes = false;
                                if (typesProp.ValueKind != JsonValueKind.Array
                                    || typesProp.GetArrayLength() != columns.Length)
                                    throw new InvalidDataException("远程 SQL meta 列类型与列名数量不一致。");
                                for (int i = 0; i < columnTypes.Length; i++)
                                    columnTypes[i] = ReadColumnType(typesProp[i]);
                            }
                            if (root.TryGetProperty("columnSchemas", out var schemasProp))
                            {
                                inferMissingColumnTypes = false;
                                if (schemasProp.ValueKind != JsonValueKind.Array
                                    || schemasProp.GetArrayLength() != columns.Length)
                                    throw new InvalidDataException("远程 SQL meta 列 schema 与列名数量不一致。");
                                columnMetadata = new ExecutionColumnMetadata[columns.Length];
                                for (int i = 0; i < columnMetadata.Length; i++)
                                {
                                    var schema = schemasProp[i];
                                    if (schema.ValueKind != JsonValueKind.Object
                                        || !schema.TryGetProperty("dataType", out var dataType))
                                        throw new InvalidDataException("远程 SQL meta 列 schema 缺少 dataType。");
                                    columnTypes[i] = ReadColumnType(dataType);
                                    columnMetadata[i] = new ExecutionColumnMetadata(
                                        ReadSchemaBoolean(schema, "isNullable"),
                                        ReadSchemaBoolean(schema, "isKey"),
                                        ReadSchemaBoolean(schema, "isAutoIncrement"),
                                        ReadSchemaBoolean(schema, "isRowVersion"))
                                    {
                                        DataType = dataType.GetString() switch
                                        {
                                            "int64" => TableColumnType.Int64,
                                            "float64" => TableColumnType.Float64,
                                            "decimal" => TableColumnType.Decimal,
                                            "boolean" => TableColumnType.Boolean,
                                            "string" => TableColumnType.String,
                                            "datetime" => TableColumnType.DateTime,
                                            "time" => TableColumnType.Time,
                                            "duration" => TableColumnType.Duration,
                                            "blob" => TableColumnType.Blob,
                                            "json" => TableColumnType.Json,
                                            _ => null,
                                        },
                                        NumericPrecision = ReadOptionalByte(schema, "numericPrecision"),
                                        NumericScale = ReadOptionalByte(schema, "numericScale"),
                                        ColumnSize = schema.TryGetProperty("columnSize", out var size)
                                            && size.ValueKind != JsonValueKind.Null ? size.GetInt32() : null,
                                        DeclaredNullable = schema.TryGetProperty("declaredNullable", out var nullable)
                                            ? nullable.ValueKind == JsonValueKind.Null ? null : nullable.GetBoolean()
                                            : ReadSchemaBoolean(schema, "isNullable"),
                                        RequiresRefresh = schema.TryGetProperty("requiresRefresh", out var refresh)
                                            && refresh.GetBoolean(),
                                    };
                                }
                            }
                        }

                        // 非 SELECT：columns 为空，紧接着应是 end；继续循环消费 end。
                        if (columns.Length == 0) continue;
                        break; // SELECT：meta 之后就是行数据，交给 ReadNextRow 处理。
                    }
                    if (type == "end")
                    {
                        if (root.TryGetProperty("recordsAffected", out var ra) && ra.ValueKind == JsonValueKind.Number)
                            recordsAffected = ra.GetInt32();
                        bool truncated = ReadTruncated(root);
                        ended = true;
                        pendingTruncated = truncated;
                        break;
                    }
                }
            }

            if (!sawMeta && !ended)
                throw new InvalidDataException("远程响应缺少 meta 或 end 行。");

            var result = new RemoteExecutionResult(response, stream, reader,
                columns, columnTypes, columnMetadata, inferMissingColumnTypes)
            {
                Truncated = pendingTruncated,
                _ended = ended,
            };
            if (ended)
                result.RecordsAffected = recordsAffected;
            return result;
        }
        catch
        {
            // 创建失败时读取器尚未移交给结果对象，必须在这里释放整条 HTTP 响应链。
            reader.Dispose();
            response.Dispose();
            throw;
        }
    }

    internal static bool ReadTruncated(JsonElement root)
    {
        if (!root.TryGetProperty("truncated", out var truncated))
            return false;
        if (truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("远程 SQL 截断标记必须是布尔值。");
        return truncated.GetBoolean();
    }

    private static byte? ReadOptionalByte(JsonElement schema, string name)
        => schema.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetByte() : null;

    internal static object? ReadScalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => ReadNumber(element),
        JsonValueKind.Array => ReadNumericArray(element),
        JsonValueKind.Object => TryReadGeoPoint(element, out var point) ? point : element.GetRawText(),
        _ => null,
    };

    internal static object? ReadScalar(JsonElement element, string? columnType, bool nativeDateTime = false, bool nativeDuration = false)
        => ReadTypedScalar(element, columnType switch
        {
            "float64" => ExecutionFieldTypeKind.Double,
            "decimal" => ExecutionFieldTypeKind.Decimal,
            "datetime" => ExecutionFieldTypeKind.DateTime,
            "time" => ExecutionFieldTypeKind.TimeOnly,
            "duration" => ExecutionFieldTypeKind.Duration,
            "blob" => ExecutionFieldTypeKind.ByteArray,
            _ => ExecutionFieldTypeKind.Object,
        }, nativeDateTime, nativeDuration);

    internal static bool HasNativeDateTimeResult(HttpResponseMessage response)
        => response.Headers.TryGetValues(SqlFrameCodec.ResultVersionHeader, out var versions)
            && (versions.SequenceEqual([SqlFrameCodec.NativeDateTimeResultVersion])
                || versions.SequenceEqual([SqlFrameCodec.NativeDurationResultVersion]));

    internal static bool HasNativeDurationResult(HttpResponseMessage response)
        => response.Headers.TryGetValues(SqlFrameCodec.ResultVersionHeader, out var versions)
            && versions.SequenceEqual([SqlFrameCodec.NativeDurationResultVersion]);

    internal static SelectColumnInfo[]? ReadColumnDeclarations(JsonElement meta, int columnCount)
    {
        if (!meta.TryGetProperty("columnSchemas", out var schemas))
            return null;
        if (schemas.ValueKind != JsonValueKind.Array || schemas.GetArrayLength() != columnCount)
            throw new InvalidDataException("远程事务预览列声明与列名数量不一致。");
        var declarations = new SelectColumnInfo[columnCount];
        for (int index = 0; index < columnCount; index++)
        {
            var schema = schemas[index];
            if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("dataType", out var type)
                || type.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("远程事务预览列声明缺少 dataType。");
            declarations[index] = new SelectColumnInfo(type.GetString() switch
            {
                "int64" => TableColumnType.Int64,
                "float64" => TableColumnType.Float64,
                "decimal" => TableColumnType.Decimal,
                "boolean" => TableColumnType.Boolean,
                "string" => TableColumnType.String,
                "datetime" => TableColumnType.DateTime,
                "time" => TableColumnType.Time,
                "duration" => TableColumnType.Duration,
                "blob" => TableColumnType.Blob,
                "json" => TableColumnType.Json,
                _ => (TableColumnType?)null,
            }, schema.TryGetProperty("declaredNullable", out var nullable)
                ? nullable.ValueKind == JsonValueKind.Null ? null : nullable.GetBoolean()
                : ReadSchemaBoolean(schema, "isNullable"),
                ReadSchemaBoolean(schema, "isKey"), ReadSchemaBoolean(schema, "isAutoIncrement"),
                ReadSchemaBoolean(schema, "isRowVersion"))
            {
                NumericPrecision = ReadOptionalByte(schema, "numericPrecision"),
                NumericScale = ReadOptionalByte(schema, "numericScale"),
                ColumnSize = schema.TryGetProperty("columnSize", out var size) && size.ValueKind != JsonValueKind.Null
                    ? size.GetInt32() : null,
                RequiresRefresh = schema.TryGetProperty("requiresRefresh", out var refresh) && refresh.GetBoolean(),
            };
        }
        return declarations;
    }

    private static object? ReadTypedScalar(JsonElement element, ExecutionFieldTypeKind type, bool nativeDateTime, bool nativeDuration)
    {
        if (type == ExecutionFieldTypeKind.Duration || element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("$type", out var durationType) && durationType.ValueKind == JsonValueKind.String
            && durationType.GetString() == "duration")
        {
            if (!nativeDuration)
                throw new InvalidDataException("远程 DURATION 值缺少 SQL v5 协商确认。");
            if (element.ValueKind == JsonValueKind.Null)
                return null;
            if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != 2
                || !element.TryGetProperty("$type", out var tag) || tag.ValueKind != JsonValueKind.String
                || tag.GetString() != "duration"
                || !element.TryGetProperty("ticks", out var tickValue) || tickValue.ValueKind != JsonValueKind.Number
                || !tickValue.TryGetInt64(out long ticks))
                throw new InvalidDataException("远程 DURATION 值必须包含完整的有符号 Int64 ticks。");
            return TimeSpan.FromTicks(ticks);
        }
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        if (nativeDateTime && (type == ExecutionFieldTypeKind.DateTime
            || element.ValueKind == JsonValueKind.Object && element.TryGetProperty("$type", out var temporalType)
                && temporalType.ValueKind == JsonValueKind.String && temporalType.GetString() == "datetime"))
        {
            if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != 3
                || !element.TryGetProperty("$type", out var tag) || tag.ValueKind != JsonValueKind.String
                || tag.GetString() != "datetime"
                || !element.TryGetProperty("ticks", out var ticksElement) || ticksElement.ValueKind != JsonValueKind.Number
                || !ticksElement.TryGetInt64(out long ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                || !element.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.Number
                || !kindElement.TryGetInt32(out int kind) || kind is < 0 or > 2)
                throw new InvalidDataException("远程 SQL v4 原生日期值的 ticks 或 Kind 无效。");
            return new DateTime(ticks, (DateTimeKind)kind);
        }
        if (type == ExecutionFieldTypeKind.Decimal)
            return ReadDecimal(element);
        if (type == ExecutionFieldTypeKind.Double)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double value))
                return value;
            throw new InvalidDataException("远程 SQL FLOAT 值无法解析为 Float64。");
        }
        if (type == ExecutionFieldTypeKind.DateTime)
        {
            if (element.ValueKind == JsonValueKind.String && element.TryGetDateTime(out var dateTime))
                return dateTime;
            throw new InvalidDataException("远程 SQL DATETIME 值不是有效的时间戳。");
        }
        if (type == ExecutionFieldTypeKind.TimeOnly)
        {
            if (element.ValueKind == JsonValueKind.String
                && TimeOnly.TryParse(element.GetString(), CultureInfo.InvariantCulture, out var time))
                return time;
            throw new InvalidDataException("远程 SQL TIME 值不是有效的时间值。");
        }
        if (type == ExecutionFieldTypeKind.ByteArray)
        {
            if (element.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("远程 SQL BLOB 值不是 Base64 文本。");
            try { return element.GetBytesFromBase64(); }
            catch (FormatException exception)
            {
                throw new InvalidDataException("远程 SQL BLOB 值不是有效的 Base64 文本。", exception);
            }
        }
        return ReadScalar(element);
    }

    private static bool TryReadGeoPoint(JsonElement element, out GeoPoint point)
    {
        point = default;
        if (!element.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "Point", StringComparison.Ordinal))
        {
            return false;
        }

        if (!element.TryGetProperty("coordinates", out var coordinates)
            || coordinates.ValueKind != JsonValueKind.Array
            || coordinates.GetArrayLength() < 2)
        {
            return false;
        }

        var lonElement = coordinates[0];
        var latElement = coordinates[1];
        if (lonElement.ValueKind != JsonValueKind.Number
            || latElement.ValueKind != JsonValueKind.Number
            || !lonElement.TryGetDouble(out var lon)
            || !latElement.TryGetDouble(out var lat))
        {
            return false;
        }

        point = GeoPoint.Create(lat, lon);
        return true;
    }

    private static object ReadNumericArray(JsonElement element)
    {
        var vector = new float[element.GetArrayLength()];
        int index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out vector[index]))
                return element.GetRawText();
            index++;
        }

        return vector;
    }

    private static object ReadNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var i64)) return i64;
        var raw = element.GetRawText();
        if (raw.IndexOfAny('.', 'e', 'E') < 0)
            throw new InvalidDataException($"远程 SQL 整数超出 Int64 范围：{raw}。请使用 STRING 列保存任意精度整数。");
        if (element.TryGetDouble(out var d)) return d;
        // 兜底：原始文本
        return raw;
    }

    private static object? ReadDecimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var value))
            throw new InvalidDataException("远程 SQL DECIMAL 值无法无损解析。");
        return value;
    }

    private static ExecutionFieldTypeKind ReadColumnType(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("远程 SQL meta 列类型必须是字符串。");
        return element.GetString() switch
        {
            "int64" => ExecutionFieldTypeKind.Int64,
            "float64" => ExecutionFieldTypeKind.Double,
            "decimal" => ExecutionFieldTypeKind.Decimal,
            "boolean" => ExecutionFieldTypeKind.Boolean,
            "string" => ExecutionFieldTypeKind.String,
            "datetime" => ExecutionFieldTypeKind.DateTime,
            "datetimeoffset" => ExecutionFieldTypeKind.DateTimeOffset,
            "time" => ExecutionFieldTypeKind.TimeOnly,
            "duration" => ExecutionFieldTypeKind.Duration,
            "guid" => ExecutionFieldTypeKind.Guid,
            "geopoint" => ExecutionFieldTypeKind.GeoPoint,
            "blob" => ExecutionFieldTypeKind.ByteArray,
            "vector" => ExecutionFieldTypeKind.Vector,
            "object" => ExecutionFieldTypeKind.Object,
            _ => throw new InvalidDataException("远程 SQL meta 包含不支持的列类型。"),
        };
    }

    private static bool ReadSchemaBoolean(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"远程 SQL meta 列 schema 的 {name} 必须是布尔值。");
        return property.GetBoolean();
    }
}
