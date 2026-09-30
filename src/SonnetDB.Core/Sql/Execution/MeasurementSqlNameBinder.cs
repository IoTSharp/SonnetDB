using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

internal static class MeasurementSqlNameBinder
{
    internal static SqlStatement Bind(Tsdb tsdb, SqlStatement statement)
        => statement switch
        {
            CreateMeasurementStatement create => BindCreate(tsdb, create),
            InsertStatement insert => BindInsert(tsdb, insert),
            SelectStatement select => BindSelect(tsdb, select),
            DeleteStatement delete => BindDelete(tsdb, delete),
            UpdateStatement update => BindUpdate(tsdb, update),
            DropMeasurementStatement drop => BindDrop(tsdb, drop),
            DescribeMeasurementStatement describe => describe with
            {
                Name = tsdb.Measurements.Resolve(describe.Name, describe.NameIsQuoted)?.Name ?? describe.Name,
            },
            _ => statement,
        };

    private static CreateMeasurementStatement BindCreate(Tsdb tsdb, CreateMeasurementStatement statement)
    {
        if (!statement.IfNotExists)
            return statement;

        MeasurementSchema? schema = tsdb.Measurements.Resolve(statement.Name, statement.NameIsQuoted);
        return schema is null ? statement : statement with { Name = schema.Name };
    }

    private static DropMeasurementStatement BindDrop(Tsdb tsdb, DropMeasurementStatement statement)
    {
        MeasurementSchema? schema = tsdb.Measurements.Resolve(statement.Name, statement.NameIsQuoted);
        return statement with { Name = schema?.Name ?? statement.Name };
    }

    private static InsertStatement BindInsert(Tsdb tsdb, InsertStatement statement)
    {
        if (tsdb.Tables.Catalog.Resolve(statement.Measurement, statement.MeasurementIsQuoted) is not null
            || tsdb.Documents.Catalog.TryGet(statement.Measurement) is not null)
            return statement;

        MeasurementSchema? schema = tsdb.Measurements.Resolve(
            statement.Measurement, statement.MeasurementIsQuoted);
        var columns = new string[statement.Columns.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < columns.Length; i++)
        {
            string name = statement.Columns[i];
            bool quoted = i < statement.ColumnIsQuoted.Count && statement.ColumnIsQuoted[i];
            if (!quoted && string.Equals(name, "time", StringComparison.OrdinalIgnoreCase))
            {
                columns[i] = "time";
                continue;
            }

            MeasurementColumn? column = schema?.Resolve(name, quoted);
            columns[i] = column?.Name ?? name;
            if (!seen.Add(columns[i]))
                throw new InvalidOperationException($"INSERT 列列表中列 '{name}' 重复。");
        }

        return statement with
        {
            Measurement = schema?.Name ?? statement.Measurement,
            Columns = columns,
        };
    }

    private static DeleteStatement BindDelete(Tsdb tsdb, DeleteStatement statement)
    {
        if (tsdb.Tables.Catalog.Resolve(statement.Measurement, statement.MeasurementIsQuoted) is not null
            || tsdb.Documents.Catalog.TryGet(statement.Measurement) is not null)
            return statement;

        MeasurementSchema? schema = tsdb.Measurements.Resolve(
            statement.Measurement, statement.MeasurementIsQuoted);
        if (schema is null)
            return statement with
            {
                Measurement = statement.Measurement,
            };
        return statement with
        {
            Measurement = schema.Name,
            Where = BindExpression(tsdb, statement.Where, schema, tableAlias: null),
        };
    }

    private static UpdateStatement BindUpdate(Tsdb tsdb, UpdateStatement statement)
    {
        if (tsdb.Tables.Catalog.Resolve(statement.TableName, statement.TableNameIsQuoted) is not null
            || tsdb.Documents.Catalog.TryGet(statement.TableName) is not null)
            return statement;

        MeasurementSchema? schema = tsdb.Measurements.Resolve(statement.TableName, statement.TableNameIsQuoted);
        if (schema is null || statement.FromClauses.Count != 0)
            return statement with
            {
                TableName = statement.TableName,
            };

        return statement with
        {
            TableName = schema.Name,
            Assignments = statement.Assignments.Select(assignment => assignment with
            {
                ColumnName = schema.Resolve(assignment.ColumnName, assignment.ColumnNameIsQuoted)?.Name
                    ?? assignment.ColumnName,
                Value = BindExpression(tsdb, assignment.Value, schema, statement.TableAlias),
            }).ToArray(),
            Where = BindExpression(tsdb, statement.Where, schema, statement.TableAlias),
        };
    }

    internal static SelectStatement BindSelect(Tsdb tsdb, SelectStatement statement)
        => BindSelect(tsdb, statement, Array.Empty<string>());

