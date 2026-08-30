using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.FullText;
using SonnetDB.Generations;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Compatibility;

public sealed class PublicApiCompatibilityTests
{
    [Fact]
    public void DocumentFullTextFilteredSearch_ExtendOnlyPublicContract_IsConsumable()
    {
        Assert.NotNull(typeof(DocumentCollectionStore).GetMethod(
            nameof(DocumentCollectionStore.SearchFullTextFiltered),
            [
                typeof(DocumentFullTextIndex),
                typeof(string),
                typeof(string),
                typeof(int),
                typeof(IReadOnlySet<string>),
                typeof(long),
                typeof(CancellationToken),
            ]));
        Assert.Equal(
            typeof(IReadOnlyList<DocumentFullTextSearchHit>),
            typeof(DocumentFullTextFilteredSearchResult)
                .GetProperty(nameof(DocumentFullTextFilteredSearchResult.Hits))!
                .PropertyType);
        Assert.Equal(
            typeof(bool),
            typeof(DocumentFullTextFilteredSearchResult)
                .GetProperty(nameof(DocumentFullTextFilteredSearchResult.PostingBudgetExceeded))!
                .PropertyType);
    }

    [Fact]
    public void DatabaseGeneration_ExtendOnlyPublicContract_IsConsumable()
    {
        var resource = new DatabaseGenerationResource(
            "state",
            DatabaseGenerationResourceKind.KvKeyspace,
            "workspace-a");
        var request = new DatabaseGenerationPublishRequest
        {
            Stream = "workspace",
            GenerationId = "commit-a",
            ExpectedRevision = 0,
            Resources = [resource],
        };

        Assert.Equal("workspace", request.Stream);
        Assert.Equal("commit-a", request.GenerationId);
        Assert.Equal(0, request.ExpectedRevision);
        Assert.Same(resource, Assert.Single(request.Resources));
        Assert.Equal(typeof(DatabaseGenerationManager), typeof(Tsdb).GetProperty(nameof(Tsdb.Generations))!.PropertyType);
        Assert.NotNull(typeof(DatabaseGenerationManager).GetMethod(
            nameof(DatabaseGenerationManager.Publish),
            [typeof(DatabaseGenerationPublishRequest), typeof(CancellationToken)]));
        Assert.NotNull(typeof(DatabaseGenerationManager).GetMethod(
            nameof(DatabaseGenerationManager.AcquireActive),
            [typeof(string)]));
        Assert.NotNull(typeof(DatabaseGenerationManager).GetMethod(
            nameof(DatabaseGenerationManager.Acquire),
            [typeof(string), typeof(long)]));
        Assert.NotNull(typeof(DatabaseGenerationManager).GetMethod(
            nameof(DatabaseGenerationManager.CleanupRetired),
            [typeof(string), typeof(CancellationToken)]));
        Assert.NotNull(typeof(DatabaseGenerationManager).GetMethod(
            nameof(DatabaseGenerationManager.CleanupRetired),
            [
                typeof(string),
                typeof(DatabaseGenerationCleanupOptions),
                typeof(CancellationToken),
            ]));
        var cleanupOptions = new DatabaseGenerationCleanupOptions(
            new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.FromHours(8)));
        Assert.Equal(TimeSpan.Zero, cleanupOptions.PublishedBeforeUtc.Offset);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 30, 4, 0, 0, TimeSpan.Zero),
            cleanupOptions.PublishedBeforeUtc);
        Assert.NotNull(typeof(DatabaseGenerationCleanupResult).GetProperty(
            nameof(DatabaseGenerationCleanupResult.RetentionDeferredRevisions)));
        Assert.Equal(1, (int)DatabaseGenerationResourceKind.KvKeyspace);
        Assert.Equal(2, (int)DatabaseGenerationResourceKind.DocumentCollection);
        Assert.Equal(3, (int)DatabaseGenerationResourceKind.DocumentFullTextIndex);
    }

    [Fact]
    public void TokenKind_NewMembers_PreserveVersion301NumericValues()
    {
        Assert.Equal(47, (int)TokenKind.KeywordFrom);
        Assert.Equal(60, (int)TokenKind.KeywordUpdate);
        Assert.Equal(111, (int)TokenKind.KeywordReferences);
        Assert.Equal(135, (int)TokenKind.KeywordTransaction);

        Assert.Equal(136, (int)TokenKind.KeywordUnion);
        Assert.Equal(137, (int)TokenKind.KeywordTruncate);
        Assert.Equal(138, (int)TokenKind.KeywordCheck);
    }

    [Fact]
    public void CreateTableStatement_Version301Contract_ConstructsAndDeconstructs()
    {
        IReadOnlyList<TableColumnDefinition> columns = Array.Empty<TableColumnDefinition>();
        IReadOnlyList<string> primaryKey = ["id"];
        IReadOnlyList<TableForeignKeyClause> foreignKeys = Array.Empty<TableForeignKeyClause>();

        var statement = new CreateTableStatement(
            "devices",
            columns,
            primaryKey,
            true,
            foreignKeys);

        var (name, actualColumns, actualPrimaryKey, ifNotExists, actualForeignKeys) = statement;

        Assert.Equal("devices", name);
        Assert.Same(columns, actualColumns);
        Assert.Same(primaryKey, actualPrimaryKey);
        Assert.True(ifNotExists);
        Assert.Same(foreignKeys, actualForeignKeys);
        Assert.Empty(statement.CheckConstraintClauses);
    }

    [Fact]
    public void SelectStatement_Version301Contract_ConstructsAndDeconstructs()
    {
        IReadOnlyList<SelectItem> projections = Array.Empty<SelectItem>();
        IReadOnlyList<SqlExpression> groupBy = Array.Empty<SqlExpression>();

        var statement = new SelectStatement(
            projections,
            "cpu",
            null,
            groupBy,
            null,
            null,
            null,
            "c",
            null,
            null,
            null,
            null,
            null,
            true);

        var (
            actualProjections,
            measurement,
            where,
            actualGroupBy,
            tableValuedFunction,
            pagination,
            orderBy,
            tableAlias,
            join,
            fromSubquery,
            joins,
            having,
            orderByItems,
            distinct) = statement;

        Assert.Same(projections, actualProjections);
        Assert.Equal("cpu", measurement);
        Assert.Null(where);
        Assert.Same(groupBy, actualGroupBy);
        Assert.Null(tableValuedFunction);
        Assert.Null(pagination);
        Assert.Null(orderBy);
        Assert.Equal("c", tableAlias);
        Assert.Null(join);
        Assert.Null(fromSubquery);
        Assert.Null(joins);
        Assert.Null(having);
        Assert.Null(orderByItems);
        Assert.True(distinct);
        Assert.Empty(statement.UnionStatements);
    }

    [Fact]
    public void SqlExplainExecutionResult_Version301Contract_ConstructsAndDeconstructs()
    {
        var result = new SqlExplainExecutionResult(
            "main",
            "select",
            "cpu",
            1,
            2,
            3,
            4,
            5,
            6,
            true,
            7,
            "segment_scan",
            "idx_cpu",
            null);

        var (
            database,
            statementType,
            measurement,
            matchedSeriesCount,
            estimatedSegmentCount,
            estimatedBlockCount,
            estimatedScannedRows,
            estimatedMemTableRows,
            estimatedSegmentRows,
            hasTimeFilter,
            tagFilterCount,
            accessPath,
            indexName,
            documentPlan) = result;

        Assert.Equal("main", database);
        Assert.Equal("select", statementType);
        Assert.Equal("cpu", measurement);
        Assert.Equal(1, matchedSeriesCount);
        Assert.Equal(2, estimatedSegmentCount);
        Assert.Equal(3, estimatedBlockCount);
        Assert.Equal(4, estimatedScannedRows);
        Assert.Equal(5, estimatedMemTableRows);
        Assert.Equal(6, estimatedSegmentRows);
        Assert.True(hasTimeFilter);
        Assert.Equal(7, tagFilterCount);
        Assert.Equal("segment_scan", accessPath);
        Assert.Equal("idx_cpu", indexName);
        Assert.Null(documentPlan);
        Assert.Null(result.ScanFilter);
    }

    [Fact]
    public void TableSchemaCreate_Version301Contract_CreatesSchema()
    {
        IReadOnlyList<(string Name, TableColumnType DataType, bool IsNullable)> columns =
            [("id", TableColumnType.Int64, false)];
        IReadOnlyList<string> primaryKey = ["id"];
        IReadOnlyList<TableIndexDefinition> indexes = Array.Empty<TableIndexDefinition>();
        IReadOnlyList<TableForeignKeyDefinition> foreignKeys = Array.Empty<TableForeignKeyDefinition>();
        IReadOnlySet<string> rowVersionColumns = new HashSet<string>(StringComparer.Ordinal);

        TableSchema schema = TableSchema.Create(
            "devices",
            columns,
            primaryKey,
            indexes,
            foreignKeys,
            rowVersionColumns,
            1234);

        Assert.Equal("devices", schema.Name);
        Assert.Equal(1234, schema.CreatedAtUtcTicks);
        Assert.Empty(schema.CheckConstraints);
    }
}
