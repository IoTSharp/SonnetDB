using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// JSON 文件只读虚拟表与导入执行器。
/// </summary>
internal static class JsonFileSqlExecutor
{
    private const int MaxBudgetedFileBytes = 256 * 1024 * 1024;
    private const int MaxBudgetedRecordBytes = 4 * 1024 * 1024;
    private const int MaxBudgetedCandidates = 1_000_000;
    private static readonly TimeSpan BudgetedTimeout = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyList<string> _jsonFileColumns =
        new List<string>(3) { "ordinal", "id", "document" }.AsReadOnly();

    public static SelectExecutionResult ExecuteTableValuedFunction(SelectStatement statement, FunctionCallExpression call)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(call);

        var options = BindOptions(call);
        if (SqlRowRetentionBudget.HasExecutionBudget)
            return ExecuteBudgeted(statement, options);
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

    public static InsertExecutionResult ExecuteImport(
        Tsdb tsdb,
        string? databaseName,
        ImportJsonStatement statement,
        IControlPlane? controlPlane)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (tsdb.Documents.Catalog.TryGet(statement.TargetName) is { } documentSchema)
            return ImportIntoDocumentCollection(tsdb, statement, documentSchema);

        if (tsdb.Tables.Catalog.TryGet(statement.TargetName) is { } tableSchema)
            return ImportIntoTable(tsdb, databaseName, statement, tableSchema, controlPlane);

