namespace SonnetDB.IoTSharpCompat.Tests;

internal static class IoTSharpCompatMatrix
{
    public static IReadOnlyList<CompatDomain> Domains { get; } =
    [
        new(
            "Relational",
            [
                new("PostgreSQL", "已接入", "DataBase=PostgreSql; EF Core migrations baseline."),
                new("MySQL", "已接入", "DataBase=MySql; verify charset, datetime precision and pagination."),
                new("SQLServer", "已接入", "DataBase=SqlServer; verify Identity and transaction semantics."),
                new("SQLite", "已接入", "DataBase=Sqlite; local bootstrap baseline."),
                new("Oracle", "需验证", "DataBase=Oracle; identifier, sequence and paging differences."),
                new("Cassandra", "需验证", "DataBase=Cassandra; non-relational semantics risk."),
                new("ClickHouse", "需验证", "DataBase=ClickHouse; OLAP semantics risk.")
            ],
            [
                "schema migration upgrade and rollback",
                "Identity login, role and JWT query",
                "tenant, customer, device, asset and rule CRUD",
                "Include, paging, ordering and filter translation",
                "transaction rollback after partial SaveChanges failure"
            ]),
        new(
            "TimeSeries",
            [
                new("SingleTable", "已接入", "TelemetryStorage=SingleTable."),
                new("Sharding", "已接入", "TelemetryStorage=Sharding; SonnetDB table sharding minimal CRUD baseline exists."),
                new("InfluxDB", "已接入", "TelemetryStorage=InfluxDB."),
                new("TimescaleDB", "已接入", "TelemetryStorage=TimescaleDB."),
                new("Taos", "已接入", "TelemetryStorage=Taos."),
                new("IoTDB", "已接入", "TelemetryStorage=IoTDB."),
                new("SonnetDB", "已接入", "TelemetryStorage=SonnetDB; telemetry adapter exists.")
            ],
            [
                "latest telemetry for all keys",
                "latest telemetry for selected keys",
                "range query by device, key and UTC time",
                "range aggregation for None, Mean, Median, Last, First, Max, Min and Sum",
                "type mapping for Boolean, String, Long, Double, Json, XML, Binary and DateTime"
            ]),
        new(
            "Cache",
            [
                new("Redis", "已接入", "CachingUseIn=Redis via EasyCaching."),
                new("LiteDB", "已接入", "CachingUseIn=LiteDB via EasyCaching."),
                new("InMemory", "已接入", "CachingUseIn=InMemory default."),
                new("SQLite", "需验证", "CachingUseIn=SQlite enum exists but startup registration is not explicit."),
                new("SonnetDB", "已接入", "CachingUseIn=SonnetDB via SonnetDB.Caching EasyCaching provider; optional IDistributedCache provider exists.")
            ],
            [
                "set get remove exists",
                "ttl expiration after restart",
                "sliding expiration refresh",
                "concurrent key write and read",
                "provider health check and configuration failure"
            ]),
        new(
            "ObjectBucket",
            [
                new("BlobStorage", "已接入", "Storage.Net IBlobStorage with disk fallback."),
                new("S3", "需验证", "S3-compatible connection string should match Storage.Net behavior."),
                new("SonnetDB", "已接入", "SonnetDB S3-compatible object bucket API plus IoTSharp SonnetDbBlobStorage adapter.")
            ],
            [
                "object upload download overwrite and delete",
                "object metadata verify for size etag sha256 content-type",
                "range read",
                "multipart upload",
                "presigned URL",
                "object versioning and delete marker",
                "bucket lifecycle apply",
                "object audit list"
            ]),
        new(
            "VectorSearch",
            [
                new("KNN", "规划接入", "SonnetDB supports knn table-valued function; IoTSharp has no current vector backend."),
                new("VectorIndex", "规划接入", "SonnetDB supports vector index lifecycle; IoTSharp integration is future work."),
                new("HybridSearch", "规划接入", "SonnetDB can combine vector and text scores.")
            ],
            [
                "vector dimension validation",
                "topK distance ordering",
                "metric coverage for cosine, l2 and inner product",
                "index rebuild after backup restore",
                "tenant-safe filtering with tags, time and relation joins"
            ]),
        new(
            "FullTextSearch",
            [
                new("FullTextIndex", "规划接入", "SonnetDB document collection fulltext indexes are available."),
                new("BM25", "规划接入", "SonnetDB exposes bm25_score for ordering."),
                new("ChineseTokenizer", "规划接入", "SonnetDB CJK/Jieba tokenizer path must be validated.")
            ],
            [
                "create drop and show fulltext index",
                "match query for Chinese English numeric and mixed tokens",
                "bm25 ordering",
                "pagination stability",
                "index rebuild after document delete and restore"
            ])
    ];

    public static IReadOnlyList<string> MigrationAndRollbackChecklist { get; } =
    [
        "relational migrate schema, indexes, constraints and data",
        "relational rollback by restoring original connection string and validating Identity login",
        "timeseries dual-write latest, range and aggregate comparison",
        "timeseries rollback by treating the original telemetry store as source of truth",
        "cache cold-start is allowed for volatile namespaces",
        "object metadata verify before content cutover",
        "object rollback by switching BlobStorage or S3 connection string back",
        "search index rebuild from source documents and embeddings"
    ];

    public static IReadOnlyList<string> RelationalSonnetDbUnsupported { get; } =
    [
        "HealthChecks UI does not yet have durable SonnetDB storage; IoTSharp.Data.SonnetDB currently uses in-memory HealthChecks UI storage.",
        "SonnetDB EF migrations history supports __EFMigrationsHistory and configurable history tables, but distributed cross-process migration locking is not yet implemented.",
        "IoTSharp.Data.SonnetDB has not yet checked in a full provider-specific production migration baseline; current ApplicationDbContext compatibility uses provider history plus EnsureCreated schema coverage."
    ];
}

internal sealed record CompatDomain(
    string Name,
    IReadOnlyList<CompatBackend> Backends,
    IReadOnlyList<string> AcceptanceCases);

internal sealed record CompatBackend(string Name, string Status, string Notes);
