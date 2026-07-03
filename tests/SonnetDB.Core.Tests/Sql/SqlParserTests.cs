using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public class SqlParserTests
{
    // ── CREATE MEASUREMENT ────────────────────────────────────────────────

    [Fact]
    public void Parse_CreateMeasurement_WithTagsAndFields_ReturnsAst()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT cpu (host TAG, region TAG STRING, value FIELD FLOAT, ok FIELD BOOL)");

        Assert.Equal("cpu", stmt.Name);
        Assert.Equal(4, stmt.Columns.Count);
        Assert.Equal(new ColumnDefinition("host", ColumnKind.Tag, SqlDataType.String), stmt.Columns[0]);
        Assert.Equal(new ColumnDefinition("region", ColumnKind.Tag, SqlDataType.String), stmt.Columns[1]);
        Assert.Equal(new ColumnDefinition("value", ColumnKind.Field, SqlDataType.Float64), stmt.Columns[2]);
        Assert.Equal(new ColumnDefinition("ok", ColumnKind.Field, SqlDataType.Boolean), stmt.Columns[3]);
    }

    [Fact]
    public void Parse_CreateFullTextIndex_WithTokenizer_ReturnsAst()
    {
        var stmt = Assert.IsType<CreateFullTextIndexStatement>(SqlParser.Parse(
            "CREATE FULLTEXT INDEX IF NOT EXISTS ft_docs ON device_docs (document, '$.message') USING cjk"));

        Assert.Equal("ft_docs", stmt.IndexName);
        Assert.Equal("device_docs", stmt.CollectionName);
        Assert.Equal(new[] { "document", "$.message" }, stmt.Fields);
        Assert.Equal("cjk", stmt.Tokenizer);
        Assert.True(stmt.IfNotExists);
    }

    [Fact]
    public void Parse_CreateTableIndex_IfNotExistsWithQuotedIdentifiers_ReturnsAst()
    {
        var stmt = Assert.IsType<CreateTableIndexStatement>(SqlParser.Parse(
            "CREATE INDEX IF NOT EXISTS \"IX_Devices_Name\" ON \"Devices\" (\"Name\")"));

        Assert.Equal("IX_Devices_Name", stmt.IndexName);
        Assert.Equal("Devices", stmt.TableName);
        Assert.Equal(new[] { "Name" }, stmt.Columns);
        Assert.True(stmt.IfNotExists);
    }

    [Fact]
    public void Parse_AlterTableDropConstraint_ReturnsAst()
    {
        var stmt = Assert.IsType<AlterTableDropConstraintStatement>(SqlParser.Parse(
            "ALTER TABLE \"Device\" DROP CONSTRAINT \"FK_Device_AuthorizedKeys_AuthorizedKeyId\""));

        Assert.Equal("Device", stmt.TableName);
        Assert.Equal("FK_Device_AuthorizedKeys_AuthorizedKeyId", stmt.ConstraintName);
    }

    [Fact]
    public void Parse_AlterTableDropColumnIfExists_ReturnsAst()
    {
        var stmt = Assert.IsType<AlterTableDropColumnStatement>(SqlParser.Parse(
            "ALTER TABLE \"Device\" DROP COLUMN IF EXISTS \"AuthorizedKeyId\""));

        Assert.Equal("Device", stmt.TableName);
        Assert.Equal("AuthorizedKeyId", stmt.ColumnName);
        Assert.True(stmt.IfExists);
    }

    [Fact]
    public void Parse_ShowDropFullTextIndexes_ReturnsAst()
    {
        var show = Assert.IsType<ShowFullTextIndexesStatement>(
            SqlParser.Parse("SHOW FULLTEXT INDEXES ON device_docs"));
        Assert.Equal("device_docs", show.CollectionName);

        var drop = Assert.IsType<DropFullTextIndexStatement>(
            SqlParser.Parse("DROP FULLTEXT INDEX ft_docs ON device_docs"));
        Assert.Equal("ft_docs", drop.IndexName);
        Assert.Equal("device_docs", drop.CollectionName);
    }

    [Fact]
    public void Parse_MatchWithStarField_ParsesStarArgument()
    {
        var stmt = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT id FROM device_docs WHERE match(ft_docs, *, 'pump alarm', 5)"));

        var match = Assert.IsType<FunctionCallExpression>(stmt.Where);
        Assert.Equal("match", match.Name);
        Assert.False(match.IsStar);
        Assert.Equal(4, match.Arguments.Count);
        Assert.IsType<StarExpression>(match.Arguments[1]);
    }

    [Fact]
    public void Parse_HybridSearchNamedArguments_ReturnsTvfAst()
    {
        var stmt = Assert.IsType<SelectStatement>(SqlParser.Parse("""
            SELECT id, hybrid_score() AS score
            FROM hybrid_search(source => docs, text => 'pump alarm', vector => [1, 0, 0], k => 5)
            WHERE site = 'north'
            ORDER BY score DESC
            """));

        Assert.Equal("docs", stmt.Measurement);
        var tvf = Assert.IsType<FunctionCallExpression>(stmt.TableValuedFunction);
        Assert.Equal("hybrid_search", tvf.Name);
        Assert.All(tvf.Arguments, arg => Assert.IsType<NamedArgumentExpression>(arg));
        var source = Assert.IsType<NamedArgumentExpression>(tvf.Arguments[0]);
        Assert.Equal("source", source.Name);
        Assert.Equal(new IdentifierExpression("docs"), source.Value);
    }

    [Fact]
    public void Parse_CreateMeasurement_TagWithNonStringType_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("CREATE MEASUREMENT m (host TAG INT)"));
    }

    [Fact]
    public void Parse_CreateMeasurement_MissingType_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("CREATE MEASUREMENT m (value FIELD)"));
    }

    [Fact]
    public void Parse_CreateMeasurement_NullabilityModifiers_ReturnsAst()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT cpu (host TAG NULL, usage FIELD FLOAT NOT NULL, label FIELD STRING)");

        Assert.Equal(ColumnNullability.Nullable, stmt.Columns[0].Nullability);
        Assert.Equal(ColumnNullability.NotNull, stmt.Columns[1].Nullability);
        Assert.Equal(ColumnNullability.Unspecified, stmt.Columns[2].Nullability);
        Assert.Null(stmt.Columns[0].DefaultExpression);
    }

    [Fact]
    public void Parse_CreateMeasurement_DefaultModifier_PreservesAst()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT cpu (host TAG DEFAULT 'local', usage FIELD FLOAT DEFAULT 0.5)");

        var hostDefault = Assert.IsType<LiteralExpression>(stmt.Columns[0].DefaultExpression);
        Assert.Equal(LiteralExpression.String("local"), hostDefault);

        var usageDefault = Assert.IsType<LiteralExpression>(stmt.Columns[1].DefaultExpression);
        Assert.Equal(LiteralExpression.Float(0.5), usageDefault);
    }

    [Fact]
    public void Parse_CreateMeasurement_DuplicateNullability_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("CREATE MEASUREMENT m (value FIELD FLOAT NULL NOT NULL)"));
    }

    // ── INSERT ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Insert_SingleRow_ReturnsAst()
    {
        var stmt = (InsertStatement)SqlParser.Parse(
            "INSERT INTO cpu (host, value, ts) VALUES ('node-1', 1.5, 1700000000000)");

        Assert.Equal("cpu", stmt.Measurement);
        Assert.Equal(new[] { "host", "value", "ts" }, stmt.Columns);
        Assert.Single(stmt.Rows);
        var row = stmt.Rows[0];
        Assert.Equal(LiteralExpression.String("node-1"), row[0]);
        Assert.Equal(LiteralExpression.Float(1.5), row[1]);
        Assert.Equal(LiteralExpression.Integer(1_700_000_000_000L), row[2]);
    }

    [Fact]
    public void Parse_Insert_MultipleRows_ReturnsAllRows()
    {
        var stmt = (InsertStatement)SqlParser.Parse(
            "INSERT INTO cpu (host, value) VALUES ('a', 1), ('b', 2), ('c', 3)");
        Assert.Equal(3, stmt.Rows.Count);
        Assert.Equal(LiteralExpression.String("c"), stmt.Rows[2][0]);
        Assert.Equal(LiteralExpression.Integer(3), stmt.Rows[2][1]);
    }

    [Fact]
    public void Parse_Insert_RowArityMismatch_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("INSERT INTO cpu (host, value) VALUES ('a')"));
    }

    [Fact]
    public void Parse_Insert_BooleanAndNullLiteralsSupported()
    {
        var stmt = (InsertStatement)SqlParser.Parse(
            "INSERT INTO m (a, b) VALUES (TRUE, NULL)");
        Assert.Equal(LiteralExpression.Bool(true), stmt.Rows[0][0]);
        Assert.Equal(LiteralExpression.Null(), stmt.Rows[0][1]);
    }

    // ── SELECT ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Select_Star_FromOnly()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT * FROM cpu");
        Assert.Equal("cpu", stmt.Measurement);
        Assert.Single(stmt.Projections);
        Assert.IsType<StarExpression>(stmt.Projections[0].Expression);
        Assert.Null(stmt.Projections[0].Alias);
        Assert.Null(stmt.Where);
        Assert.Empty(stmt.GroupBy);
        Assert.False(stmt.Distinct);
    }

    [Fact]
    public void Parse_SelectDistinct_SetsFlagAndParsesColumn()
    {
        // #219 Q11：DISTINCT 是关键字，不再被误解析为列别名。
        var stmt = (SelectStatement)SqlParser.Parse("SELECT DISTINCT region FROM cpu");
        Assert.True(stmt.Distinct);
        Assert.Single(stmt.Projections);
        var id = Assert.IsType<IdentifierExpression>(stmt.Projections[0].Expression);
        Assert.Equal("region", id.Name);
        Assert.Null(stmt.Projections[0].Alias);
    }

    [Fact]
    public void Parse_Select_AllowsDoubleSlashCommentInsideStatement()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT // note\r\n* FROM cpu");
        Assert.Equal("cpu", stmt.Measurement);
        Assert.Single(stmt.Projections);
        Assert.IsType<StarExpression>(stmt.Projections[0].Expression);
    }

    [Fact]
    public void Parse_Select_AggregateAndAlias()
    {
        var stmt = (SelectStatement)SqlParser.Parse(
            "SELECT count(*) AS c, avg(value) v FROM cpu");
        Assert.Equal(2, stmt.Projections.Count);

        var first = (FunctionCallExpression)stmt.Projections[0].Expression;
        Assert.Equal("count", first.Name);
        Assert.True(first.IsStar);
        Assert.Equal("c", stmt.Projections[0].Alias);

        var second = (FunctionCallExpression)stmt.Projections[1].Expression;
        Assert.Equal("avg", second.Name);
        Assert.False(second.IsStar);
        Assert.Single(second.Arguments);
        Assert.Equal(new IdentifierExpression("value"), second.Arguments[0]);
        Assert.Equal("v", stmt.Projections[1].Alias);
    }

    [Fact]
    public void Parse_Select_CountOne_ParsesLiteralArgument()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT count(1) FROM cpu");

        var fn = Assert.IsType<FunctionCallExpression>(stmt.Projections[0].Expression);
        Assert.Equal("count", fn.Name);
        Assert.False(fn.IsStar);
        Assert.Equal(LiteralExpression.Integer(1), Assert.Single(fn.Arguments));
    }

    [Fact]
    public void Parse_Select_LiteralProjection_ParsesExpression()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT 1 AS ok FROM cpu LIMIT 1");

        Assert.Equal(LiteralExpression.Integer(1), stmt.Projections[0].Expression);
        Assert.Equal("ok", stmt.Projections[0].Alias);
        Assert.NotNull(stmt.Pagination);
        Assert.Equal(1, stmt.Pagination!.Fetch);
    }

    [Fact]
    public void Parse_Select_ScalarFunctionCall_ParsesArguments()
    {
        var stmt = (SelectStatement)SqlParser.Parse(
            "SELECT abs(value), round(value, 2), sqrt(value), log(value), coalesce(label, 'n/a') FROM cpu");

        Assert.Equal(5, stmt.Projections.Count);
        Assert.Equal("abs", Assert.IsType<FunctionCallExpression>(stmt.Projections[0].Expression).Name);
        Assert.Equal("round", Assert.IsType<FunctionCallExpression>(stmt.Projections[1].Expression).Name);
        Assert.Equal("sqrt", Assert.IsType<FunctionCallExpression>(stmt.Projections[2].Expression).Name);
        Assert.Equal("log", Assert.IsType<FunctionCallExpression>(stmt.Projections[3].Expression).Name);
        var coalesce = Assert.IsType<FunctionCallExpression>(stmt.Projections[4].Expression);
        Assert.Equal("coalesce", coalesce.Name);
        Assert.Equal(LiteralExpression.String("n/a"), coalesce.Arguments[1]);
    }

    [Fact]
    public void Parse_Select_GroupByGenericExpression_PreservesAst()
    {
        var stmt = (SelectStatement)SqlParser.Parse(
            "SELECT avg(value) FROM cpu GROUP BY time(1m)");

        Assert.Single(stmt.GroupBy);
        var fn = Assert.IsType<FunctionCallExpression>(stmt.GroupBy[0]);
        Assert.Equal("time", fn.Name);
        Assert.Equal(new DurationLiteralExpression(60_000L), fn.Arguments[0]);
    }

    [Fact]
    public void Parse_Select_WherePrecedence_AndBeforeOr()
    {
        var stmt = (SelectStatement)SqlParser.Parse(
            "SELECT * FROM cpu WHERE host = 'a' AND value > 10 OR ok = TRUE");

        var or = Assert.IsType<BinaryExpression>(stmt.Where);
        Assert.Equal(SqlBinaryOperator.Or, or.Operator);

        var and = Assert.IsType<BinaryExpression>(or.Left);
        Assert.Equal(SqlBinaryOperator.And, and.Operator);

        var hostEq = Assert.IsType<BinaryExpression>(and.Left);
        Assert.Equal(SqlBinaryOperator.Equal, hostEq.Operator);
        Assert.Equal(new IdentifierExpression("host"), hostEq.Left);
        Assert.Equal(LiteralExpression.String("a"), hostEq.Right);

        var valueGt = Assert.IsType<BinaryExpression>(and.Right);
        Assert.Equal(SqlBinaryOperator.GreaterThan, valueGt.Operator);

        var okEq = Assert.IsType<BinaryExpression>(or.Right);
        Assert.Equal(SqlBinaryOperator.Equal, okEq.Operator);
        Assert.Equal(LiteralExpression.Bool(true), okEq.Right);
    }

    [Fact]
    public void Parse_Select_NotOperator()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT * FROM cpu WHERE NOT ok = TRUE");
        var notExpr = Assert.IsType<UnaryExpression>(stmt.Where);
        Assert.Equal(SqlUnaryOperator.Not, notExpr.Operator);
        Assert.IsType<BinaryExpression>(notExpr.Operand);
    }

    [Fact]
    public void Parse_Select_IsNullAndInPredicates_ReturnAst()
    {
        var stmt = (SelectStatement)SqlParser.Parse(
            "SELECT p.Id FROM Produces AS p WHERE NOT (p.Deleted) AND p.ProduceToken IS NOT NULL AND p.Id IN (SELECT d.ProduceId FROM Device AS d)");

        var and = Assert.IsType<BinaryExpression>(stmt.Where);
        var inExpression = Assert.IsType<InExpression>(and.Right);
        Assert.False(inExpression.Negated);
        Assert.NotNull(inExpression.Subquery);

        var leftAnd = Assert.IsType<BinaryExpression>(and.Left);
        var isNotNull = Assert.IsType<IsNullExpression>(leftAnd.Right);
        Assert.True(isNotNull.Negated);
        var tokenRef = Assert.IsType<IdentifierExpression>(isNotNull.Operand);
        Assert.Equal("ProduceToken", tokenRef.Name);
    }

    [Fact]
    public void Parse_Select_ParenthesesOverridePrecedence()
    {
        var stmt = (SelectStatement)SqlParser.Parse(
            "SELECT * FROM cpu WHERE (host = 'a' OR host = 'b') AND value > 0");
        var and = Assert.IsType<BinaryExpression>(stmt.Where);
        Assert.Equal(SqlBinaryOperator.And, and.Operator);
        var or = Assert.IsType<BinaryExpression>(and.Left);
        Assert.Equal(SqlBinaryOperator.Or, or.Operator);
    }

    [Fact]
    public void Parse_Select_GroupByTime_ParsesBucketSize()
    {
        var stmt = (SelectStatement)SqlParser.Parse(
            "SELECT avg(v) FROM cpu WHERE time >= 1000 AND time < 2000 GROUP BY time(1m)");
        Assert.NotEmpty(stmt.GroupBy);
        var groupBy = Assert.IsType<FunctionCallExpression>(stmt.GroupBy[0]);
        Assert.Equal("time", groupBy.Name);
        Assert.Single(groupBy.Arguments);
        Assert.Equal(new DurationLiteralExpression(60_000L), groupBy.Arguments[0]);
    }

    [Fact]
    public void Parse_Select_GroupByTime_ZeroDuration_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("SELECT avg(v) FROM cpu GROUP BY time(0ms)"));
    }

    [Fact]
    public void Parse_Select_Limit_WithOptionalOffset_ParsesPagination()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT * FROM cpu LIMIT 10 OFFSET 5");

        Assert.NotNull(stmt.Pagination);
        Assert.Equal(5, stmt.Pagination!.Offset);
        Assert.Equal(10, stmt.Pagination.Fetch);
    }

    [Fact]
    public void Parse_Select_FromAliasWithQualifiedColumns_ReturnsAst()
    {
        var stmt = (SelectStatement)SqlParser.Parse(
            "SELECT c.time, c.\"usage\" FROM cpu AS c WHERE c.host = 'h1' ORDER BY c.time DESC LIMIT 2");

        Assert.Equal("cpu", stmt.Measurement);
        Assert.Equal("c", stmt.TableAlias);

        var time = Assert.IsType<IdentifierExpression>(stmt.Projections[0].Expression);
        Assert.Equal("time", time.Name);
        Assert.Equal("c", time.Qualifier);

        var usage = Assert.IsType<IdentifierExpression>(stmt.Projections[1].Expression);
        Assert.Equal("usage", usage.Name);
        Assert.Equal("c", usage.Qualifier);

        var where = Assert.IsType<BinaryExpression>(stmt.Where);
        var host = Assert.IsType<IdentifierExpression>(where.Left);
        Assert.Equal("host", host.Name);
        Assert.Equal("c", host.Qualifier);

        var orderBy = Assert.IsType<IdentifierExpression>(stmt.OrderBy!.Expression);
        Assert.Equal("time", orderBy.Name);
        Assert.Equal("c", orderBy.Qualifier);
        Assert.Equal(SortDirection.Descending, stmt.OrderBy.Direction);
    }

    [Fact]
    public void Parse_Select_FromAliasWithoutAs_ParsesAlias()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT c.time FROM cpu c");

        Assert.Equal("c", stmt.TableAlias);
        var time = Assert.IsType<IdentifierExpression>(stmt.Projections[0].Expression);
        Assert.Equal("time", time.Name);
        Assert.Equal("c", time.Qualifier);
    }

    [Fact]
    public void Parse_Select_JoinMeasurementWithTable_ReturnsJoinAst()
    {
        var stmt = (SelectStatement)SqlParser.Parse(
            "SELECT t.time, d.name FROM temperature AS t INNER JOIN devices d ON t.device_id = d.id WHERE d.tenant = 'tenant-1'");

        Assert.Equal("temperature", stmt.Measurement);
        Assert.Equal("t", stmt.TableAlias);

        Assert.NotNull(stmt.Join);
        Assert.Equal("devices", stmt.Join!.TableName);
        Assert.Equal("d", stmt.Join.Alias);

        var on = Assert.IsType<BinaryExpression>(stmt.Join.On);
        Assert.Equal(SqlBinaryOperator.Equal, on.Operator);
        Assert.Equal(new IdentifierExpression("device_id", "t"), on.Left);
        Assert.Equal(new IdentifierExpression("id", "d"), on.Right);
    }

    [Fact]
    public void Parse_Select_HybridSearchJoinDimensionTable_ReturnsTvfJoinAst()
    {
        var stmt = (SelectStatement)SqlParser.Parse("""
            SELECT measurement.time, d.site
            FROM hybrid_search(source => incidents, documents => knowledge, vector => [1, 0, 0],
                               measurement_join_tag => device_id, document_join_path => '$.device_id')
            JOIN devices d ON measurement.device_id = d.id
            WHERE d.tenant = 'tenant-1'
            """);

        Assert.Equal("incidents", stmt.Measurement);
        Assert.NotNull(stmt.TableValuedFunction);
        Assert.NotNull(stmt.Join);
        Assert.Equal("devices", stmt.Join!.TableName);
        Assert.Equal("d", stmt.Join.Alias);
    }

    [Fact]
    public void Parse_Select_OrderByTimeDesc_ParsesOrderByBeforePagination()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT time, usage FROM cpu ORDER BY time DESC LIMIT 2");

        Assert.NotNull(stmt.OrderBy);
        var id = Assert.IsType<IdentifierExpression>(stmt.OrderBy!.Expression);
        Assert.Equal("time", id.Name);
        Assert.Equal(SortDirection.Descending, stmt.OrderBy.Direction);
        Assert.NotNull(stmt.Pagination);
        Assert.Equal(2, stmt.Pagination!.Fetch);
    }

    [Fact]
    public void Parse_Select_OrderByUnsupportedColumn_Throws()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT time, usage FROM cpu ORDER BY usage DESC");

        var orderBy = Assert.IsType<IdentifierExpression>(stmt.OrderBy!.Expression);
        Assert.Equal("usage", orderBy.Name);
        Assert.Equal(SortDirection.Descending, stmt.OrderBy.Direction);
    }

    [Fact]
    public void Parse_Select_OrderByMultipleColumns_ParsesCompleteList()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT id, tenant FROM devices ORDER BY tenant ASC, id DESC LIMIT 10");

        Assert.Equal(2, stmt.OrderByList.Count);
        var tenant = Assert.IsType<IdentifierExpression>(stmt.OrderByList[0].Expression);
        Assert.Equal("tenant", tenant.Name);
        Assert.Equal(SortDirection.Ascending, stmt.OrderByList[0].Direction);
        var id = Assert.IsType<IdentifierExpression>(stmt.OrderByList[1].Expression);
        Assert.Equal("id", id.Name);
        Assert.Equal(SortDirection.Descending, stmt.OrderByList[1].Direction);
        Assert.Same(stmt.OrderBy, stmt.OrderByList[0]);
        Assert.Equal(10, stmt.Pagination!.Fetch);
    }

    [Fact]
    public void Parse_Select_OffsetFetch_ParsesPagination()
    {
        var stmt = (SelectStatement)SqlParser.Parse(
            "SELECT * FROM cpu OFFSET 7 ROWS FETCH NEXT 3 ROWS ONLY");

        Assert.NotNull(stmt.Pagination);
        Assert.Equal(7, stmt.Pagination!.Offset);
        Assert.Equal(3, stmt.Pagination.Fetch);
    }

    [Fact]
    public void Parse_Select_OffsetOnly_ParsesPaginationWithoutFetch()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT * FROM cpu OFFSET 4");

        Assert.NotNull(stmt.Pagination);
        Assert.Equal(4, stmt.Pagination!.Offset);
        Assert.Null(stmt.Pagination.Fetch);
    }

    [Fact]
    public void Parse_Select_FetchWithoutOffset_UsesZeroOffset()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT * FROM cpu FETCH FIRST 2 ROWS ONLY");

        Assert.NotNull(stmt.Pagination);
        Assert.Equal(0, stmt.Pagination!.Offset);
        Assert.Equal(2, stmt.Pagination.Fetch);
    }

    [Fact]
    public void Parse_Select_FetchMissingOnly_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("SELECT * FROM cpu OFFSET 1 ROW FETCH NEXT 2 ROWS"));
    }

    [Fact]
    public void Parse_Select_NegativeNumberInWhere()
    {
        var stmt = (SelectStatement)SqlParser.Parse("SELECT * FROM cpu WHERE value > -1.5");
        var binary = Assert.IsType<BinaryExpression>(stmt.Where);
        var negate = Assert.IsType<UnaryExpression>(binary.Right);
        Assert.Equal(SqlUnaryOperator.Negate, negate.Operator);
        Assert.Equal(LiteralExpression.Float(1.5), negate.Operand);
    }

    [Fact]
    public void Parse_Select_NotEqualOperators()
    {
        var stmt1 = (SelectStatement)SqlParser.Parse("SELECT * FROM cpu WHERE host != 'a'");
        var stmt2 = (SelectStatement)SqlParser.Parse("SELECT * FROM cpu WHERE host <> 'a'");
        var b1 = Assert.IsType<BinaryExpression>(stmt1.Where);
        var b2 = Assert.IsType<BinaryExpression>(stmt2.Where);
        Assert.Equal(SqlBinaryOperator.NotEqual, b1.Operator);
        Assert.Equal(SqlBinaryOperator.NotEqual, b2.Operator);
    }

    // ── DELETE ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Delete_WithTimeRange()
    {
        var stmt = (DeleteStatement)SqlParser.Parse(
            "DELETE FROM cpu WHERE time >= 100 AND time < 200");
        Assert.Equal("cpu", stmt.Measurement);
        var and = Assert.IsType<BinaryExpression>(stmt.Where);
        Assert.Equal(SqlBinaryOperator.And, and.Operator);
    }

    [Fact]
    public void Parse_Delete_RequiresWhere()
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse("DELETE FROM cpu"));
    }

    // ── 综合 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_AllowsTrailingSemicolon()
    {
        var stmt = SqlParser.Parse("SELECT * FROM cpu;");
        Assert.IsType<SelectStatement>(stmt);
    }

    [Fact]
    public void Parse_TrailingGarbage_Throws()
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse("SELECT * FROM cpu alias garbage"));
    }

    [Fact]
    public void Parse_Select_JoinSyntax_RequiresOn()
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse("SELECT * FROM cpu c JOIN memory"));
    }

    [Fact]
    public void Parse_UnknownStatement_Throws()
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse("UPSERT cpu SET value = 1"));
    }

    [Fact]
    public void ParseScript_MultipleStatements_AreReturnedInOrder()
    {
        var stmts = SqlParser.ParseScript(
            "CREATE MEASUREMENT m (h TAG, v FIELD FLOAT); INSERT INTO m (h, v) VALUES ('a', 1); SELECT * FROM m");
        Assert.Equal(3, stmts.Count);
        Assert.IsType<CreateMeasurementStatement>(stmts[0]);
        Assert.IsType<InsertStatement>(stmts[1]);
        Assert.IsType<SelectStatement>(stmts[2]);
    }

    // ── 递归深度上限（防 StackOverflow 崩进程）─────────────────────────────

    [Fact]
    public void Parse_DeeplyNestedParentheses_ThrowsInsteadOfStackOverflow()
    {
        var where = new string('(', 5000) + "1 = 1" + new string(')', 5000);
        Assert.Throws<SqlParseException>(() => SqlParser.Parse($"SELECT * FROM cpu WHERE {where}"));
    }

    [Fact]
    public void Parse_DeepNotChain_ThrowsInsteadOfStackOverflow()
    {
        var where = string.Concat(Enumerable.Repeat("NOT ", 5000)) + "ok";
        Assert.Throws<SqlParseException>(() => SqlParser.Parse($"SELECT * FROM cpu WHERE {where}"));
    }

    [Fact]
    public void Parse_DeepUnaryMinusChain_ThrowsInsteadOfStackOverflow()
    {
        var expr = new string('-', 5000) + "1";
        Assert.Throws<SqlParseException>(() => SqlParser.Parse($"SELECT * FROM cpu WHERE value > {expr}"));
    }

    [Fact]
    public void Parse_ModeratelyNestedExpression_StillParses()
    {
        // 远低于上限（200）的合法嵌套必须正常解析，不能误伤。
        var where = new string('(', 50) + "value > 1" + new string(')', 50);
        var stmt = (SelectStatement)SqlParser.Parse($"SELECT * FROM cpu WHERE {where}");
        Assert.NotNull(stmt.Where);
    }

    [Fact]
    public void Parse_LongFlatAndChain_StillParses()
    {
        // 扁平 AND 链走 while 循环而非递归，长链不应触发深度上限。
        var predicates = string.Join(" AND ", Enumerable.Range(0, 500).Select(i => $"v{i} = {i}"));
        var stmt = (SelectStatement)SqlParser.Parse($"SELECT * FROM cpu WHERE {predicates}");
        Assert.NotNull(stmt.Where);
    }
}