    private static SelectStatement BindSelect(
        Tsdb tsdb, SelectStatement statement, IReadOnlyList<string> inheritedCtes)
    {
        var visibleCtes = new List<string>(inheritedCtes);
        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ctes = new CommonTableExpression[statement.CommonTableExpressions.Count];
        for (int i = 0; i < ctes.Length; i++)
        {
            var cte = statement.CommonTableExpressions[i];
            if (!localNames.Add(cte.Name))
                throw new InvalidOperationException($"CTE 名称 '{cte.Name}' 重复或仅大小写不同。");
            visibleCtes.RemoveAll(name => Matches(name, cte.Name, quoted: false));
            if (statement.IsRecursive)
                visibleCtes.Add(cte.Name);
            ctes[i] = cte with { Query = BindSelect(tsdb, cte.Query, visibleCtes) };
            if (!statement.IsRecursive)
                visibleCtes.Add(cte.Name);
        }

        bool isPhysicalSource = statement.FromSubquery is null
            && statement.TableValuedFunction is null
            && statement.GraphTable is null
            && !visibleCtes.Any(name => Matches(name, statement.Measurement, statement.MeasurementIsQuoted));
        MeasurementSchema? schema = isPhysicalSource
            && tsdb.Tables.Catalog.Resolve(statement.Measurement, statement.MeasurementIsQuoted) is null
            && tsdb.Documents.Catalog.TryGet(statement.Measurement) is null
            ? tsdb.Measurements.Resolve(statement.Measurement, statement.MeasurementIsQuoted)
            : null;

        Func<SqlExpression, SqlExpression> bindExpression = schema is not null
            && statement.JoinClauses.Count == 0
            && !HybridSearchExecutor.IsHybridSearch(statement)
            ? expression => BindExpression(tsdb, expression, schema, statement.TableAlias, visibleCtes)
            : expression => SqlIdentifierExpressionBinder.Rewrite(
                expression,
                static identifier => identifier,
                select => BindSelect(tsdb, select, visibleCtes));

        var projections = statement.Projections
            .Select(item => item with { Expression = bindExpression(item.Expression) })
            .ToArray();
        var joins = statement.JoinClauses
            .Select(join => join with
            {
                On = bindExpression(join.On),
                Subquery = join.Subquery is null ? null : BindSelect(tsdb, join.Subquery, visibleCtes),
            })
            .ToArray();
        var orderBy = statement.OrderByList
            .Select(item => item with
            {
                Expression = BindOrderExpression(item.Expression, projections, bindExpression),
            })
            .ToArray();
        var setOperations = statement.SetOperationList
            .Select(operation => operation with { Query = BindSelect(tsdb, operation.Query, visibleCtes) })
            .ToArray();
        var bound = statement with
        {
            Measurement = schema?.Name ?? statement.Measurement,
            Projections = projections,
            Where = statement.Where is null ? null : bindExpression(statement.Where),
            GroupBy = statement.GroupBy.Select(bindExpression).ToArray(),
            Having = statement.Having is null ? null : bindExpression(statement.Having),
            // 旧 measurement/document 执行入口仍读取首项属性；两个表示必须保持一致。
            OrderBy = orderBy.FirstOrDefault(),
            OrderByItems = orderBy,
            Join = null,
            Joins = joins,
            FromSubquery = statement.FromSubquery is null
                ? null
                : BindSelect(tsdb, statement.FromSubquery, visibleCtes),
            SetOperations = setOperations,
            Unions = null,
            CommonTableExpressions = ctes,
        };
        return HybridSearchExecutor.IsHybridSearch(bound)
            ? HybridSearchExecutor.BindMeasurementNames(tsdb, bound)
            : bound;
    }

    private static SqlExpression BindExpression(
        Tsdb tsdb,
        SqlExpression expression,
        MeasurementSchema schema,
        string? tableAlias,
        IReadOnlyList<string>? ctes = null)
        => SqlIdentifierExpressionBinder.Rewrite(
            expression,
            identifier => BindIdentifier(identifier, schema, tableAlias),
            select => BindSelect(tsdb, select, ctes ?? Array.Empty<string>()));

    private static IdentifierExpression BindIdentifier(
        IdentifierExpression identifier,
        MeasurementSchema schema,
        string? tableAlias)
    {
        if (identifier.IsNameBound)
            return identifier;
        string qualifier = tableAlias ?? schema.Name;
        if (identifier.Qualifier is not null)
        {
            if (!Matches(qualifier, identifier.Qualifier, identifier.QualifierIsQuoted))
                throw new InvalidOperationException($"未知表别名 '{identifier.Qualifier}'。");
        }

        if (!identifier.IsQuoted
            && string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase))
            return identifier with
            {
                Name = "time",
                Qualifier = identifier.Qualifier is null ? null : qualifier,
                IsNameBound = true,
            };

        MeasurementColumn? column = schema.Resolve(identifier.Name, identifier.IsQuoted);
        if (column is null)
            throw new InvalidOperationException($"SELECT 中引用了未知列 '{identifier.Name}'。");
        return identifier with
        {
            Name = column.Name,
            Qualifier = identifier.Qualifier is null ? null : qualifier,
            IsNameBound = true,
        };
    }

    private static SqlExpression BindOrderExpression(
        SqlExpression expression,
        IReadOnlyList<SelectItem> projections,
        Func<SqlExpression, SqlExpression> bindExpression)
    {
        if (expression is IdentifierExpression { Qualifier: null } identifier)
        {
            var aliases = projections.Where(item => item.Alias is not null
                && Matches(item.Alias, identifier.Name, identifier.IsQuoted || identifier.IsNameBound)).ToArray();
            if (aliases.Length > 1)
                throw new InvalidOperationException($"ORDER BY 别名 '{identifier.Name}' 存在歧义。");
            if (aliases.Length == 1)
                return identifier with { Name = aliases[0].Alias!, IsNameBound = true };
        }
        return bindExpression(expression);
    }

    private static bool Matches(string candidate, string name, bool quoted)
        => string.Equals(candidate, name, quoted ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
