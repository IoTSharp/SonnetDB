using SonnetDB.Contracts;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Json;
using SonnetDB.Storage.Format;

namespace SonnetDB.Endpoints;

/// <summary>
/// 提供 <c>GET /v1/db/{db}/schema</c> 的响应构造逻辑。
/// </summary>
internal static class SchemaEndpointHandler
{
    /// <summary>
    /// 生成指定数据库的 schema 快照响应。
    /// </summary>
    public static IResult Handle(string db, Tsdb tsdb)
    {
        ArgumentException.ThrowIfNullOrEmpty(db);
        ArgumentNullException.ThrowIfNull(tsdb);

        var measurements = BuildMeasurements(tsdb);
        var tables = BuildTables(tsdb);
        var documents = BuildDocuments(tsdb);
        var indexes = BuildIndexes(measurements, tables, documents);
        var backupStatus = BuildBackupStatus(tsdb);

        return Results.Json(
            new SchemaResponse(measurements, tables, documents, indexes, backupStatus),
            ServerJsonContext.Default.SchemaResponse);
    }

    private static List<MeasurementInfo> BuildMeasurements(Tsdb tsdb)
    {
        var schemas = tsdb.Measurements.Snapshot();
        var infos = new List<MeasurementInfo>(schemas.Count);
        foreach (var measurement in schemas)
        {
            var columns = new List<ColumnInfo>(measurement.Columns.Count);
            foreach (var column in measurement.Columns)
            {
                columns.Add(new ColumnInfo(
                    column.Name,
                    column.Role.ToString(),
                    column.DataType.ToString(),
                    column.VectorDimension,
                    column.VectorIndex is null ? null : MapVectorIndex(column.VectorIndex)));
            }

            infos.Add(new MeasurementInfo(measurement.Name, columns));
        }

        return infos;
    }

    private static List<TableInfo> BuildTables(Tsdb tsdb)
    {
        var schemas = tsdb.Tables.Catalog.Snapshot();
        var result = new List<TableInfo>(schemas.Count);
        foreach (var schema in schemas)
        {
            var columns = new List<TableColumnInfo>(schema.Columns.Count);
            foreach (var column in schema.Columns)
            {
                columns.Add(new TableColumnInfo(
                    column.Name,
                    column.DataType.ToString(),
                    column.IsPrimaryKey,
                    column.IsNullable,
                    column.Ordinal));
            }

            var indexes = new List<TableIndexInfo>(schema.Indexes.Count);
            foreach (var index in schema.Indexes)
            {
                indexes.Add(new TableIndexInfo(
                    index.Name,
                    index.Columns.ToList(),
                    index.IsUnique,
                    ToDateTimeOffset(index.CreatedAtUtcTicks),
                    Rebuildable: true,
                    JsonPath: index.JsonPath));
            }

            result.Add(new TableInfo(
                schema.Name,
                columns,
                schema.PrimaryKey.ToList(),
                indexes,
                ToDateTimeOffset(schema.CreatedAtUtcTicks)));
        }

        return result;
    }

    private static List<DocumentCollectionInfo> BuildDocuments(Tsdb tsdb)
    {
        var schemas = tsdb.Documents.Catalog.Snapshot();
        var result = new List<DocumentCollectionInfo>(schemas.Count);
        foreach (var schema in schemas)
        {
            var jsonIndexes = new List<DocumentJsonIndexInfo>(schema.Indexes.Count);
            foreach (var index in schema.Indexes)
            {
                jsonIndexes.Add(new DocumentJsonIndexInfo(
                    index.Name,
                    index.Path,
                    ToDateTimeOffset(index.CreatedAtUtcTicks),
                    Rebuildable: true,
                    Paths: index.Paths.ToList(),
                    IsUnique: index.IsUnique,
                    IsSparse: index.IsSparse,
                    IsPartial: index.PartialFilter is not null,
                    PartialFilter: FormatDocumentPartialFilter(index.PartialFilter),
                    IsTtl: index.IsTtl,
                    TtlSeconds: index.TtlSeconds));
            }

            var fullTextIndexes = new List<DocumentFullTextIndexInfo>(schema.FullTextIndexes.Count);
            foreach (var index in schema.FullTextIndexes)
            {
                fullTextIndexes.Add(new DocumentFullTextIndexInfo(
                    index.Name,
                    index.Fields.ToList(),
                    index.Tokenizer,
                    ToDateTimeOffset(index.CreatedAtUtcTicks),
                    IncludedInBackup: true,
                    Rebuildable: true));
            }

            result.Add(new DocumentCollectionInfo(
                schema.Name,
                jsonIndexes,
                fullTextIndexes,
                ToDateTimeOffset(schema.CreatedAtUtcTicks)));
        }

        return result;
    }

