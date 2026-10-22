using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static class SqlCreateTableAsSchema
{
    internal static TableSchema Create(string name, SelectExecutionResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Truncated)
            throw new InvalidOperationException("CTAS 不接受截断的预览结果。");
        if (result.Columns.Count == 0 || result.Columns.Count > 1_024)
            throw new InvalidOperationException("CTAS 输出列数必须为 1 到 1024。");
        if (result.ColumnInfo is not { } declarations || declarations.Count != result.Columns.Count)
            throw new InvalidOperationException("CTAS 需要完整的静态列类型；未知表达式请显式 CAST。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<(string Name, TableColumnType DataType, bool IsNullable)>(result.Columns.Count);
        var decimalDefinitions = new Dictionary<string, (byte Precision, byte Scale)>(StringComparer.Ordinal);
        var explicitDecimals = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < result.Columns.Count; index++)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            string columnName = result.Columns[index];
            if (string.IsNullOrWhiteSpace(columnName) || !names.Add(columnName))
                throw new InvalidOperationException("CTAS 输出列名必须非空且不能仅大小写不同或重复。");
            SelectColumnInfo declaration = declarations[index];
            if (!declaration.IsDeclared || declaration.RequiresRefresh || declaration.DataType is not { } type)
                throw new InvalidOperationException($"CTAS 列 '{columnName}' 的类型未知或过期，请显式 CAST 或刷新来源。");
            columns.Add((columnName, type, true));
            if (type == TableColumnType.Decimal)
            {
                if (declaration.HasExplicitDecimalDeclaration
                    && (declaration.NumericPrecision is null || declaration.NumericScale is null))
                    throw new InvalidOperationException($"CTAS Decimal 列 '{columnName}' 缺少声明 precision/scale。");
                decimalDefinitions.Add(columnName, (declaration.NumericPrecision ?? (byte)38, declaration.NumericScale ?? (byte)28));
                if (declaration.HasExplicitDecimalDeclaration)
                    explicitDecimals.Add(columnName);
            }
        }
        // 只复制值域声明，不继承键、默认值、自增、ROWVERSION、外键、CHECK、索引或备注。
        return TableSchema.CreateWithDefaults(name, columns, [], indexes: null, foreignKeys: null,
            rowVersionColumns: null, createdAtUtcTicks: 0, checkConstraints: null, columnDefaults: null,
            autoIncrementColumns: null, decimalDefinitions: decimalDefinitions, explicitDecimalDeclarations: explicitDecimals);
    }
}