        throw new InvalidOperationException(
            $"IMPORT JSON 目标 '{statement.TargetName}' 不存在；请先 CREATE DOCUMENT COLLECTION 或 CREATE TABLE。");
    }

    public static (string AccessPath, string? IndexName, int EstimatedRows) ExplainAccess(SelectStatement statement)
    {
        var call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("内部错误：JSON 文件 TVF 调用为空。");
        var options = BindOptions(call);
        if (SqlRowRetentionBudget.HasExecutionBudget)
        {
            int count = 0;
            foreach (JsonFileRow row in EnumerateBudgetedRows(options, Stopwatch.GetTimestamp()))
                count++;
            return ("json_file_budgeted_stream", null, count);
        }
        return ("json_file_virtual_table", null, ReadRows(options.FilePath, options.Format, options.IdPath).Count);
    }

    internal static void ValidateMaterializationSupported(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.TableValuedFunction is not { } call
            || !(call.Name.Equals("json_each", StringComparison.OrdinalIgnoreCase)
                || call.Name.Equals("json_table", StringComparison.OrdinalIgnoreCase))
            || statement.GroupBy.Count != 0 || statement.Having is not null || statement.Distinct
            || statement.IsRecursive || statement.JoinClauses.Count != 0 || statement.FromSubquery is not null
            || statement.CommonTableExpressions.Count != 0 || statement.SetOperationList.Count != 0
            || call.IsDistinct || call.Over is not null
            || statement.GraphTable is not null || statement.OrderByList.Count != 0)
        {
            throw new NotSupportedException(
                "JSON 文件 SQL 物化预算仅支持直接 json_each/json_table、标量投影、WHERE 和 LIMIT/OFFSET；排序、聚合、窗口、JOIN、去重和嵌套查询尚不支持。");
        }
        if (tsdb.Functions.TryGetTableValuedFunction(call.Name, out _))
            throw new NotSupportedException("JSON 文件 SQL 物化预算不支持用户表值函数回调。");
        JsonFileOptions options = BindOptions(call);
        if (!string.IsNullOrWhiteSpace(options.IdPath))
            _ = JsonPath.Parse(options.IdPath);
        int remainingNodes = 1024;
        foreach (SelectItem projection in statement.Projections)
            ValidateBudgetedExpression(tsdb, projection.Expression, ref remainingNodes, depth: 0, allowStar: true);
        ValidateBudgetedExpression(tsdb, statement.Where, ref remainingNodes, depth: 0, allowBoolean: true);
        _ = BuildProjections(statement.Projections);
        if (statement.Pagination is { } pagination)
        {
            _ = pagination.Offset;
            _ = pagination.Fetch;
        }
    }

    private static void ValidateBudgetedExpression(Tsdb tsdb, SqlExpression? expression,
        ref int remainingNodes, int depth, bool allowStar = false, bool allowBoolean = false)
    {
        if (--remainingNodes < 0 || depth > 64)
            throw new NotSupportedException("JSON 文件 SQL 预算表达式超过 1024 节点或 64 层预检上限。");
        switch (expression)
        {
            case null or LiteralExpression:
                return;
            case StarExpression when allowStar:
                return;
            case IdentifierExpression identifier:
                ValidateIdentifier(identifier);
                return;
            case CastExpression cast when cast.TargetType is not (SqlDataType.Vector or SqlDataType.GeoPoint):
                ValidateBudgetedExpression(tsdb, cast.Operand, ref remainingNodes, depth + 1);
                return;
            case UnaryExpression unary when unary.Operator == SqlUnaryOperator.Negate
                || (allowBoolean && unary.Operator == SqlUnaryOperator.Not):
                ValidateBudgetedExpression(tsdb, unary.Operand, ref remainingNodes, depth + 1,
                    allowBoolean: unary.Operator == SqlUnaryOperator.Not);
                return;
            case BinaryExpression binary when IsArithmeticOperator(binary.Operator)
                || (allowBoolean && (IsComparisonOperator(binary.Operator)
                    || binary.Operator is SqlBinaryOperator.And or SqlBinaryOperator.Or)):
                bool logical = binary.Operator is SqlBinaryOperator.And or SqlBinaryOperator.Or;
                ValidateBudgetedExpression(tsdb, binary.Left, ref remainingNodes, depth + 1, allowBoolean: logical);
                ValidateBudgetedExpression(tsdb, binary.Right, ref remainingNodes, depth + 1, allowBoolean: logical);
                return;
            case IsNullExpression isNull when allowBoolean:
                ValidateBudgetedExpression(tsdb, isNull.Operand, ref remainingNodes, depth + 1);
                return;
            case FunctionCallExpression function:
                if (function.Over is not null || function.IsDistinct || function.IsStar
                    || tsdb.Functions.TryGetScalar(function.Name, out _)
                    || tsdb.Functions.TryGetAggregate(function.Name, out _)
                    || tsdb.Functions.TryGetWindow(function.Name, out _)
                    || FunctionRegistry.TryGetAggregate(function.Name, out _)
                    || FunctionRegistry.TryGetWindow(function.Name, out _))
                    throw new NotSupportedException("JSON 文件 SQL 物化预算不支持用户回调、聚合或窗口函数。");
                if (function.Name.Equals("json_value", StringComparison.OrdinalIgnoreCase))
                {
                    if (function.Arguments.Count != 2 || function.Arguments[1] is not
                        LiteralExpression { Kind: SqlLiteralKind.String, StringValue: { } path })
                        throw new NotSupportedException("JSON 文件 SQL 物化预算要求 json_value 使用固定 JSON path。");
                    _ = JsonPath.Parse(path);
                }
                else if (function.Name.Equals("regexp_like", StringComparison.OrdinalIgnoreCase))
                {
                    if (function.Arguments.Count is < 2 or > 3)
                        throw new InvalidOperationException("函数 regexp_like 需要 2~3 个参数。");
                }
                else if (!FunctionRegistry.TryGetScalar(function.Name, out _))
                    throw new NotSupportedException($"JSON 文件 SQL 物化预算不支持函数 '{function.Name}'。");
                SqlTableFunctionMaterialization.ValidateScalarArgumentCount(function);
                foreach (SqlExpression argument in function.Arguments)
                    ValidateBudgetedExpression(tsdb, argument, ref remainingNodes, depth + 1);
                return;
            default:
                throw new NotSupportedException("JSON 文件 SQL 物化预算仅支持直接只读标量表达式；子查询和模型查询表达式尚不支持。");
        }
    }

    private static SelectExecutionResult ExecuteBudgeted(SelectStatement statement, JsonFileOptions options)
    {
        long startedAt = Stopwatch.GetTimestamp();
        ThrowIfBudgetedCancelled(startedAt);
        Projection[] projections = BuildProjections(statement.Projections);
        string[] columns = projections.Select(static projection => projection.ColumnName).ToArray();
        var rows = new List<IReadOnlyList<object?>>();
        int? fetch = statement.Pagination?.Fetch;
        if (fetch == 0)
            return new SelectExecutionResult(columns, rows);

        int offset = statement.Pagination?.Offset ?? 0;
        int skipped = 0;
        SqlExecutionTelemetry.RecordAccessPath("json_file_budgeted_stream");
        foreach (JsonFileRow row in EnumerateBudgetedRows(options, startedAt))
        {
            ThrowIfBudgetedCancelled(startedAt);
            SqlExecutionTelemetry.RecordExaminedRows(1);
            if (!EvaluateWhere(statement.Where, row))
                continue;
            if (skipped < offset)
            {
                skipped++;
                continue;
            }
            var output = new object?[projections.Length];
            for (int i = 0; i < projections.Length; i++)
            {
                ThrowIfBudgetedCancelled(startedAt);
                output[i] = EvaluateScalar(projections[i].Expression, row);
            }
            SqlRowRetentionBudget.RetainForExecution(output);
            rows.Add(output);
            if (fetch is { } limit && rows.Count >= limit)
                break;
        }
        return new SelectExecutionResult(columns, rows);
    }

    private static IEnumerable<JsonFileRow> EnumerateBudgetedRows(JsonFileOptions options, long startedAt)
    {
        using var input = new BudgetedJsonInput(options.FilePath, startedAt);
        int first = options.Format == JsonImportFormat.Lines ? input.PeekByte() : input.SkipWhitespace();
        if (first < 0)
        {
            if (options.Format == JsonImportFormat.Lines)
                yield break;
            throw new InvalidOperationException("JSON 文件为空。");
        }
        bool lines = options.Format == JsonImportFormat.Lines
            || (options.Format == JsonImportFormat.Auto && first is not ('[' or '{'));
        if (lines)
        {
            if (options.Format == JsonImportFormat.Auto)
                input.RestartForLines();
            // 文件字节界限制物理行数；每行读取另有固定记录字节界和取消/墙钟检查。
            long ordinal = 0;
            for (int line = 0; line < MaxBudgetedFileBytes; line++)
            {
                ThrowIfBudgetedCancelled(startedAt);
                using MemoryStream? record = input.ReadLine();
                if (record is null)
                    yield break;
                if (record.Length == 0)
                    continue;
                if (ordinal >= MaxBudgetedCandidates)
                    throw new InvalidOperationException("JSON 文件 SQL 候选记录数超过 1000000 上限。");
                yield return CreateBudgetedRow(record, ordinal++, options.IdPath);
            }
            throw new InvalidOperationException("JSON 文件 SQL 物理行数超过有界读取上限。");
        }
        if (first == '{')
        {
            using MemoryStream record = input.ReadValue();
            JsonFileRow row = CreateBudgetedRow(record, 0, options.IdPath);
            input.RequireEnd();
            yield return row;
            yield break;
        }
        if (first != '[')
            throw new InvalidOperationException("JSON array 格式要求顶层是数组或对象。");
        input.ReadByte();
        if (input.SkipWhitespace() == ']')
        {
            input.ReadByte();
            input.RequireEnd();
            yield break;
        }
        for (int ordinal = 0; ordinal < MaxBudgetedCandidates; ordinal++)
        {
            ThrowIfBudgetedCancelled(startedAt);
            using MemoryStream record = input.ReadValue();
            JsonFileRow row = CreateBudgetedRow(record, ordinal, options.IdPath);
            // 一个完整记录确认后即可消费；LIMIT 早停不会读取或验证尚未消费的尾部。
            yield return row;
            int separator = input.SkipWhitespace();
            if (separator == ']')
            {
                input.ReadByte();
                input.RequireEnd();
                yield break;
            }
            if (separator != ',')
                throw new JsonException("JSON 数组记录之间缺少逗号或数组未闭合。");
            input.ReadByte();
            if (input.SkipWhitespace() == ']')
                throw new JsonException("JSON 数组不允许尾随逗号。");
        }
        throw new InvalidOperationException("JSON 文件 SQL 候选记录数超过 1000000 上限。");
    }

    private static JsonFileRow CreateBudgetedRow(MemoryStream record, long ordinal, string? idPath)
    {
        using var document = JsonDocument.Parse(record.GetBuffer().AsMemory(0, checked((int)record.Length)));
        JsonFileRow row = CreateRow(document.RootElement, ordinal, idPath);
        SqlExecutionTelemetry.RecordCandidateRows(1);
        // 候选规范化 JSON、ID 及 ordinal 先计入根预算；WHERE/OFFSET 不豁免已物化载荷。
        SqlRowRetentionBudget.RetainForExecution([row.Ordinal, row.Id, row.Document]);
        return row;
    }

    private static void ThrowIfBudgetedCancelled(long startedAt)
    {
        SqlExecutor.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(startedAt) >= BudgetedTimeout)
            throw new TimeoutException("JSON 文件 SQL 物化预算查询超过五分钟执行上限。");
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
        string? databaseName,
        ImportJsonStatement statement,
        TableSchema schema,
        IControlPlane? controlPlane)
    {
        var rows = ReadRows(statement.FilePath, statement.Format, statement.IdPath);
        if (rows.Count == 0)
            return new InsertExecutionResult(schema.Name, 0);

        var columns = schema.Columns
            .Where(static column => !column.IsRowVersion)
            .ToArray();
        var insertRows = new List<IReadOnlyList<SqlExpression>>(rows.Count);
        foreach (var row in rows)
            insertRows.Add(ConvertJsonObjectToInsertRow(schema, columns, row));

        var insert = new InsertStatement(
            schema.Name,
            columns.Select(static column => column.Name).ToArray(),
            insertRows);
        return SqlExecutor.ExecuteImportedTableInsert(
            tsdb,
            databaseName,
            insert,
            schema,
            controlPlane);
    }

    private static SqlExpression[] ConvertJsonObjectToInsertRow(
        TableSchema schema,
        IReadOnlyList<TableColumn> columns,
        JsonFileRow fileRow)
    {
        using var document = JsonDocument.Parse(fileRow.Document);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("IMPORT JSON INTO table 要求每条 JSON 记录是对象。");

        if (schema.RowVersionColumn is { } rowVersionColumn
            && document.RootElement.TryGetProperty(rowVersionColumn.Name, out _))
        {
            throw new InvalidOperationException(
                $"ROWVERSION 列 '{rowVersionColumn.Name}' 由数据库自动维护，IMPORT JSON 不允许显式提供该属性。");
        }

        var values = new SqlExpression[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (!document.RootElement.TryGetProperty(column.Name, out var element))
            {
                values[i] = DefaultValueExpression.Instance;
                continue;
            }

            values[i] = ConvertColumnValueToLiteral(ConvertJsonElementToColumnValue(column, element));
        }

        return values;
    }

    private static LiteralExpression ConvertColumnValueToLiteral(object? value)
        => value switch
        {
            null => LiteralExpression.Null(),
            bool booleanValue => LiteralExpression.Bool(booleanValue),
            long integerValue => LiteralExpression.Integer(integerValue),
            double floatingValue => LiteralExpression.Float(floatingValue),
            string stringValue => LiteralExpression.String(stringValue),
            DateTime dateTimeValue => LiteralExpression.String(
                dateTimeValue.ToString("O", CultureInfo.InvariantCulture)),
            byte[] blobValue => LiteralExpression.String(Convert.ToBase64String(blobValue)),
            _ => throw new InvalidOperationException(
                $"IMPORT JSON 无法将运行时值类型 '{value.GetType().Name}' 转换为 SQL 字面量。"),
        };

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
                    projections.Add(new Projection(item.Alias ?? "expression", item.Expression));
                    break;
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
            CastExpression cast => SqlCastOperations.Convert(EvaluateScalar(cast.Operand, row), cast.TargetType),
            IdentifierExpression identifier => GetIdentifierValue(identifier, row),
            FunctionCallExpression function => EvaluateFunction(function, row),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => SqlScalarOperations.Negate(EvaluateScalar(unary.Operand, row)),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) =>
                SqlScalarOperations.EvaluateArithmetic(
                    binary.Operator,
                    EvaluateScalar(binary.Left, row),
                    EvaluateScalar(binary.Right, row)),
            _ => throw new InvalidOperationException($"JSON 虚拟表表达式暂不支持 '{expression.GetType().Name}'。"),
        };

    private static object? EvaluateFunction(FunctionCallExpression function, JsonFileRow row)
    {
        if (string.Equals(function.Name, "regexp_like", StringComparison.OrdinalIgnoreCase))
        {
            if (function.IsStar || function.Arguments.Count is < 2 or > 3)
                throw new InvalidOperationException("函数 regexp_like 需要 2~3 个参数。");
            return RegexPatternMatcher.IsMatch(
                EvaluateScalar(function.Arguments[0], row),
                EvaluateScalar(function.Arguments[1], row),
                function.Arguments.Count == 3 ? EvaluateScalar(function.Arguments[2], row) : null);
        }

        if (string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
            && !function.IsStar
            && function.Arguments.Count == 2
            && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path })
        {
            var json = EvaluateScalar(function.Arguments[0], row) as string;
            return JsonPathEvaluator.Evaluate(json, path!);
        }

        if (!function.IsStar && FunctionRegistry.TryGetScalar(function.Name, out var scalarFunction))
        {
            var arguments = function.Arguments.Select(argument => EvaluateScalar(argument, row)).ToArray();
            return scalarFunction.Evaluate(arguments);
        }

        throw new InvalidOperationException($"JSON 虚拟表不支持标量函数 '{function.Name}'。");
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

    /// <summary>
    /// 判断 JSON 虚拟表标量表达式可使用的算术运算符。
    /// </summary>
    private static bool IsArithmeticOperator(SqlBinaryOperator value) => value is
        SqlBinaryOperator.Add or
        SqlBinaryOperator.Subtract or
        SqlBinaryOperator.Multiply or
        SqlBinaryOperator.Divide or
        SqlBinaryOperator.Modulo or
        SqlBinaryOperator.BitwiseAnd or
        SqlBinaryOperator.BitwiseOr;

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

    /// <summary>固定缓冲的 UTF-8 分帧读取；记录再交给 JsonDocument 验证完整 JSON 语法。</summary>
    private sealed class BudgetedJsonInput : IDisposable
    {
        private readonly BufferedStream _stream;
        private readonly long _startedAt;
        private int? _lookahead;
        private int _bytesRead;
        private readonly int _contentStart;

        internal BudgetedJsonInput(string filePath, long startedAt)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            _startedAt = startedAt;
            ThrowIfBudgetedCancelled(startedAt);
            var stream = File.OpenRead(filePath);
            try
            {
                if (stream.Length > MaxBudgetedFileBytes)
                    throw new InvalidOperationException("JSON 文件 SQL 预算路径只读取不超过 256 MiB 的文件。");
                _stream = new BufferedStream(stream, 4096);
                if (PeekByte() == 0xef)
                {
                    ReadByte();
                    if (ReadByte() != 0xbb || ReadByte() != 0xbf)
                        throw new JsonException("JSON 文件包含无效 UTF-8 BOM。");
                    _contentStart = 3;
                }
                else if (PeekByte() is 0xff or 0xfe)
                    throw new NotSupportedException("JSON 文件 SQL 预算路径仅支持 UTF-8 文件。");
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal void RestartForLines()
        {
            ThrowIfBudgetedCancelled(_startedAt);
            _stream.Position = _contentStart;
            _lookahead = null;
            _bytesRead = _contentStart;
        }

        internal int PeekByte()
        {
            if (_lookahead is { } value)
                return value;
            if ((_bytesRead & 1023) == 0)
                ThrowIfBudgetedCancelled(_startedAt);
            int next = _stream.ReadByte();
            if (next >= 0 && ++_bytesRead > MaxBudgetedFileBytes)
                throw new InvalidOperationException("JSON 文件 SQL 读取字节数超过 256 MiB 上限。");
            _lookahead = next;
            return next;
        }

        internal int ReadByte()
        {
            int value = PeekByte();
            _lookahead = null;
            return value;
        }

        internal int SkipWhitespace()
        {
            for (int skipped = 0; skipped <= MaxBudgetedFileBytes; skipped++)
            {
                int value = PeekByte();
                if (!IsWhitespace(value))
                    return value;
                ReadByte();
            }
            throw new InvalidOperationException("JSON 文件 SQL 空白读取超过文件字节上限。");
        }

        internal void RequireEnd()
        {
            if (SkipWhitespace() >= 0)
                throw new JsonException("JSON 文件顶层值后存在多余数据。");
        }

        internal MemoryStream? ReadLine()
        {
            if (PeekByte() < 0)
                return null;
            var record = new MemoryStream();
            bool hasContent = false;
            try
            {
                for (int length = 0; length <= MaxBudgetedRecordBytes; length++)
                {
                    int value = ReadByte();
                    if (value is < 0 or '\n')
                    {
                        if (!hasContent)
                            record.SetLength(0);
                        return record;
                    }
                    if (length == MaxBudgetedRecordBytes)
                        throw RecordLimitExceeded();
                    record.WriteByte((byte)value);
                    hasContent |= !IsWhitespace(value);
                }
                throw RecordLimitExceeded();
            }
            catch
            {
                record.Dispose();
                throw;
            }
        }

        internal MemoryStream ReadValue()
        {
            int first = SkipWhitespace();
            if (first < 0)
                throw new JsonException("JSON 数组值未完成。");
            var record = new MemoryStream();
            int depth = 0;
            bool quoted = false;
            bool escaped = false;
            bool structured = first is '{' or '[';
            bool stringValue = first == '"';
            try
            {
                // 每次消费一个字节；退出条件由记录字节界、结构闭合或标量分隔符共同保证。
                for (int length = 0; length < MaxBudgetedRecordBytes; length++)
                {
                    int value = PeekByte();
                    if (!structured && !stringValue && length != 0
                        && (value is < 0 or ',' or ']' || IsWhitespace(value)))
                        return record;
                    if (value < 0)
                        throw new JsonException("JSON 数组值未闭合。");
                    ReadByte();
                    record.WriteByte((byte)value);
                    if (quoted)
                    {
                        if (escaped)
                            escaped = false;
                        else if (value == '\\')
                            escaped = true;
                        else if (value == '"')
                        {
                            quoted = false;
                            if (stringValue)
                                return record;
                        }
                    }
                    else if (value == '"')
                        quoted = true;
                    else if (value is '{' or '[')
                        depth++;
                    else if (value is '}' or ']')
                    {
                        depth--;
                        if (structured && depth == 0)
                            return record;
                    }
                }
                // 精确等号的标量允许以分隔符终止；结构与字符串在最后一个字节已返回。
                int next = PeekByte();
                if (!structured && !stringValue && (next is < 0 or ',' or ']' || IsWhitespace(next)))
                    return record;
                throw RecordLimitExceeded();
            }
            catch
            {
                record.Dispose();
                throw;
            }
        }

        private static bool IsWhitespace(int value) => value is ' ' or '\t' or '\r' or '\n';

        private static InvalidOperationException RecordLimitExceeded()
            => new("JSON 文件 SQL 单条记录或 NDJSON 物理行超过 4 MiB 上限。");

        public void Dispose() => _stream.Dispose();
    }

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