    private static List<IndexLifecycleInfo> BuildIndexes(
        IReadOnlyList<MeasurementInfo> measurements,
        IReadOnlyList<TableInfo> tables,
        IReadOnlyList<DocumentCollectionInfo> documents)
    {
        var indexes = new List<IndexLifecycleInfo>();
        foreach (var table in tables)
        {
            foreach (var index in table.Indexes)
            {
                indexes.Add(new IndexLifecycleInfo(
                    Id: $"table:{table.Name}:{index.Name}",
                    Model: "table",
                    Owner: table.Name,
                    Name: index.Name,
                    Kind: string.IsNullOrWhiteSpace(index.JsonPath)
                        ? index.IsUnique ? "unique_secondary" : "secondary"
                        : "json_path",
                    State: "ready",
                    IncludedInBackup: true,
                    Rebuildable: index.Rebuildable,
                    CreatedUtc: index.CreatedUtc,
                    Columns: index.Columns,
                    Detail: index.JsonPath));
            }
        }

        foreach (var collection in documents)
        {
            foreach (var index in collection.JsonIndexes)
            {
                indexes.Add(new IndexLifecycleInfo(
                    Id: $"document:{collection.Name}:{index.Name}",
                    Model: "document",
                    Owner: collection.Name,
                    Name: index.Name,
                    Kind: DocumentIndexKind(index),
                    State: "ready",
                    IncludedInBackup: true,
                    Rebuildable: index.Rebuildable,
                    CreatedUtc: index.CreatedUtc,
                    Columns: index.Paths ?? [index.Path],
                    Detail: FormatDocumentIndexDetail(index)));
            }

            foreach (var index in collection.FullTextIndexes)
            {
                indexes.Add(new IndexLifecycleInfo(
                    Id: $"document:{collection.Name}:{index.Name}",
                    Model: "document",
                    Owner: collection.Name,
                    Name: index.Name,
                    Kind: "fulltext",
                    State: "ready",
                    IncludedInBackup: index.IncludedInBackup,
                    Rebuildable: index.Rebuildable,
                    CreatedUtc: index.CreatedUtc,
                    Columns: index.Fields,
                    Detail: index.Tokenizer));
            }
        }

        foreach (var measurement in measurements)
        {
            foreach (var column in measurement.Columns)
            {
                if (column.DataType != FieldType.Vector.ToString() || column.VectorIndex is null)
                    continue;

                indexes.Add(new IndexLifecycleInfo(
                    Id: $"measurement:{measurement.Name}:{column.Name}",
                    Model: "measurement",
                    Owner: measurement.Name,
                    Name: column.Name,
                    Kind: "vector:" + column.VectorIndex.Kind,
                    State: "planned",
                    IncludedInBackup: true,
                    Rebuildable: true,
                    CreatedUtc: null,
                    Columns: [column.Name],
                    Detail: column.VectorDimension is null ? null : $"dim={column.VectorDimension.Value}"));
            }
        }

        return indexes;
    }

