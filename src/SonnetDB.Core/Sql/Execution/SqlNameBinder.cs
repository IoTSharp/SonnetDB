using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static class SqlNameBinder
{
    internal static SqlStatement Bind(Tsdb tsdb, SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.IdentifierNamesBound)
            return statement;
        SqlStatement bound = statement switch
        {
            CreateTableStatement create => BindCreateTable(tsdb, create),
            DropTableStatement drop => drop with
            {
                Name = ResolveTableName(tsdb, drop.Name, drop.NameIsQuoted),
            },
            TruncateTableStatement truncate => truncate with
            {
                TableName = ResolveTableName(tsdb, truncate.TableName, truncate.TableNameIsQuoted),
            },
            DescribeTableStatement describe => describe with
            {
                Name = ResolveTableName(tsdb, describe.Name, describe.NameIsQuoted),
            },
            ShowTableIndexesStatement show => show with
            {
                TableName = ResolveTableName(tsdb, show.TableName, show.TableNameIsQuoted),
            },
            AnalyzeTableStatement analyze => analyze with
            {
                TableName = ResolveTableName(tsdb, analyze.TableName, analyze.TableNameIsQuoted),
            },
            AlterTableAddColumnStatement add => BindAddColumn(tsdb, add),
            AlterTableAlterPrimaryKeyStatement primaryKey => BindAlterPrimaryKey(tsdb, primaryKey),
            AlterTableAlterColumnStatement alter => BindAlterColumn(tsdb, alter),
            AlterTableAddForeignKeyStatement foreignKey => BindAddForeignKey(tsdb, foreignKey),
            AlterTableAddCheckConstraintStatement check => BindAddCheck(tsdb, check),
            AlterTableDropColumnStatement dropColumn => BindDropColumn(tsdb, dropColumn),
            AlterTableDropConstraintStatement dropConstraint => BindDropConstraint(tsdb, dropConstraint),
            AlterTableRenameColumnStatement renameColumn => BindRenameColumn(tsdb, renameColumn),
            AlterTableRenameTableStatement renameTable => renameTable with
            {
                OldTableName = ResolveTableName(tsdb, renameTable.OldTableName, renameTable.OldTableNameIsQuoted),
                NewTableName = Key(renameTable.NewTableName, renameTable.NewTableNameIsQuoted),
            },
            CreateTableIndexStatement createIndex => BindCreateIndex(tsdb, createIndex),
            CreateTableJsonPathIndexStatement jsonIndex => BindCreateJsonIndex(tsdb, jsonIndex),
            DropTableIndexStatement dropIndex => BindDropIndex(tsdb, dropIndex),
            SelectStatement select => BindSelect(tsdb, select),
            InsertStatement insert => BindInsert(tsdb, insert),
            UpdateStatement update => BindUpdate(tsdb, update),
            DeleteStatement delete => BindDelete(tsdb, delete),
            _ => statement,
        };
        return ReferenceEquals(bound, statement) ? bound : bound with { IdentifierNamesBound = true };
    }

    internal static SelectStatement BindSelect(Tsdb tsdb, SelectStatement statement)
        => BindSelect(tsdb, statement, parent: null, new HashSet<string>(StringComparer.Ordinal));

    private static CreateTableStatement BindCreateTable(Tsdb tsdb, CreateTableStatement statement)
    {
        string name = Key(statement.Name, statement.NameIsQuoted);
        if (statement.IfNotExists
            && ResolveTable(tsdb, statement.Name, statement.NameIsQuoted) is { } existing)
            return statement with { Name = existing.Name };

        var columns = statement.Columns
            .Select(column => column with { Name = Key(column.Name, column.NameIsQuoted) })
            .ToArray();
        var declared = columns.Select(static column => column.Name).ToArray();
        string ResolveDeclared(string columnName, bool quoted)
        {
            var matches = declared.Where(candidate => Matches(candidate, columnName, quoted)).ToArray();
            if (matches.Length == 0)
                throw new ArgumentException($"table '{name}' 中不存在列 '{columnName}'。", nameof(statement));
            if (matches.Length > 1)
                throw new ArgumentException($"table '{name}' 中列 '{columnName}' 存在大小写歧义。", nameof(statement));
            return matches[0];
        }

        var foreignKeys = statement.ForeignKeyClauses.Select(foreignKey =>
        {
            string principalName = Key(foreignKey.PrincipalTable, foreignKey.PrincipalTableIsQuoted);
            var principal = ResolveTable(tsdb,
                foreignKey.PrincipalTable, foreignKey.PrincipalTableIsQuoted);
            bool selfReference = Matches(name, principalName, foreignKey.PrincipalTableIsQuoted);
            return foreignKey with
            {
                Columns = foreignKey.Columns
                    .Select((column, index) => ResolveDeclared(
                        column, IsQuoted(foreignKey.ColumnIsQuoted, index)))
                    .ToArray(),
                PrincipalTable = principal?.Name ?? principalName,
                PrincipalColumns = foreignKey.PrincipalColumns
                    .Select((column, index) => principal is not null
                        ? RequireColumn(principal, column,
                            IsQuoted(foreignKey.PrincipalColumnIsQuoted, index)).Name
                        : selfReference
                            ? ResolveDeclared(column,
                                IsQuoted(foreignKey.PrincipalColumnIsQuoted, index))
                            : Key(column, IsQuoted(foreignKey.PrincipalColumnIsQuoted, index)))
                    .ToArray(),
            };
        }).ToArray();
        var checks = statement.CheckConstraintClauses.Select(check =>
        {
            var expression = SqlIdentifierExpressionBinder.Rewrite(check.Expression,
                identifier => identifier with
                {
                    Name = ResolveDeclared(identifier.Name, identifier.IsQuoted),
                    IsNameBound = true,
                }, static select => select);
            return check with { Expression = expression, ExpressionSql = SqlExpressionFormatter.Format(expression) };
        }).ToArray();

        return statement with
        {
            Name = name,
            Columns = columns,
            PrimaryKey = statement.PrimaryKey
                .Select((column, index) => ResolveDeclared(
                    column, IsQuoted(statement.PrimaryKeyColumnIsQuoted, index)))
                .ToArray(),
            ForeignKeys = foreignKeys,
            CheckConstraints = checks,
        };
    }

    private static AlterTableAddColumnStatement BindAddColumn(
        Tsdb tsdb, AlterTableAddColumnStatement statement)
        => statement with
        {
            TableName = ResolveTableName(tsdb, statement.TableName, statement.TableNameIsQuoted),
            ColumnName = Key(statement.ColumnName, statement.ColumnNameIsQuoted),
        };

    private static AlterTableAlterPrimaryKeyStatement BindAlterPrimaryKey(
        Tsdb tsdb, AlterTableAlterPrimaryKeyStatement statement)
    {
        var schema = ResolveTable(tsdb, statement.TableName, statement.TableNameIsQuoted);
        return schema is null ? statement : statement with
        {
            TableName = schema.Name,
            Columns = statement.Columns.Select((name, index) =>
                (schema.Resolve(name, IsQuoted(statement.ColumnIsQuoted, index))
                    ?? throw new ArgumentException($"PRIMARY KEY 引用了未知列 '{name}'。", nameof(statement))).Name).ToArray(),
        };
    }

    private static AlterTableAlterColumnStatement BindAlterColumn(
        Tsdb tsdb, AlterTableAlterColumnStatement statement)
    {
        var schema = ResolveTable(tsdb, statement.TableName, statement.TableNameIsQuoted);
        return schema is null ? statement : statement with
        {
            TableName = schema.Name,
            ColumnName = RequireColumn(schema, statement.ColumnName, statement.ColumnNameIsQuoted).Name,
        };
    }

    private static AlterTableAddForeignKeyStatement BindAddForeignKey(
        Tsdb tsdb, AlterTableAddForeignKeyStatement statement)
    {
        var schema = ResolveTable(tsdb, statement.TableName, statement.TableNameIsQuoted);
        if (schema is null)
            return statement;
        var principal = ResolveTable(tsdb,
            statement.PrincipalTable, statement.PrincipalTableIsQuoted);
        return statement with
        {
            TableName = schema.Name,
            Columns = statement.Columns.Select((name, index) =>
                RequireColumn(schema, name, IsQuoted(statement.ColumnIsQuoted, index)).Name).ToArray(),
            PrincipalTable = principal?.Name ?? Key(
                statement.PrincipalTable, statement.PrincipalTableIsQuoted),
            PrincipalColumns = principal is null
                ? statement.PrincipalColumns.Select((name, index) =>
                    Key(name, IsQuoted(statement.PrincipalColumnIsQuoted, index))).ToArray()
                : statement.PrincipalColumns.Select((name, index) =>
                    RequireColumn(principal, name,
                        IsQuoted(statement.PrincipalColumnIsQuoted, index)).Name).ToArray(),
        };
    }

    private static AlterTableAddCheckConstraintStatement BindAddCheck(
        Tsdb tsdb, AlterTableAddCheckConstraintStatement statement)
    {
        var schema = ResolveTable(tsdb, statement.TableName, statement.TableNameIsQuoted);
        if (schema is null)
            return statement;
        var expression = BindExpression(tsdb, statement.Expression,
            new Scope([new Source(schema.Name, schema)], Parent: null));
        return statement with
        {
            TableName = schema.Name,
            Expression = expression,
            ExpressionSql = SqlExpressionFormatter.Format(expression),
        };
    }

    private static AlterTableDropColumnStatement BindDropColumn(
        Tsdb tsdb, AlterTableDropColumnStatement statement)
    {
        var schema = ResolveTable(tsdb, statement.TableName, statement.TableNameIsQuoted);
        return schema is null ? statement : statement with
        {
            TableName = schema.Name,
            ColumnName = schema.Resolve(statement.ColumnName, statement.ColumnNameIsQuoted)?.Name
                ?? Key(statement.ColumnName, statement.ColumnNameIsQuoted),
        };
    }

    private static AlterTableRenameColumnStatement BindRenameColumn(
        Tsdb tsdb, AlterTableRenameColumnStatement statement)
    {
        var schema = ResolveTable(tsdb, statement.TableName, statement.TableNameIsQuoted);
        return schema is null ? statement : statement with
        {
            TableName = schema.Name,
            OldColumnName = RequireColumn(schema, statement.OldColumnName,
                statement.OldColumnNameIsQuoted).Name,
            NewColumnName = Key(statement.NewColumnName, statement.NewColumnNameIsQuoted),
        };
    }

    private static CreateTableIndexStatement BindCreateIndex(
        Tsdb tsdb, CreateTableIndexStatement statement)
    {
        var schema = ResolveTable(tsdb, statement.TableName, statement.TableNameIsQuoted);
        if (statement.Columns.Any(static column => column.StartsWith('$'))
            || statement.DocumentOptions is not null
            || statement.Online && statement.IsUnique)
            return schema is null ? statement : statement with { TableName = schema.Name };
        return schema is null ? statement : statement with
        {
            TableName = schema.Name,
            IndexName = Key(statement.IndexName, statement.IndexNameIsQuoted),
            Columns = statement.Columns.Select((name, index) =>
                RequireColumn(schema, name, IsQuoted(statement.ColumnIsQuoted, index)).Name).ToArray(),
        };
    }

    private static DropTableIndexStatement BindDropIndex(Tsdb tsdb, DropTableIndexStatement statement)
    {
        var schema = ResolveTable(tsdb, statement.TableName, statement.TableNameIsQuoted);
        if (schema is null)
            return statement;
        var indexes = schema.Indexes.Where(index =>
            Matches(index.Name, statement.IndexName, statement.IndexNameIsQuoted)).ToArray();
        if (indexes.Length > 1)
            throw new InvalidOperationException($"索引 '{statement.IndexName}' 存在大小写歧义。");
        return statement with
        {
            TableName = schema.Name,
            IndexName = indexes.Length == 1 ? indexes[0].Name : statement.IndexName,
        };
    }

    private static AlterTableDropConstraintStatement BindDropConstraint(
        Tsdb tsdb, AlterTableDropConstraintStatement statement)
    {
        var schema = ResolveTable(tsdb, statement.TableName, statement.TableNameIsQuoted);
        if (schema is null)
            return statement;
        var matches = schema.ForeignKeys.Select(static key => key.Name)
            .Concat(schema.CheckConstraints.Select(static check => check.Name))
            .Where(name => Matches(name, statement.ConstraintName, statement.ConstraintNameIsQuoted))
            .ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"约束 '{statement.ConstraintName}' 存在大小写歧义。");
        return statement with
        {
            TableName = schema.Name,
            ConstraintName = matches.Length == 1 ? matches[0] : statement.ConstraintName,
        };
    }

    private static CreateTableJsonPathIndexStatement BindCreateJsonIndex(
        Tsdb tsdb, CreateTableJsonPathIndexStatement statement)
    {
        var schema = ResolveTable(tsdb, statement.TableName, statement.TableNameIsQuoted);
        return schema is null ? statement : statement with
        {
            TableName = schema.Name,
            JsonColumnName = RequireColumn(schema, statement.JsonColumnName, statement.JsonColumnNameIsQuoted).Name,
        };
    }

    private static string ResolveTableName(Tsdb tsdb, string name, bool quoted)
        => ResolveTable(tsdb, name, quoted)?.Name ?? Key(name, quoted);

    private static TableSchema? ResolveTable(Tsdb tsdb, string name, bool quoted)
        => tsdb.Tables.Catalog.Resolve(name, quoted);

    private static TableSchema? ResolveSourceTable(Tsdb tsdb, string name, bool quoted)
    {
        SqlSourceName? source = SqlSourceNameResolver.Resolve(tsdb, name, quoted);
        return source is { Kind: SqlSourceKind.Table }
            ? tsdb.Tables.Catalog.TryGet(source.Value.Name) : null;
    }

    private static SelectStatement BindSelect(
        Tsdb tsdb,
        SelectStatement statement,
        Scope? parent,
        IReadOnlySet<string> inheritedCtes)
    {
        var visibleCtes = new HashSet<string>(inheritedCtes, StringComparer.OrdinalIgnoreCase);
        var declaredCtes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ctes = new CommonTableExpression[statement.CommonTableExpressions.Count];
        for (int i = 0; i < ctes.Length; i++)
        {
            var cte = statement.CommonTableExpressions[i];
            string name = Key(cte.Name, cte.NameIsQuoted);
            if (!declaredCtes.Add(name))
                throw new InvalidOperationException($"CTE 名称重复：'{name}'。");
            if (cte.ColumnNames is { } columnNames
                && columnNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columnNames.Count)
                throw new InvalidOperationException("CTE 输出列名不能重复。");
            if (statement.IsRecursive)
                visibleCtes.Add(name);
            ctes[i] = cte with
            {
                Name = name,
                Query = BindSelect(tsdb, cte.Query, parent: null, visibleCtes),
            };
            visibleCtes.Add(name);
        }

        var fromSubquery = statement.FromSubquery is null
            ? null
            : BindSelect(tsdb, statement.FromSubquery, parent: null, visibleCtes);
        string? fromCteName = statement.TableValuedFunction is null
            ? ResolveCteName(visibleCtes, statement.Measurement, statement.MeasurementIsQuoted)
            : null;
        SqlSourceName? fromSource = fromSubquery is null
            && (statement.TableValuedFunction is null
                || statement.Measurement is not ("__json_file__" or "__graph__"))
            && statement.GraphTable is null
            && fromCteName is null
            ? SqlSourceNameResolver.Resolve(tsdb, statement.Measurement,
                statement.IdentifierNamesBound || statement.MeasurementIsQuoted)
            : null;
        TableSchema? fromSchema = fromSource is { Kind: SqlSourceKind.Table }
            ? tsdb.Tables.Catalog.TryGet(fromSource.Value.Name) : null;
        string fromName = fromSource?.Name ?? fromCteName ?? statement.Measurement;
        string? tableAlias = statement.TableAlias is null
            ? null
            : Key(statement.TableAlias, statement.TableAliasIsQuoted);
        var sources = new List<Source>();
        if (!string.IsNullOrEmpty(fromName) || fromSubquery is not null)
        {
            var fromCte = ctes.FirstOrDefault(cte => string.Equals(cte.Name, fromCteName, StringComparison.Ordinal));
            sources.Add(new Source(tableAlias ?? fromName, fromSchema,
                fromSubquery is not null ? OutputColumns(fromSubquery)
                    : fromCte is not null ? fromCte.ColumnNames ?? OutputColumns(fromCte.Query) : null,
                QualifierKnown: tableAlias is not null || fromSource is not null
                    || fromCteName is not null || fromSubquery is not null));
        }

        var joins = new JoinClause[statement.JoinClauses.Count];
        for (int i = 0; i < joins.Length; i++)
        {
            var join = statement.JoinClauses[i];
            var subquery = join.Subquery is null
                ? null
                : BindSelect(tsdb, join.Subquery, parent: null, visibleCtes);
            string? cteName = ResolveCteName(visibleCtes, join.TableName, join.TableNameIsQuoted);
            SqlSourceName? source = subquery is null
                && cteName is null
                ? SqlSourceNameResolver.Resolve(tsdb, join.TableName,
                    statement.IdentifierNamesBound || join.TableNameIsQuoted)
                : null;
            TableSchema? schema = source is { Kind: SqlSourceKind.Table }
                ? tsdb.Tables.Catalog.TryGet(source.Value.Name) : null;
            string tableName = source?.Name ?? cteName ?? join.TableName;
            bool implicitAlias = string.Equals(join.Alias, join.TableName, StringComparison.Ordinal)
                && join.AliasIsQuoted == join.TableNameIsQuoted;
            string alias = implicitAlias ? tableName : Key(join.Alias, join.AliasIsQuoted);
            joins[i] = join with { TableName = tableName, Alias = alias, Subquery = subquery };
            var joinedCte = ctes.FirstOrDefault(cte => string.Equals(cte.Name, cteName, StringComparison.Ordinal));
            sources.Add(new Source(alias, schema,
                subquery is not null ? OutputColumns(subquery)
                    : joinedCte is not null ? joinedCte.ColumnNames ?? OutputColumns(joinedCte.Query) : null,
                QualifierKnown: !implicitAlias || source is not null
                    || cteName is not null || subquery is not null));
        }

        if (sources.Select(static source => source.Alias).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != sources.Count)
            throw new InvalidOperationException("表别名不能重复或仅大小写不同。");

        // 表值函数可暴露 measurement/document 等合成限定符，由对应执行器解释。
        // 物理 JOIN 来源仍在上面绑定，函数输出表达式保持其专有作用域语义。
        var scope = new Scope(sources, parent, DeferIdentifiers: statement.TableValuedFunction is not null);
        for (int i = 0; i < joins.Length; i++)
            joins[i] = joins[i] with { On = BindExpression(tsdb, joins[i].On, scope, visibleCtes) };

        var projections = statement.Projections
            .Select(item => item with
            {
                Expression = BindExpression(tsdb, item.Expression, scope, visibleCtes),
                Alias = item.Alias is null ? null : Key(item.Alias, item.AliasIsQuoted),
            })
            .ToArray();
        var orderBy = statement.OrderByList
            .Select(item => item with
            {
                Expression = BindOrderExpression(tsdb, item.Expression, scope, visibleCtes, projections),
            })
            .ToArray();
        var setOperations = statement.SetOperationList
            .Select(operation => operation with
            {
                Query = BindSelect(tsdb, operation.Query, parent, visibleCtes),
            })
            .ToArray();

        return statement with
        {
            Measurement = fromName,
            TableAlias = tableAlias,
            FromSubquery = fromSubquery,
            Join = null,
            Joins = joins,
            Projections = projections,
            Where = statement.Where is null
                ? null
                : BindExpression(tsdb, statement.Where, scope, visibleCtes),
            GroupBy = statement.GroupBy
                .Select(expression => BindExpression(tsdb, expression, scope, visibleCtes))
                .ToArray(),
            Having = statement.Having is null
                ? null
                : BindExpression(tsdb, statement.Having, scope, visibleCtes),
            OrderBy = null,
            OrderByItems = orderBy,
            SetOperations = setOperations,
            Unions = null,
            CommonTableExpressions = ctes,
            IdentifierNamesBound = true,
        };
    }

    private static InsertStatement BindInsert(Tsdb tsdb, InsertStatement statement)
    {
        TableSchema? schema = ResolveSourceTable(tsdb,
            statement.Measurement, statement.MeasurementIsQuoted);
        if (schema is null)
            return statement;

        var columns = statement.Columns
            .Select((name, index) => RequireColumn(
                schema, name, IsQuoted(statement.ColumnIsQuoted, index)).Name)
            .ToArray();
        var query = statement.Query is null ? null : BindSelect(tsdb, statement.Query);
        var onConflict = statement.OnConflict;
        if (onConflict is not null)
        {
            var conflictScope = new Scope(
                [new Source(schema.Name, schema), new Source("excluded", schema, QualifiedOnly: true)], Parent: null);
            onConflict = onConflict with
            {
                TargetColumns = onConflict.TargetColumns
                    .Select((name, index) => (schema.Resolve(name, IsQuoted(onConflict.TargetColumnIsQuoted, index))
                        ?? throw new InvalidOperationException(
                            $"ON CONFLICT 目标列 ({string.Join(", ", onConflict.TargetColumns)}) 必须对应主键或唯一索引。 ")).Name)
                    .ToArray(),
                UpdateAssignments = onConflict.UpdateAssignments
                    .Select(assignment => BindAssignment(tsdb, assignment, schema, conflictScope))
                    .ToArray(),
                UpdateWhere = onConflict.UpdateWhere is null
                    ? null
                    : BindExpression(tsdb, onConflict.UpdateWhere, conflictScope),
            };
        }

        return statement with
        {
            Measurement = schema.Name,
            Columns = columns,
            ReturningColumns = BindReturningColumns(
                schema, statement.ReturningColumns, statement.ReturningColumnIsQuoted),
            Query = query,
            OnConflict = onConflict,
            Rows = statement.Rows
                .Select(row => SqlIdentifierExpressionBinder.RewriteList(
                    row, static identifier => identifier, select => BindSelect(tsdb, select)))
                .ToArray(),
        };
    }

    private static UpdateStatement BindUpdate(Tsdb tsdb, UpdateStatement statement)
    {
        TableSchema? schema = ResolveSourceTable(tsdb,
            statement.TableName, statement.TableNameIsQuoted);
        if (schema is null)
            return statement;

        string? tableAlias = statement.TableAlias is null
            ? null
            : Key(statement.TableAlias, statement.TableAliasIsQuoted);
        var sources = new List<Source> { new(tableAlias ?? schema.Name, schema) };
        var joins = new JoinClause[statement.FromClauses.Count];
        for (int i = 0; i < joins.Length; i++)
        {
            var join = statement.FromClauses[i];
            var sourceSchema = ResolveSourceTable(tsdb, join.TableName, join.TableNameIsQuoted);
            string tableName = sourceSchema?.Name ?? join.TableName;
            bool implicitAlias = string.Equals(join.Alias, join.TableName, StringComparison.Ordinal)
                && join.AliasIsQuoted == join.TableNameIsQuoted;
            string alias = implicitAlias ? tableName : Key(join.Alias, join.AliasIsQuoted);
            joins[i] = join with { TableName = tableName, Alias = alias };
            sources.Add(new Source(alias, sourceSchema));
        }
        var scope = new Scope(sources, Parent: null);
        for (int i = 0; i < joins.Length; i++)
            joins[i] = joins[i] with { On = BindExpression(tsdb, joins[i].On, scope) };

        return statement with
        {
            TableName = schema.Name,
            TableAlias = tableAlias,
            FromClauses = joins,
            Assignments = statement.Assignments
                .Select(assignment => BindAssignment(tsdb, assignment, schema, scope))
                .ToArray(),
            Where = BindExpression(tsdb, statement.Where, scope),
            ReturningColumns = BindReturningColumns(
                schema, statement.ReturningColumns, statement.ReturningColumnIsQuoted),
        };
    }

    private static DeleteStatement BindDelete(Tsdb tsdb, DeleteStatement statement)
    {
        TableSchema? schema = ResolveSourceTable(tsdb,
            statement.Measurement, statement.MeasurementIsQuoted);
        if (schema is null)
            return statement;
        return statement with
        {
            Measurement = schema.Name,
            ReturningColumns = BindReturningColumns(
                schema, statement.ReturningColumns, statement.ReturningColumnIsQuoted),
            Where = BindExpression(
                tsdb, statement.Where, new Scope([new Source(schema.Name, schema)], Parent: null)),
        };
    }

    private static UpdateAssignment BindAssignment(
        Tsdb tsdb,
        UpdateAssignment assignment,
        TableSchema schema,
        Scope scope)
        => assignment with
        {
            ColumnName = RequireColumn(schema, assignment.ColumnName, assignment.ColumnNameIsQuoted).Name,
            Value = BindExpression(tsdb, assignment.Value, scope),
        };

    private static SqlExpression BindOrderExpression(
        Tsdb tsdb,
        SqlExpression expression,
        Scope scope,
        IReadOnlySet<string> ctes,
        IReadOnlyList<SelectItem> projections)
    {
        if (expression is IdentifierExpression { Qualifier: null } identifier)
        {
            var matches = projections.Where(item => item.Alias is not null
                && Matches(item.Alias, identifier.Name, identifier.IsQuoted || identifier.IsNameBound)).ToArray();
            if (matches.Length == 1)
                return identifier with { Name = matches[0].Alias!, IsNameBound = true };
            if (matches.Length > 1)
                throw new InvalidOperationException($"ORDER BY 别名 '{identifier.Name}' 存在歧义。");
        }
        return BindExpression(tsdb, expression, scope, ctes);
    }

    private static SqlExpression BindExpression(
        Tsdb tsdb,
        SqlExpression expression,
        Scope scope,
        IReadOnlySet<string>? ctes = null)
        => SqlIdentifierExpressionBinder.Rewrite(
            expression,
            identifier => BindIdentifier(identifier, scope),
            select => BindSelect(
                tsdb,
                select,
                scope,
                ctes ?? new HashSet<string>(StringComparer.Ordinal)));

    private static IdentifierExpression BindIdentifier(IdentifierExpression identifier, Scope scope)
    {
        if (identifier.IsNameBound || scope.DeferIdentifiers)
            return identifier;
        if (identifier.Qualifier is not null)
        {
            var source = FindSource(scope, identifier.Qualifier, identifier.QualifierIsQuoted)
                ?? throw new InvalidOperationException($"未知别名 '{identifier.Qualifier}'。");
            if (identifier.Name == "*" || (source.Schema is null && source.OutputColumns is null))
                return source.QualifierKnown
                    ? identifier with { Qualifier = source.Alias, QualifierIsQuoted = true }
                    : identifier;
            string name = source.Schema is not null
                ? RequireColumn(source.Schema, identifier.Name, identifier.IsQuoted).Name
                : ResolveOutputColumn(source.OutputColumns!, identifier.Name, identifier.IsQuoted)
                    ?? throw new InvalidOperationException($"未知列 '{identifier.Name}'。");
            return identifier with { Name = name, Qualifier = source.Alias, IsNameBound = true };
        }

        if (identifier.Name == "*")
            return identifier;
        for (Scope? current = scope; current is not null; current = current.Parent)
        {
            // 派生源的输出列在运行时确定；保留未限定引用供现有解析器处理。
            if (current.Sources.Any(static source => source.Schema is null && source.OutputColumns is null))
                return identifier;
            var columns = current.Sources
                .Where(static source => !source.QualifiedOnly)
                .Select(source => source.Schema is not null
                    ? source.Schema.Resolve(identifier.Name, identifier.IsQuoted)?.Name
                    : ResolveOutputColumn(source.OutputColumns!, identifier.Name, identifier.IsQuoted))
                .Where(static column => column is not null)
                .ToArray();
            if (columns.Length > 1)
                throw new InvalidOperationException($"列 '{identifier.Name}' 存在歧义，请使用表别名限定。");
            if (columns.Length == 1)
                return identifier with { Name = columns[0]!, IsNameBound = true };
        }
        throw new InvalidOperationException($"未知列 '{identifier.Name}'。");
    }

    private static Source? FindSource(Scope scope, string alias, bool quoted)
    {
        for (Scope? current = scope; current is not null; current = current.Parent)
        {
            var matches = current.Sources
                .Where(source => Matches(source.Alias, alias, quoted))
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException($"表别名 '{alias}' 存在歧义。");
            if (matches.Length == 1)
                return matches[0];
        }
        return null;
    }

    private static TableColumn RequireColumn(TableSchema schema, string name, bool quoted)
        => schema.Resolve(name, quoted)
            ?? throw new InvalidOperationException($"table '{schema.Name}' 中不存在列 '{name}'。");

    private static IReadOnlyList<string> BindReturningColumns(
        TableSchema schema,
        IReadOnlyList<string> columns,
        IReadOnlyList<bool> quoted)
        => columns.Select((name, index) => name == "*"
            ? name
            : RequireColumn(schema, name, IsQuoted(quoted, index)).Name).ToArray();

    private static bool IsQuoted(IReadOnlyList<bool> flags, int index)
        => index < flags.Count && flags[index];

    private static string Key(string name, bool quoted)
        => name;

    private static bool Matches(string candidate, string name, bool quoted)
        => string.Equals(candidate, name, quoted ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string? ResolveCteName(IReadOnlySet<string> ctes, string name, bool quoted)
        => ctes.FirstOrDefault(candidate => Matches(candidate, name, quoted));

    private static string? ResolveOutputColumn(IReadOnlyList<string> columns, string name, bool quoted)
    {
        var matches = columns.Where(candidate => Matches(candidate, name, quoted)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"列 '{name}' 存在歧义。");
        return matches.Length == 1 ? matches[0] : null;
    }

    private static IReadOnlyList<string>? OutputColumns(SelectStatement query)
    {
        if (query.CteOutputColumnNames is not null)
            return query.CteOutputColumnNames;
        if (query.Projections.Any(static item => item.Expression is StarExpression
            || item.Expression is IdentifierExpression { Name: "*" }
            || item.Alias is null && item.Expression is not IdentifierExpression { IsNameBound: true }))
            return null;
        return query.Projections.Select(static item => item.Alias ??
            (item.Expression is IdentifierExpression identifier
                ? identifier.Name
                : SqlExpressionFormatter.Format(item.Expression))).ToArray();
    }

    private sealed record Source(
        string Alias,
        TableSchema? Schema,
        IReadOnlyList<string>? OutputColumns = null,
        bool QualifierKnown = true,
        bool QualifiedOnly = false);

    private sealed record Scope(IReadOnlyList<Source> Sources, Scope? Parent, bool DeferIdentifiers = false);
}
