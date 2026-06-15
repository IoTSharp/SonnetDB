using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Relational;

/// <summary>
/// <c>information_schema</c> 自省场景：验证表、列和索引元数据可查询。
/// </summary>
public sealed class InformationSchemaIntrospectionScenario : RelationalScenarioBase
{
    /// <inheritdoc />
    public override string Name => "information_schema_introspection";

    /// <inheritdoc />
    public override Capability Required => Capability.Relational | Capability.SqlInformationSchema;

    /// <inheritdoc />
    protected override IReadOnlyList<string> TablesToDrop => ["rel_meta"];

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunRelationalAsync(IRelationalOps ops, ScenarioContext ctx)
    {
        var ct = ctx.Cancellation;
        await ops.ExecuteAsync($"""
            CREATE TABLE rel_meta (
                id {IntType(ops)},
                tenant {StringType(ops)},
                name {StringType(ops)},
                PRIMARY KEY (id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync("CREATE INDEX idx_rel_meta_tenant ON rel_meta (tenant)", ct).ConfigureAwait(false);

        var result = ops.Dialect == RelationalDialect.Postgres
            ? await QueryPostgresMetadataAsync(ops, ct).ConfigureAwait(false)
            : await QuerySonnetDbMetadataAsync(ops, ct).ConfigureAwait(false);

        return ScenarioFromRows(
            result,
            Row("columns", 3L),
            Row("indexes", 1L),
            Row("tables", 1L));
    }

    private static async Task<RelationalSqlResult> QuerySonnetDbMetadataAsync(IRelationalOps ops, CancellationToken ct)
    {
        var tables = await ops.QueryAsync("""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_name = 'rel_meta'
            """, ct).ConfigureAwait(false);
        var columns = await ops.QueryAsync("""
            SELECT column_name
            FROM information_schema.columns
            WHERE table_name = 'rel_meta'
            """, ct).ConfigureAwait(false);
        var indexes = await ops.QueryAsync("""
            SELECT index_name
            FROM information_schema.indexes
            WHERE table_name = 'rel_meta'
            """, ct).ConfigureAwait(false);

        return MetadataSummary(tables.Rows.Count, columns.Rows.Count, indexes.Rows.Count);
    }

    private static async Task<RelationalSqlResult> QueryPostgresMetadataAsync(IRelationalOps ops, CancellationToken ct)
    {
        var tables = await ops.QueryAsync("""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = 'rel_meta'
            """, ct).ConfigureAwait(false);
        var columns = await ops.QueryAsync("""
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'rel_meta'
            """, ct).ConfigureAwait(false);
        var indexes = await ops.QueryAsync("""
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = 'rel_meta' AND indexname = 'idx_rel_meta_tenant'
            """, ct).ConfigureAwait(false);

        return MetadataSummary(tables.Rows.Count, columns.Rows.Count, indexes.Rows.Count);
    }

    private static RelationalSqlResult MetadataSummary(long tableCount, long columnCount, long indexCount)
        => new(
            ["object_type", "count"],
            [Row("columns", columnCount), Row("indexes", indexCount), Row("tables", tableCount)],
            -1);
}