    private static BackupStatusInfo BuildBackupStatus(Tsdb tsdb)
    {
        var root = tsdb.RootDirectory;
        var manifestPath = Path.Combine(root, SonnetDB.Backup.BackupManifest.FileName);
        var manifestCreatedUtc = File.Exists(manifestPath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(manifestPath), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        return new BackupStatusInfo(
            BackupCapable: true,
            HasRestoreManifest: manifestCreatedUtc.HasValue,
            RestoreManifestCreatedUtc: manifestCreatedUtc,
            SegmentCount: tsdb.Segments.SegmentCount,
            WalFileCount: CountFiles(TsdbPaths.WalDir(root), "*.SDBWAL"),
            TotalBytes: SumDirectoryBytes(root),
            MemTablePointCount: tsdb.MemTable.PointCount,
            CheckpointLsn: tsdb.CheckpointLsn,
            NextSegmentId: tsdb.NextSegmentId);
    }

    private static VectorIndexInfo MapVectorIndex(SonnetDB.Catalog.VectorIndexDefinition index)
    {
        var options = new List<KeyValueInfo>();
        switch (index.Kind)
        {
            case SonnetDB.Catalog.VectorIndexKind.Hnsw:
                if (index.Hnsw is not null)
                {
                    options.Add(new KeyValueInfo("m", index.Hnsw.M.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    options.Add(new KeyValueInfo("ef", index.Hnsw.Ef.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
                break;
            case SonnetDB.Catalog.VectorIndexKind.IvfFlat:
                if (index.Ivf is not null)
                {
                    options.Add(new KeyValueInfo("nlist", index.Ivf.NList.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    options.Add(new KeyValueInfo("nprobe", index.Ivf.NProbe.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    options.Add(new KeyValueInfo("max_iterations", index.Ivf.MaxIterations.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
                break;
            case SonnetDB.Catalog.VectorIndexKind.IvfPq:
                if (index.IvfPq is not null)
                {
                    options.Add(new KeyValueInfo("nlist", index.IvfPq.NList.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    options.Add(new KeyValueInfo("nprobe", index.IvfPq.NProbe.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    options.Add(new KeyValueInfo("max_iterations", index.IvfPq.MaxIterations.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    options.Add(new KeyValueInfo("m", index.IvfPq.M.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    options.Add(new KeyValueInfo("nbits", index.IvfPq.NBits.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
                break;
            case SonnetDB.Catalog.VectorIndexKind.Vamana:
                if (index.Vamana is not null)
                {
                    options.Add(new KeyValueInfo("max_degree", index.Vamana.MaxDegree.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    options.Add(new KeyValueInfo("search_list_size", index.Vamana.SearchListSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    options.Add(new KeyValueInfo("alpha", index.Vamana.Alpha.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    options.Add(new KeyValueInfo("beam_width", index.Vamana.BeamWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
                break;
        }

        return new VectorIndexInfo(index.Kind.ToString(), options);
    }

    private static DateTimeOffset ToDateTimeOffset(long ticks)
        => new(new DateTime(ticks, DateTimeKind.Utc));

    private static string DocumentIndexKind(DocumentJsonIndexInfo index)
    {
        if (index.IsTtl)
            return "ttl";
        if (index.IsUnique)
            return "unique_document";
        if (index.IsPartial)
            return "partial_document";
        if (index.IsSparse)
            return "sparse_document";
        return index.Paths is { Count: > 1 } ? "compound_document" : "document";
    }

    private static string FormatDocumentIndexDetail(DocumentJsonIndexInfo index)
    {
        var parts = new List<string>
        {
            "paths=" + string.Join(",", index.Paths ?? [index.Path]),
        };
        if (index.IsUnique)
            parts.Add("unique=true");
        if (index.IsSparse)
            parts.Add("sparse=true");
        if (!string.IsNullOrWhiteSpace(index.PartialFilter))
            parts.Add("partial=" + index.PartialFilter);
        if (index.IsTtl)
            parts.Add("ttl_seconds=" + index.TtlSeconds);
        return string.Join(";", parts);
    }

    private static string? FormatDocumentPartialFilter(DocumentIndexPartialFilter? filter)
    {
        if (filter is null)
            return null;

        string op = filter.Operator switch
        {
            DocumentIndexPartialFilterOperator.Exists => "exists",
            DocumentIndexPartialFilterOperator.Equal => "=",
            DocumentIndexPartialFilterOperator.NotEqual => "!=",
            DocumentIndexPartialFilterOperator.GreaterThan => ">",
            DocumentIndexPartialFilterOperator.GreaterThanOrEqual => ">=",
            DocumentIndexPartialFilterOperator.LessThan => "<",
            DocumentIndexPartialFilterOperator.LessThanOrEqual => "<=",
            _ => filter.Operator.ToString(),
        };
        return filter.Operator == DocumentIndexPartialFilterOperator.Exists
            ? $"{filter.Path} exists {filter.ValueScalar ?? "true"}"
            : $"{filter.Path} {op} {filter.ValueScalar}";
    }

    private static int CountFiles(string directory, string searchPattern)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly).Count()
            : 0;

    private static long SumDirectoryBytes(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return bytes;
    }
}
