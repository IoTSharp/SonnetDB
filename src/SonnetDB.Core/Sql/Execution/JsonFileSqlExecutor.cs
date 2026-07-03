using System.Globalization;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// JSON 文件只读虚拟表与导入执行器。
/// </summary>
internal static class JsonFileSqlExecutor
{
    private static readonly IReadOnlyList<string> _jsonFileColumns =
        new List<string>(3) { "ordinal", "id", "document" }.AsReadOnly();

    public static SelectExecutionResult ExecuteTableValuedFunction(SelectStatement statement, FunctionCallExpression call)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(call);

        var options = BindOptions(call);
        var rows = ReadRows(options.FilePath, options.Format, options.IdPath);
        var projections = BuildProjections(statement.Projections);
        var filtered = new List<IReadOnlyList<object?>>();
        foreach (var row in rows)
        {
            if (!EvaluateWhere(statement.Where, row))
                continue;

            var output = new object?[projections.Length];
            for (int i = 0; i < projections.Length; i++)
                output[i] = EvaluateScalar(projections[i].Expression, row);
            filtered.Add(output);
        }

        var result = new SelectExecutionResult(
            projections.Select(static p => p.ColumnName).ToArray(),
            filtered);
        return ApplyPagination(ApplyOrderBy(result, statement.OrderBy), statement.Pagination);
    }

    public static InsertExecutionResult ExecuteImport(Tsdb tsdb, ImportJsonStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (tsdb.Documents.Catalog.TryGet(statement.TargetName) is { } documentSchema)
            return ImportIntoDocumentCollection(tsdb, statement, documentSchema);

        if (tsdb.Tables.Catalog.TryGet(statement.TargetName) is { } tableSchema)
            return ImportIntoTable(tsdb, statement, tableSchema);

        throw new InvalidOperationException(
            $"IMPORT JSON 目标 '{statement.TargetName}' 不存在；请先 CREATE DOCUMENT COLLECTION 或 CREATE TABLE。");
    }

    public static (string AccessPath, string? IndexName, int EstimatedRows) ExplainAccess(SelectStatement statement)
    {
        var call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("内部错误：JSON 文件 TVF 调用为空。");
        var options = BindOptions(call);
        return ("json_file_virtual_table", null, ReadRows(options.FilePath, options.Format, options.IdPath).Count);
    }

    private static InsertExecutionResult ImportIntoDocumentCollection(
        Tsdb tsdb,
        ImportJsonStatement statement,
        Documents.DocumentCollectionSchema schema)
    {
        var store = tsdb.Documents.Open(schema.Name);
        var rows = ReadRows(statement.FilePath, statement.Format, statement.IdPath);
        foreach (var row in rows)
            store.Upsert(row.Id, row.Document);
        return new InsertExecutionResult(schema.Name, rows.Count);
    }

    private static InsertExecutionResult ImportIntoTable(
        Tsdb tsdb,
        ImportJsonStatement statement,
        TableSchema schema)
    {
        var rows = ReadRows(statement.FilePath, statement.Format, statement.IdPath);
        if (rows.Count == 0)
            return new InsertExecutionResult(schema.Name, 0);

        var tableRows = new List<IReadOnlyList<object?>>(rows.Count);
        foreach (var row in rows)
            tableRows.Add(ConvertJsonObjectToTableRow(schema, row));

        int inserted = tsdb.Tables.Open(schema.Name).InsertMany(tableRows);
        return new InsertExecutionResult(schema.Name, inserted);
    }

    private static object?[] ConvertJsonObjectToTableRow(TableSchema schema, JsonFileRow fileRow)
    {
        using var document = JsonDocument.Parse(fileRow.Document);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("IMPORT JSON INTO table 要求每条 JSON 记录是对象。");

        var values = new object?[schema.Columns.Count];
        foreach (var column in schema.Columns)
        {
            if (!document.RootElement.TryGetProperty(column.Name, out var element))
            {
                values[column.Ordinal] = null;
                continue;
            }

            values[column.Ordinal] = ConvertJsonElementToColumnValue(column, element);
        }

        return values;
    }

    private static object? ConvertJsonElementToColumnValue(TableColumn column, JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return column.DataType switch
        {
            TableColumnType.Int64 => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var longValue)
                ? longValue
                : ParseInt64(element, column.Name),
            TableColumnType.Float64 => element.ValueKind == JsonValueKind.Number
                ? element.GetDouble()
                : ParseDouble(element, column.Name),
            TableColumnType.Boolean => element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(element.GetString(), out var value) => value,
                _ => throw new InvalidOperationException($"列 '{column.Name}' 需要 BOOL JSON 值。"),
            },
            TableColumnType.String => element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
            TableColumnType.Json => JsonPathEvaluator.NormalizeJson(element.GetRawText()),
            TableColumnType.DateTime => element.ValueKind == JsonValueKind.String
                ? ParseDateTime(element.GetString(), column.Name)
                : element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                    : throw new InvalidOperationException($"列 '{column.Name}' 需要 DATETIME 字符串或 Unix 毫秒。"),
            TableColumnType.Blob => element.ValueKind == JsonValueKind.String
                ? Convert.FromBase64String(element.GetString()!)
                : throw new InvalidOperationException($"列 '{column.Name}' 需要 base64 字符串。"),
            _ => throw new NotSupportedException($"不支持的关系表类型 {column.DataType}。"),
        };
    }

    private static long ParseInt64(JsonElement element, string columnName)
    {
        if (element.ValueKind == JsonValueKind.String
            && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"列 '{columnName}' 需要 INT JSON 值。");
    }

    private static double ParseDouble(JsonElement element, string columnName)
    {
        if (element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"列 '{columnName}' 需要 FLOAT JSON 值。");
    }

    private static DateTime ParseDateTime(string? value, string columnName)
    {
        if (value is not null
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            return dto.UtcDateTime;
        }

        throw new InvalidOperationException($"列 '{columnName}' 需要 DATETIME JSON 字符串。");
    }

    private static JsonFileOptions BindOptions(FunctionCallExpression call)
    {
        if (call.IsStar || call.Arguments.Count is < 1 or > 3)
            throw new InvalidOperationException("json_each/json_table 需要参数：json_each('file'[, 'array|lines|auto'[, '$.id']])。");

        string filePath = RequireString(call.Arguments[0], "file");
        var format = JsonImportFormat.Auto;
        if (call.Arguments.Count >= 2)
            format = ParseFormat(RequireString(call.Arguments[1], "format"));
        string? idPath = call.Arguments.Count == 3
            ? RequireString(call.Arguments[2], "id_path")
            : null;
        return new JsonFileOptions(filePath, format, idPath);
    }

    private static string RequireString(SqlExpression expression, string name)
        => expression is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: { } value }
            ? value
            : throw new InvalidOperationException($"json_each/json_table 参数 '{name}' 必须是字符串字面量。");

    private static JsonImportFormat ParseFormat(string text)
        => text.ToLowerInvariant() switch
        {
            "auto" => JsonImportFormat.Auto,
            "array" => JsonImportFormat.Array,
            "lines" or "ndjson" or "jsonl" => JsonImportFormat.Lines,
            _ => throw new InvalidOperationException("JSON 文件格式仅支持 auto / array / lines。"),
        };

    private static IReadOnlyList<JsonFileRow> ReadRows(string filePath, JsonImportFormat format, string? idPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"JSON 文件 '{filePath}' 不存在。", filePath);

        return format switch
        {
            JsonImportFormat.Array => ReadArrayRows(filePath, idPath),
            JsonImportFormat.Lines => ReadLineRows(filePath, idPath),
            JsonImportFormat.Auto => ReadAutoRows(filePath, idPath),
            _ => throw new InvalidOperationException($"不支持的 JSON 导入格式 {format}。"),
        };
    }

    private static IReadOnlyList<JsonFileRow> ReadAutoRows(string filePath, string? idPath)
    {
        using var stream = File.OpenRead(filePath);
        int first = ReadFirstNonWhitespaceByte(stream);
        stream.Position = 0;
        return first == '[' || first == '{'
            ? ReadArrayOrObjectRows(stream, idPath)
            : ReadLineRows(filePath, idPath);
    }

    private static IReadOnlyList<JsonFileRow> ReadArrayRows(string filePath, string? idPath)
    {
        using var stream = File.OpenRead(filePath);
        return ReadArrayOrObjectRows(stream, idPath);
    }

    private static IReadOnlyList<JsonFileRow> ReadArrayOrObjectRows(Stream stream, string? idPath)
    {
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            var rows = new List<JsonFileRow>(document.RootElement.GetArrayLength());
            long ordinal = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                rows.Add(CreateRow(element, ordinal, idPath));
                ordinal++;
            }

            return rows;
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object)
            return [CreateRow(document.RootElement, 0, idPath)];

        throw new InvalidOperationException("JSON array 格式要求顶层是数组或对象。");
    }

    private static IReadOnlyList<JsonFileRow> ReadLineRows(string filePath, string? idPath)
    {
        var rows = new List<JsonFileRow>();
        long ordinal = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = JsonDocument.Parse(line);
            rows.Add(CreateRow(document.RootElement, ordinal, idPath));
            ordinal++;
        }

        return rows;
    }

    private static JsonFileRow CreateRow(JsonElement element, long ordinal, string? idPath)
    {
        var json = JsonPathEvaluator.NormalizeJson(element.GetRawText());
        string id = ResolveId(json, ordinal, idPath);
        return new JsonFileRow(ordinal, id, json);
    }

    private static string ResolveId(string json, long ordinal, string? idPath)
    {
        if (!string.IsNullOrWhiteSpace(idPath))
        {
            object? value = JsonPathEvaluator.Evaluate(json, idPath);
            if (value is not null)
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? ordinal.ToString(CultureInfo.InvariantCulture);
        }

        object? defaultId = JsonPathEvaluator.Evaluate(json, "$.id");
        return defaultId is null
            ? ordinal.ToString(CultureInfo.InvariantCulture)
            : Convert.ToString(defaultId, CultureInfo.InvariantCulture) ?? ordinal.ToString(CultureInfo.InvariantCulture);
    }

    private static int ReadFirstNonWhitespaceByte(Stream stream)
    {
        int value;
        do
        {
            value = stream.ReadByte();
        }
        while (value >= 0 && char.IsWhiteSpace((char)value));

        if (value < 0)
            throw new InvalidOperationException("JSON 文件为空。");
        return value;
    }

    private static Projection[] BuildProjections(IReadOnlyList<SelectItem> items)
    {
        var projections = new List<Projection>(items.Count);
        foreach (var item in items)
        {
            switch (item.Expression)
            {
                case StarExpression:
                    if (item.Alias is not null)
                        throw new InvalidOperationException("'*' 不允许带 alias。");
                    foreach (var column in _jsonFileColumns)
                        projections.Add(new Projection(column, new IdentifierExpression(column)));
                    break;
                case IdentifierExpression id:
                    ValidateIdentifier(id);
                    projections.Add(new Projection(item.Alias ?? id.Name, item.Expression));
                    break;
                case FunctionCallExpression function:
                    projections.Add(new Projection(item.Alias ?? FormatFunctionColumnName(function), item.Expression));
                    break;
                case LiteralExpression literal:
                    projections.Add(new Projection(item.Alias ?? FormatLiteralColumnName(literal), item.Expression));
                    break;
                default:
                    throw new InvalidOperationException($"JSON 虚拟表 SELECT 暂不支持投影表达式 '{item.Expression.GetType().Name}'。");
            }
        }

        return [.. projections];
    }

    private static bool EvaluateWhere(SqlExpression? expression, JsonFileRow row)
    {
        if (expression is null)
            return true;
        return EvaluateBoolean(expression, row);
    }

    private static bool EvaluateBoolean(SqlExpression expression, JsonFileRow row)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                    return EvaluateBoolean(binary.Left, row) && EvaluateBoolean(binary.Right, row);
                if (binary.Operator == SqlBinaryOperator.Or)
                    return EvaluateBoolean(binary.Left, row) || EvaluateBoolean(binary.Right, row);
                if (IsComparisonOperator(binary.Operator))
                    return EvaluateComparison(binary, row);
                break;
            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                return !EvaluateBoolean(unary.Operand, row);
            case IsNullExpression isNull:
                var isNullValue = EvaluateScalar(isNull.Operand, row) is null;
                return isNull.Negated ? !isNullValue : isNullValue;
        }

        var value = EvaluateScalar(expression, row);
        if (value is bool b)
            return b;
        throw new InvalidOperationException("WHERE 表达式必须计算为布尔值。");
    }

    private static bool EvaluateComparison(BinaryExpression binary, JsonFileRow row)
    {
        var left = EvaluateScalar(binary.Left, row);
        var right = EvaluateScalar(binary.Right, row);
        int? compare = ScalarComparer.Instance.Compare(left, right);

        return binary.Operator switch
        {
            SqlBinaryOperator.Equal => Equals(left, right),
            SqlBinaryOperator.NotEqual => !Equals(left, right),
            SqlBinaryOperator.LessThan => compare is < 0,
            SqlBinaryOperator.LessThanOrEqual => compare is <= 0,
            SqlBinaryOperator.GreaterThan => compare is > 0,
            SqlBinaryOperator.GreaterThanOrEqual => compare is >= 0,
            SqlBinaryOperator.Like => LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotLike => !LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.Regex => RegexPatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotRegex => !RegexPatternMatcher.IsMatch(left, right),
            _ => throw new InvalidOperationException($"不支持的比较运算符 {binary.Operator}。"),
        };
    }

    private static object? EvaluateScalar(SqlExpression expression, JsonFileRow row)
        => expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            IdentifierExpression identifier => GetIdentifierValue(identifier, row),
            FunctionCallExpression function => EvaluateFunction(function, row),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => -Convert.ToDouble(EvaluateScalar(unary.Operand, row), CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"JSON 虚拟表表达式暂不支持 '{expression.GetType().Name}'。"),
        };

    private static object? EvaluateFunction(FunctionCallExpression function, JsonFileRow row)
    {
        if (!string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
            || function.IsStar
            || function.Arguments.Count != 2
            || function.Arguments[1] is not LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path })
        {
            throw new InvalidOperationException("JSON 虚拟表当前仅支持 json_value(document, '$.path') 函数。");
        }

        var json = EvaluateScalar(function.Arguments[0], row) as string;
        return JsonPathEvaluator.Evaluate(json, path!);
    }

    private static object? GetIdentifierValue(IdentifierExpression identifier, JsonFileRow row)
    {
        ValidateIdentifier(identifier);
        if (string.Equals(identifier.Name, "ordinal", StringComparison.OrdinalIgnoreCase))
            return row.Ordinal;
        if (string.Equals(identifier.Name, "id", StringComparison.OrdinalIgnoreCase))
            return row.Id;
        return row.Document;
    }

    private static void ValidateIdentifier(IdentifierExpression identifier)
    {
        if (_jsonFileColumns.Any(c => string.Equals(c, identifier.Name, StringComparison.OrdinalIgnoreCase)))
            return;
        throw new InvalidOperationException($"JSON 虚拟表只暴露 ordinal、id 与 document 伪列，未知列 '{identifier.Name}'。");
    }

    private static object? EvaluateLiteral(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => null,
        SqlLiteralKind.Boolean => literal.BooleanValue,
        SqlLiteralKind.Integer => literal.IntegerValue,
        SqlLiteralKind.Float => literal.FloatValue,
        SqlLiteralKind.String => literal.StringValue,
        _ => throw new InvalidOperationException($"不支持的字面量类型 {literal.Kind}。"),
    };

    private static SelectExecutionResult ApplyOrderBy(SelectExecutionResult result, OrderBySpec? orderBy)
    {
        if (orderBy is null)
            return result;

        if (orderBy.Expression is not IdentifierExpression { Name: var name })
            throw new InvalidOperationException("JSON 虚拟表 ORDER BY 当前仅支持结果列名。");

        int columnIndex = -1;
        for (int i = 0; i < result.Columns.Count; i++)
        {
            if (string.Equals(result.Columns[i], name, StringComparison.Ordinal))
            {
                columnIndex = i;
                break;
            }
        }

        if (columnIndex < 0)
            throw new InvalidOperationException($"ORDER BY 引用了结果集中不存在的列 '{name}'。");

        var rows = orderBy.Direction == SortDirection.Descending
            ? result.Rows.OrderByDescending(row => row[columnIndex], ScalarComparer.Instance).ToArray()
            : result.Rows.OrderBy(row => row[columnIndex], ScalarComparer.Instance).ToArray();
        return new SelectExecutionResult(result.Columns, rows);
    }

    private static SelectExecutionResult ApplyPagination(SelectExecutionResult result, PaginationSpec? pagination)
    {
        if (pagination is null)
            return result;

        int offset = pagination.Offset;
        if (offset >= result.Rows.Count)
            return new SelectExecutionResult(result.Columns, []);

        int take = pagination.Fetch ?? (result.Rows.Count - offset);
        if (take <= 0)
            return new SelectExecutionResult(result.Columns, []);

        return new SelectExecutionResult(result.Columns, result.Rows.Skip(offset).Take(Math.Min(take, result.Rows.Count - offset)).ToArray());
    }

    private static bool IsComparisonOperator(SqlBinaryOperator op)
        => op is SqlBinaryOperator.Equal
            or SqlBinaryOperator.NotEqual
            or SqlBinaryOperator.LessThan
            or SqlBinaryOperator.LessThanOrEqual
            or SqlBinaryOperator.GreaterThan
            or SqlBinaryOperator.GreaterThanOrEqual
            or SqlBinaryOperator.Like
            or SqlBinaryOperator.NotLike
            or SqlBinaryOperator.Regex
            or SqlBinaryOperator.NotRegex;

    private static string FormatFunctionColumnName(FunctionCallExpression function)
        => function.IsStar
            ? function.Name + "(*)"
            : function.Name + "(" + string.Join(",", function.Arguments.Select(FormatExpression)) + ")";

    private static string FormatLiteralColumnName(LiteralExpression literal)
        => literal.Kind switch
        {
            SqlLiteralKind.Null => "NULL",
            SqlLiteralKind.Boolean => literal.BooleanValue ? "TRUE" : "FALSE",
            SqlLiteralKind.Integer => literal.IntegerValue.ToString(CultureInfo.InvariantCulture),
            SqlLiteralKind.Float => literal.FloatValue.ToString(CultureInfo.InvariantCulture),
            SqlLiteralKind.String => literal.StringValue ?? string.Empty,
            _ => literal.Kind.ToString(),
        };

    private static string FormatExpression(SqlExpression expression)
        => expression switch
        {
            IdentifierExpression id => id.Qualifier is null ? id.Name : id.Qualifier + "." + id.Name,
            LiteralExpression literal => FormatLiteralColumnName(literal),
            StarExpression => "*",
            FunctionCallExpression fn => FormatFunctionColumnName(fn),
            _ => expression.GetType().Name,
        };

    private sealed record JsonFileOptions(string FilePath, JsonImportFormat Format, string? IdPath);

    private sealed record JsonFileRow(long Ordinal, string Id, string Document);

    private sealed record Projection(string ColumnName, SqlExpression Expression);

    private sealed class ScalarComparer : IComparer<object?>
    {
        public static ScalarComparer Instance { get; } = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            if (IsNumeric(x) && IsNumeric(y))
                return Convert.ToDouble(x, CultureInfo.InvariantCulture)
                    .CompareTo(Convert.ToDouble(y, CultureInfo.InvariantCulture));

            if (x is IComparable comparable && x.GetType() == y.GetType())
                return comparable.CompareTo(y);

            return string.Compare(
                Convert.ToString(x, CultureInfo.InvariantCulture),
                Convert.ToString(y, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        private static bool IsNumeric(object value)
            => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }
}
