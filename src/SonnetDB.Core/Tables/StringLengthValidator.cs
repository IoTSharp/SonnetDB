namespace SonnetDB.Tables;

internal static class StringLengthValidator
{
    internal static void Validate(TableColumn column, object? value, string tableName)
    {
        if (column.StringLength is { } maximum && value is string text && text.Length > maximum)
            throw new TableConstraintException(TableConstraintException.StringLengthViolation, tableName, null,
                $"列 '{column.Name}' 的字符串长度超过 {maximum} 个 UTF-16 代码单元；不会截断值。");
    }
}
