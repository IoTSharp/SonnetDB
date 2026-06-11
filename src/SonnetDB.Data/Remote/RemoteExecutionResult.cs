using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SonnetDB.Data.Internal;
using SonnetDB.Model;

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
    private object?[] _currentRow;
    private bool _ended;

    public int RecordsAffected { get; private set; }

    public IReadOnlyList<string> Columns => _columns;

    private RemoteExecutionResult(HttpResponseMessage response, Stream stream, StreamReader reader, string[] columns)
    {
        _response = response;
        _stream = stream;
        _reader = reader;
        _columns = columns;
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
                return false;
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
                return false;
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
        var v = _currentRow[ordinal];
        return ExecutionFieldTypeResolver.GetRuntimeType(ExecutionFieldTypeResolver.Resolve(v));
    }

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
                _currentRow[i] = ReadScalar(root[i]);
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
            {
                var type = typeProp.GetString();
                if (type == "end")
                {
                    if (root.TryGetProperty("recordsAffected", out var ra) && ra.ValueKind == JsonValueKind.Number)
                        RecordsAffected = ra.GetInt32();
                    if (_columns.Length > 0) RecordsAffected = -1;
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
    /// 创建实例：先消费 meta 行（或直接读取 end 行用于非 SELECT）。
    /// </summary>
    public static RemoteExecutionResult Create(HttpResponseMessage response, Stream stream)
    {
        var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);

        string[] columns = Array.Empty<string>();
        int recordsAffected = -1;
        bool sawMeta = false;
        bool ended = false;

        while (!ended)
        {
            var line = reader.ReadLine();
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
                reader.Dispose();
                stream.Dispose();
                response.Dispose();
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
                    }

                    // 非 SELECT：columns 为空，紧接着应是 end；继续循环消费 end
                    if (columns.Length == 0) continue;
                    break; // SELECT：meta 之后就是行数据，交给 ReadNextRow 处理
                }
                if (type == "end")
                {
                    if (root.TryGetProperty("recordsAffected", out var ra) && ra.ValueKind == JsonValueKind.Number)
                        recordsAffected = ra.GetInt32();
                    ended = true;
                    break;
                }
            }
        }

        if (!sawMeta && !ended)
        {
            // 协议异常：既无 meta 也无 end
            reader.Dispose();
            stream.Dispose();
            response.Dispose();
            throw new InvalidDataException("远程响应缺少 meta 或 end 行。");
        }

        var result = new RemoteExecutionResult(response, stream, reader, columns);
        if (ended)
            result.RecordsAffected = recordsAffected;
        return result;
    }

    private static object? ReadScalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => ReadNumber(element),
        JsonValueKind.Array => element.GetRawText(),
        JsonValueKind.Object => TryReadGeoPoint(element, out var point) ? point : element.GetRawText(),
        _ => null,
    };

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

    private static object ReadNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var i64)) return i64;
        if (element.TryGetDouble(out var d)) return d;
        // 兜底：原始文本
        return element.GetRawText();
    }
}
