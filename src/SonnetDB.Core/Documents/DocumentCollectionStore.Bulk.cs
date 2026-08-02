using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Kv;

namespace SonnetDB.Documents;

public sealed partial class DocumentCollectionStore
{
    private const int MaxBulkOperations = 1000;
    private const int MaxBulkPayloadBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan BulkJournalRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// 在单个 collection 内执行 mixed insert/replace/update/delete 批量写。
    /// </summary>
    /// <param name="operations">按请求顺序执行的操作。</param>
    /// <param name="ordered">为 true 时任一错误使整个请求零文档提交；为 false 时跳过失败项并原子提交其余成功项。</param>
    /// <param name="requestId">可选幂等键；24 小时内相同键与相同请求会返回持久化结果，不会重复执行。</param>
    /// <returns>总计、逐项状态、稳定错误码与重放状态。</returns>
    public DocumentWriteResult BulkWrite(
        IEnumerable<DocumentBulkWriteOperation> operations,
        bool ordered = true,
        string? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var materialized = operations.ToArray();
        if (materialized.Length == 0)
            throw new ArgumentException("bulk operations 不能为空。", nameof(operations));
        for (int index = 0; index < materialized.Length; index++)
        {
            if (materialized[index] is null)
                throw new ArgumentException($"bulk operation {index} 不可为空。", nameof(operations));
        }
        ValidateBulkRequestId(requestId);
        if (materialized.Length > MaxBulkOperations)
        {
            return BuildBulkBatchTooLargeResult(
                materialized,
                requestId,
                $"单次 bulk 最多允许 {MaxBulkOperations} 个操作。");
        }

        string fingerprint;
        try
        {
            fingerprint = ComputeBulkFingerprint(materialized, ordered);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BuildBulkBatchTooLargeResult(materialized, requestId, ex.Message);
        }

        try
        {
            lock (_sync)
            {
                PurgeExpiredDocumentsLocked();
                RepairDerivedIndexesIfNeededLocked();

                if (requestId is not null && TryReadBulkJournalLocked(requestId) is { } journal)
                {
                    if (string.Equals(journal.Fingerprint, fingerprint, StringComparison.Ordinal))
                        return CopyBulkResult(journal.Result, replayed: true);

                    var conflict = new DocumentWriteError(
                        -1,
                        null,
                        DocumentWriteErrorCodes.IdempotencyConflict,
                        $"requestId '{requestId}' 已绑定到不同的 bulk 请求。");
                    return new DocumentWriteResult(
                        errors: [conflict],
                        items: [],
                        requestId: requestId,
                        committed: false);
                }

                var virtualRows = LoadBulkVirtualRowsLocked(_schema, materialized);

                var plannedMutations = new List<PendingDocumentMutation>();
                var items = new List<DocumentBulkWriteItemResult>(materialized.Length);
                var errors = new List<DocumentWriteError>();

                for (int index = 0; index < materialized.Length; index++)
                {
                    DocumentBulkWriteOperation operation = materialized[index]
                        ?? throw new ArgumentException($"bulk operation {index} 不可为空。", nameof(operations));
                    try
                    {
                        var planned = PlanBulkOperationLocked(_schema, operation, index, virtualRows);
                        foreach (var change in planned.FinalRows)
                        {
                            if (change.Value is null)
                                virtualRows.Remove(change.Key);
                            else
                                virtualRows[change.Key] = change.Value;
                        }

                        plannedMutations.AddRange(planned.Mutations);
                        items.Add(planned.Item);
                        if (planned.Warnings.Count != 0)
                            errors.AddRange(planned.Warnings);
                    }
                    catch (BulkPlanningException ex)
                    {
                        var error = new DocumentWriteError(index, ex.Id ?? operation.Id, ex.Code, ex.Message);
                        errors.Add(error);
                        items.Add(new DocumentBulkWriteItemResult(
                            index,
                            BulkOperationName(operation.Type),
                            ex.Id ?? operation.Id,
                            DocumentBulkWriteItemStatuses.Failed,
                            Error: error));

                        if (ordered)
                        {
                            MarkOrderedItemsNotAttempted(items, materialized, index);
                            var failed = BuildBulkResult(items, errors, requestId, committed: false);
                            PersistBulkJournalOnlyLocked(requestId, fingerprint, failed);
                            return failed;
                        }
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        var error = new DocumentWriteError(
                            index,
                            operation.Id,
                            DocumentWriteErrorCodes.DocumentTooLarge,
                            ex.Message);
                        errors.Add(error);
                        items.Add(new DocumentBulkWriteItemResult(
                            index,
                            BulkOperationName(operation.Type),
                            operation.Id,
                            DocumentBulkWriteItemStatuses.Failed,
                            Error: error));
                        if (ordered)
                        {
                            MarkOrderedItemsNotAttempted(items, materialized, index);
                            var failed = BuildBulkResult(items, errors, requestId, committed: false);
                            PersistBulkJournalOnlyLocked(requestId, fingerprint, failed);
                            return failed;
                        }
                    }
                    catch (JsonException ex)
                    {
                        var error = new DocumentWriteError(
                            index,
                            operation.Id,
                            DocumentWriteErrorCodes.ValidationFailed,
                            ex.Message);
                        errors.Add(error);
                        items.Add(new DocumentBulkWriteItemResult(
                            index,
                            BulkOperationName(operation.Type),
                            operation.Id,
                            DocumentBulkWriteItemStatuses.Failed,
                            Error: error));
                        if (ordered)
                        {
                            MarkOrderedItemsNotAttempted(items, materialized, index);
                            var failed = BuildBulkResult(items, errors, requestId, committed: false);
                            PersistBulkJournalOnlyLocked(requestId, fingerprint, failed);
                            return failed;
                        }
                    }
                    catch (ArgumentException ex)
                    {
                        var error = new DocumentWriteError(
                            index,
                            operation.Id,
                            DocumentWriteErrorCodes.ValidationFailed,
                            ex.Message);
                        errors.Add(error);
                        items.Add(new DocumentBulkWriteItemResult(
                            index,
                            BulkOperationName(operation.Type),
                            operation.Id,
                            DocumentBulkWriteItemStatuses.Failed,
                            Error: error));
                        if (ordered)
                        {
                            MarkOrderedItemsNotAttempted(items, materialized, index);
                            var failed = BuildBulkResult(items, errors, requestId, committed: false);
                            PersistBulkJournalOnlyLocked(requestId, fingerprint, failed);
                            return failed;
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        var error = new DocumentWriteError(
                            index,
                            operation.Id,
                            DocumentWriteErrorCodes.ValidationFailed,
                            ex.Message);
                        errors.Add(error);
                        items.Add(new DocumentBulkWriteItemResult(
                            index,
                            BulkOperationName(operation.Type),
                            operation.Id,
                            DocumentBulkWriteItemStatuses.Failed,
                            Error: error));
                        if (ordered)
                        {
                            MarkOrderedItemsNotAttempted(items, materialized, index);
                            var failed = BuildBulkResult(items, errors, requestId, committed: false);
                            PersistBulkJournalOnlyLocked(requestId, fingerprint, failed);
                            return failed;
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                    {
                        var error = new DocumentWriteError(
                            index,
                            operation.Id,
                            DocumentWriteErrorCodes.ValidationFailed,
                            ex.Message);
                        errors.Add(error);
                        items.Add(new DocumentBulkWriteItemResult(
                            index,
                            BulkOperationName(operation.Type),
                            operation.Id,
                            DocumentBulkWriteItemStatuses.Failed,
                            Error: error));
                        if (ordered)
                        {
                            MarkOrderedItemsNotAttempted(items, materialized, index);
                            var failed = BuildBulkResult(items, errors, requestId, committed: false);
                            PersistBulkJournalOnlyLocked(requestId, fingerprint, failed);
                            return failed;
                        }
                    }
                }

                var result = BuildBulkResult(items, errors, requestId, committed: true);
                IReadOnlyList<KvBatchMutation>? journalMutation = requestId is null
                    ? null
                    : [KvBatchMutation.Put(
                    EncodeBulkJournalKey(requestId),
                    EncodeBulkJournal(requestId, fingerprint, result),
                    DateTimeOffset.UtcNow.Add(BulkJournalRetention))];
                ApplyPlannedMutationsLocked(_schema, plannedMutations, journalMutation);
                return result;
            }
        }
        catch (IOException ex) when (KvAtomicBatchErrors.IsTooLarge(ex))
        {
            return BuildBulkBatchTooLargeResult(materialized, requestId, ex.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BuildBulkBatchTooLargeResult(materialized, requestId, ex.Message);
        }
    }

    private PlannedBulkOperation PlanBulkOperationLocked(
        DocumentCollectionSchema schema,
        DocumentBulkWriteOperation operation,
        int index,
        IReadOnlyDictionary<string, DocumentRow> virtualRows)
    {
        ValidateBulkOperationShape(operation);
        if (operation.ExpectedVersion is < 0)
            throw BulkValidation(operation.Id, "expectedVersion 不能为负数。");

        var proposed = new SortedDictionary<string, DocumentRow>(StringComparer.Ordinal);
        foreach (var pair in virtualRows)
            proposed[pair.Key] = pair.Value;
        var mutations = new List<PendingDocumentMutation>();
        var warnings = new List<DocumentWriteError>();
        string? resultId = operation.Id;
        int inserted = 0;
        int matched = 0;
        int modified = 0;
        int deleted = 0;
        string? upsertedId = null;

        switch (operation.Type)
        {
            case DocumentBulkWriteOperationType.InsertOne:
                {
                    string id = RequireBulkId(operation.Id);
                    if (proposed.ContainsKey(id))
                        throw BulkDuplicate(id, $"document id '{id}' 已存在。");
                    var row = PrepareBulkNewRow(schema, id, operation.Json, oldRow: null, index, warnings);
                    proposed[id] = row;
                    mutations.Add(new PendingDocumentMutation(index, OldRow: null, row, DocumentIndexCodec.EncodeDocumentKey(id)));
                    inserted = 1;
                    resultId = id;
                    break;
                }
            case DocumentBulkWriteOperationType.ReplaceOne:
                {
                    string id = RequireBulkId(operation.Id);
                    proposed.TryGetValue(id, out var oldRow);
                    ValidateExpectedVersion(operation, oldRow);
                    if (oldRow is null && !operation.Upsert)
                        break;

                    var row = PrepareBulkNewRow(schema, id, operation.Json, oldRow, index, warnings);
                    resultId = id;
                    if (oldRow is null)
                    {
                        proposed[id] = row;
                        mutations.Add(new PendingDocumentMutation(index, OldRow: null, row, DocumentIndexCodec.EncodeDocumentKey(id)));
                        inserted = 1;
                        upsertedId = id;
                    }
                    else
                    {
                        matched = 1;
                        if (!string.Equals(oldRow.Json, row.Json, StringComparison.Ordinal))
                        {
                            proposed[id] = row;
                            mutations.Add(new PendingDocumentMutation(index, oldRow, row, DocumentIndexCodec.EncodeDocumentKey(id)));
                            modified = 1;
                        }
                    }
                    break;
                }
            case DocumentBulkWriteOperationType.UpdateOne:
            case DocumentBulkWriteOperationType.UpdateMany:
                {
                    if (operation.Update is null)
                        throw BulkValidation(operation.Id, "update 操作必须提供 update 操作符。");
                    DocumentFilter? filter = MergeBulkFilter(operation.Id, operation.Filter);
                    DocumentQueryPlanner.ValidateFilter(filter);
                    int take = operation.Type == DocumentBulkWriteOperationType.UpdateOne ? 1 : int.MaxValue;
                    DocumentRow[] rows = proposed.Values
                        .Where(row => DocumentQueryPlanner.MatchesValidated(filter, row))
                        .Take(take)
                        .ToArray();
                    if (rows.Length == 0)
                    {
                        if (!operation.Upsert)
                            break;
                        string id = operation.UpsertId ?? operation.Id ?? DocumentUpdateExecutor.TryInferUpsertId(filter)
                            ?? throw BulkValidation(operation.Id, "upsert 需要提供 upsertId、id 或 ID 等值过滤条件。");
                        if (proposed.ContainsKey(id))
                            throw BulkDuplicate(id, $"document id '{id}' 已存在。");
                        string json = DocumentUpdateExecutor.Apply(DocumentUpdateExecutor.BuildUpsertSeed(filter), operation.Update);
                        var row = PrepareBulkNewRow(schema, id, json, oldRow: null, index, warnings);
                        proposed[id] = row;
                        mutations.Add(new PendingDocumentMutation(index, OldRow: null, row, DocumentIndexCodec.EncodeDocumentKey(id)));
                        inserted = 1;
                        upsertedId = id;
                        resultId = id;
                        break;
                    }

                    matched = rows.Length;
                    foreach (var oldRow in rows)
                    {
                        ValidateExpectedVersion(operation, oldRow);
                        string json = DocumentUpdateExecutor.Apply(oldRow.Json, operation.Update);
                        if (string.Equals(oldRow.Json, json, StringComparison.Ordinal))
                            continue;
                        var row = PrepareBulkNewRow(schema, oldRow.Id, json, oldRow, index, warnings);
                        proposed[oldRow.Id] = row;
                        mutations.Add(new PendingDocumentMutation(
                            index,
                            oldRow,
                            row,
                            DocumentIndexCodec.EncodeDocumentKey(oldRow.Id)));
                        modified++;
                        resultId ??= oldRow.Id;
                    }
                    break;
                }
            case DocumentBulkWriteOperationType.DeleteOne:
            case DocumentBulkWriteOperationType.DeleteMany:
                {
                    DocumentFilter? filter = MergeBulkFilter(operation.Id, operation.Filter);
                    DocumentQueryPlanner.ValidateFilter(filter);
                    int take = operation.Type == DocumentBulkWriteOperationType.DeleteOne ? 1 : int.MaxValue;
                    DocumentRow[] rows = proposed.Values
                        .Where(row => DocumentQueryPlanner.MatchesValidated(filter, row))
                        .Take(take)
                        .ToArray();
                    foreach (var oldRow in rows)
                    {
                        ValidateExpectedVersion(operation, oldRow);
                        proposed.Remove(oldRow.Id);
                        mutations.Add(new PendingDocumentMutation(
                            index,
                            oldRow,
                            NewRow: null,
                            DocumentIndexCodec.EncodeDocumentKey(oldRow.Id)));
                        resultId ??= oldRow.Id;
                    }
                    matched = rows.Length;
                    deleted = rows.Length;
                    break;
                }
            default:
                throw BulkValidation(operation.Id, $"不支持的 bulk 操作类型 '{operation.Type}'。");
        }

        ValidateVirtualUniqueIndexes(schema, proposed);
        var finalRows = BuildFinalRows(virtualRows, proposed);
        string status = inserted + modified + deleted > 0
            ? DocumentBulkWriteItemStatuses.Succeeded
            : DocumentBulkWriteItemStatuses.NoOp;
        return new PlannedBulkOperation(
            mutations,
            finalRows,
            new DocumentBulkWriteItemResult(
                index,
                BulkOperationName(operation.Type),
                resultId,
                status,
                inserted,
                matched,
                modified,
                deleted,
                upsertedId,
                warnings.FirstOrDefault()),
            warnings);
    }

    private DocumentRow PrepareBulkNewRow(
        DocumentCollectionSchema schema,
        string id,
        string? json,
        DocumentRow? oldRow,
        int index,
        ICollection<DocumentWriteError> warnings)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw BulkValidation(id, "document JSON 不能为空。");
        string normalized;
        try
        {
            normalized = JsonPathEvaluator.NormalizeJson(json);
        }
        catch (JsonException ex)
        {
            throw BulkValidation(id, ex.Message);
        }
        byte[] documentKey = DocumentIndexCodec.EncodeDocumentKey(id);
        var row = new DocumentRow(id, normalized, oldRow?.Version ?? 0);
        var validation = ValidateDocumentForWrite(schema, row);
        if (!validation.IsValid)
        {
            string message = DocumentValidatorExecutor.FormatFailures(validation.Failures);
            if (schema.Validator?.Action == DocumentValidationAction.Warn)
            {
                warnings.Add(new DocumentWriteError(
                    index,
                    id,
                    DocumentWriteErrorCodes.ValidationFailed,
                    message,
                    DocumentWriteErrorSeverity.Warning));
            }
            else
            {
                throw BulkValidation(id, message);
            }
        }

        ValidateMutationSize(schema, row, documentKey);
        return row;
    }

    private static void ValidateExpectedVersion(DocumentBulkWriteOperation operation, DocumentRow? row)
    {
        if (operation.ExpectedVersion.HasValue
            && (row?.Version ?? 0) != operation.ExpectedVersion.Value)
        {
            throw new BulkPlanningException(
                DocumentWriteErrorCodes.WriteConflict,
                row?.Id ?? operation.Id,
                $"document id '{row?.Id ?? operation.Id}' version mismatch.");
        }
    }

    private static IReadOnlyDictionary<string, DocumentRow?> BuildFinalRows(
        IReadOnlyDictionary<string, DocumentRow> before,
        IReadOnlyDictionary<string, DocumentRow> after)
    {
        var changes = new Dictionary<string, DocumentRow?>(StringComparer.Ordinal);
        foreach (var pair in before)
        {
            if (!after.TryGetValue(pair.Key, out var value))
                changes[pair.Key] = null;
            else if (!ReferenceEquals(pair.Value, value))
                changes[pair.Key] = value;
        }
        foreach (var pair in after)
        {
            if (!before.ContainsKey(pair.Key))
                changes[pair.Key] = pair.Value;
        }
        return changes;
    }

    private SortedDictionary<string, DocumentRow> LoadBulkVirtualRowsLocked(
        DocumentCollectionSchema schema,
        IReadOnlyList<DocumentBulkWriteOperation> operations)
    {
        var rows = new SortedDictionary<string, DocumentRow>(StringComparer.Ordinal);
        bool canLoadById = !schema.Indexes.Any(static index => index.IsUnique)
            && operations.All(static operation => operation.Type is
                DocumentBulkWriteOperationType.InsertOne or DocumentBulkWriteOperationType.ReplaceOne);
        if (canLoadById)
        {
            foreach (var operation in operations)
            {
                string? id = operation.Type == DocumentBulkWriteOperationType.InsertOne
                    ? operation.Id
                    : operation.Id ?? operation.UpsertId;
                if (string.IsNullOrWhiteSpace(id) || rows.ContainsKey(id))
                    continue;
                if (GetLocked(id) is { } row)
                    rows.Add(id, row);
            }
            return rows;
        }

        foreach (var row in ScanRowsLocked(int.MaxValue))
            rows.Add(row.Id, row);
        return rows;
    }

    private static void ValidateVirtualUniqueIndexes(
        DocumentCollectionSchema schema,
        IReadOnlyDictionary<string, DocumentRow> rows)
    {
        foreach (var index in schema.Indexes.Where(static value => value.IsUnique))
        {
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows.Values)
            {
                using var document = JsonDocument.Parse(row.Json);
                if (!ShouldIndexDocument(index, document.RootElement))
                    continue;
                foreach (var indexEntry in BuildIndexEntries(index, document.RootElement, row.Id))
                {
                    string key = Convert.ToBase64String(indexEntry.Key);
                    if (owners.TryGetValue(key, out string? owner)
                        && !string.Equals(owner, row.Id, StringComparison.Ordinal))
                    {
                        throw BulkDuplicate(row.Id, $"文档唯一索引 '{index.Name}' 冲突。");
                    }
                    owners[key] = row.Id;
                }
            }
        }
    }

    private static DocumentFilter? MergeBulkFilter(string? id, DocumentFilter? filter)
    {
        if (string.IsNullOrWhiteSpace(id))
            return filter;
        var idFilter = new DocumentFieldFilter(DocumentFieldRef.Id, DocumentFilterOperator.Equal, id);
        return filter is null ? idFilter : new DocumentAndFilter([idFilter, filter]);
    }

    private static string RequireBulkId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw BulkValidation(id, "document id 不能为空。");
        _ = DocumentIndexCodec.EncodeDocumentKey(id);
        return id;
    }

    private static BulkPlanningException BulkValidation(string? id, string message)
        => new(DocumentWriteErrorCodes.ValidationFailed, id, message);

    private static BulkPlanningException BulkDuplicate(string? id, string message)
        => new(DocumentWriteErrorCodes.DuplicateKey, id, message);

    private static void ValidateBulkOperationShape(DocumentBulkWriteOperation operation)
    {
        switch (operation.Type)
        {
            case DocumentBulkWriteOperationType.InsertOne:
                RejectBulkField(operation.Filter is not null, operation.Id, "insertOne", "filter");
                RejectBulkField(operation.Update is not null, operation.Id, "insertOne", "update");
                RejectBulkField(operation.Upsert, operation.Id, "insertOne", "upsert");
                RejectBulkField(operation.UpsertId is not null, operation.Id, "insertOne", "upsertId");
                RejectBulkField(operation.ExpectedVersion.HasValue, operation.Id, "insertOne", "expectedVersion");
                break;

            case DocumentBulkWriteOperationType.ReplaceOne:
                RejectBulkField(operation.Filter is not null, operation.Id, "replaceOne", "filter");
                RejectBulkField(operation.Update is not null, operation.Id, "replaceOne", "update");
                RejectBulkField(operation.UpsertId is not null, operation.Id, "replaceOne", "upsertId");
                break;

            case DocumentBulkWriteOperationType.UpdateOne:
            case DocumentBulkWriteOperationType.UpdateMany:
                RejectBulkField(operation.Json is not null, operation.Id, BulkOperationName(operation.Type), "document");
                RejectBulkField(
                    !operation.Upsert && operation.UpsertId is not null,
                    operation.Id,
                    BulkOperationName(operation.Type),
                    "upsertId");
                break;

            case DocumentBulkWriteOperationType.DeleteOne:
            case DocumentBulkWriteOperationType.DeleteMany:
                RejectBulkField(operation.Json is not null, operation.Id, BulkOperationName(operation.Type), "document");
                RejectBulkField(operation.Update is not null, operation.Id, BulkOperationName(operation.Type), "update");
                RejectBulkField(operation.Upsert, operation.Id, BulkOperationName(operation.Type), "upsert");
                RejectBulkField(operation.UpsertId is not null, operation.Id, BulkOperationName(operation.Type), "upsertId");
                break;

            default:
                throw BulkValidation(operation.Id, $"不支持的 bulk 操作类型 '{operation.Type}'。");
        }
    }

    private static void RejectBulkField(bool rejected, string? id, string operation, string field)
    {
        if (rejected)
            throw BulkValidation(id, $"{operation} 不接受字段 '{field}'。");
    }

    private static string BulkOperationName(DocumentBulkWriteOperationType type)
        => type switch
        {
            DocumentBulkWriteOperationType.InsertOne => "insert_one",
            DocumentBulkWriteOperationType.ReplaceOne => "replace_one",
            DocumentBulkWriteOperationType.UpdateOne => "update_one",
            DocumentBulkWriteOperationType.UpdateMany => "update_many",
            DocumentBulkWriteOperationType.DeleteOne => "delete_one",
            DocumentBulkWriteOperationType.DeleteMany => "delete_many",
            _ => "unknown",
        };

    private static DocumentWriteResult BuildBulkResult(
        IReadOnlyList<DocumentBulkWriteItemResult> items,
        IReadOnlyList<DocumentWriteError> errors,
        string? requestId,
        bool committed)
        => new(
            inserted: committed ? items.Sum(static item => item.Inserted) : 0,
            matched: committed ? items.Sum(static item => item.Matched) : 0,
            modified: committed ? items.Sum(static item => item.Modified) : 0,
            deleted: committed ? items.Sum(static item => item.Deleted) : 0,
            errors: errors,
            items: items,
            requestId: requestId,
            committed: committed);

    private static void MarkOrderedItemsNotAttempted(
        List<DocumentBulkWriteItemResult> items,
        IReadOnlyList<DocumentBulkWriteOperation> operations,
        int failedIndex)
    {
        for (int i = 0; i < items.Count - 1; i++)
        {
            var item = items[i];
            items[i] = item with
            {
                Status = DocumentBulkWriteItemStatuses.NotAttempted,
                Inserted = 0,
                Matched = 0,
                Modified = 0,
                Deleted = 0,
                UpsertedId = null,
                Error = new DocumentWriteError(
                    item.Index,
                    item.Id,
                    DocumentWriteErrorCodes.NotAttempted,
                    "有序 bulk 因批内错误未提交当前项。"),
            };
        }

        for (int i = failedIndex + 1; i < operations.Count; i++)
        {
            var operation = operations[i];
            var error = new DocumentWriteError(
                i,
                operation.Id,
                DocumentWriteErrorCodes.NotAttempted,
                "有序 bulk 在前序错误后停止执行。");
            items.Add(new DocumentBulkWriteItemResult(
                i,
                BulkOperationName(operation.Type),
                operation.Id,
                DocumentBulkWriteItemStatuses.NotAttempted,
                Error: error));
        }
    }

    private void PersistBulkJournalOnlyLocked(
        string? requestId,
        string fingerprint,
        DocumentWriteResult result)
    {
        if (requestId is null)
            return;
        ApplyPlannedMutationsLocked(
            _schema,
            [],
            [KvBatchMutation.Put(
                EncodeBulkJournalKey(requestId),
                EncodeBulkJournal(requestId, fingerprint, result),
                DateTimeOffset.UtcNow.Add(BulkJournalRetention))]);
    }

    private BulkJournalEntry? TryReadBulkJournalLocked(string requestId)
    {
        byte[]? value = _keyspace.Get(EncodeBulkJournalKey(requestId));
        return value is null ? null : DecodeBulkJournal(value);
    }

    private static void ValidateBulkRequestId(string? requestId)
    {
        if (requestId is null)
            return;
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("requestId 不可为空白。", nameof(requestId));
        if (Encoding.UTF8.GetByteCount(requestId) > 256)
            throw new ArgumentException("requestId 的 UTF-8 长度不可超过 256 bytes。", nameof(requestId));
    }

    private static byte[] EncodeBulkJournalKey(string requestId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(requestId));
        var key = new byte[hash.Length + 2];
        key[0] = (byte)'m';
        key[1] = (byte)'b';
        hash.CopyTo(key, 2);
        return key;
    }

    private static string ComputeBulkFingerprint(
        IReadOnlyList<DocumentBulkWriteOperation> operations,
        bool ordered)
    {
        var builder = new StringBuilder(operations.Count * 128);
        builder.Append("bulk-v2:").Append(ordered ? '1' : '0').Append(':').Append(operations.Count);
        foreach (var operation in operations)
        {
            builder.Append("|op{").Append((int)operation.Type);
            AppendFingerprintString(builder, operation.Id);
            AppendFingerprintString(builder, operation.Json);
            builder.Append(operation.Upsert ? '1' : '0');
            AppendFingerprintString(builder, operation.UpsertId);
            AppendFingerprintString(
                builder,
                operation.ExpectedVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintFilter(builder, operation.Filter);
            AppendFingerprintUpdate(builder, operation.Update);
            builder.Append('}');
        }

        string canonical = builder.ToString();
        if (Encoding.UTF8.GetByteCount(canonical) > MaxBulkPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(operations), $"单次 bulk 规范化载荷不可超过 {MaxBulkPayloadBytes} bytes。");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void AppendFingerprintFilter(StringBuilder builder, DocumentFilter? filter)
    {
        switch (filter)
        {
            case null:
                builder.Append("|filter:null;");
                break;
            case DocumentAndFilter and:
                builder.Append("|and:").Append(and.Filters.Count).Append('[');
                foreach (var child in and.Filters)
                    AppendFingerprintFilter(builder, child);
                builder.Append(']');
                break;
            case DocumentOrFilter or:
                builder.Append("|or:").Append(or.Filters.Count).Append('[');
                foreach (var child in or.Filters)
                    AppendFingerprintFilter(builder, child);
                builder.Append(']');
                break;
            case DocumentNotFilter not:
                builder.Append("|not[");
                AppendFingerprintFilter(builder, not.Filter);
                builder.Append(']');
                break;
            case DocumentFieldFilter field:
                builder.Append("|field:").Append((int)field.Field.Kind).Append(':');
                AppendFingerprintString(builder, field.Field.Path);
                builder.Append((int)field.Operator).Append(':');
                AppendFingerprintValue(builder, field.Value);
                break;
            default:
                builder.Append("|filter:unknown:");
                AppendFingerprintString(builder, filter.GetType().FullName);
                break;
        }
    }

    private static void AppendFingerprintUpdate(StringBuilder builder, DocumentUpdate? update)
    {
        if (update is null)
        {
            builder.Append("|update:null");
            return;
        }

        AppendFingerprintDictionary(builder, "set", update.Set);
        AppendFingerprintDictionary(builder, "unset", update.Unset);
        AppendFingerprintDictionary(builder, "inc", update.Inc);
        AppendFingerprintDictionary(builder, "min", update.Min);
        AppendFingerprintDictionary(builder, "max", update.Max);
        AppendFingerprintDictionary(builder, "rename", update.Rename);
        AppendFingerprintDictionary(builder, "push", update.Push);
        AppendFingerprintDictionary(builder, "pull", update.Pull);
        AppendFingerprintDictionary(builder, "addToSet", update.AddToSet);
        AppendFingerprintDictionary(builder, "currentDate", update.CurrentDate);
        AppendFingerprintDictionary(builder, "mul", update.Mul);
        AppendFingerprintDictionary(builder, "pop", update.Pop);
    }

    private static void AppendFingerprintDictionary<T>(
        StringBuilder builder,
        string name,
        IReadOnlyDictionary<string, T>? values)
    {
        builder.Append('|').Append(name).Append(':').Append(values?.Count ?? 0).Append('{');
        if (values is not null)
        {
            foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                AppendFingerprintString(builder, pair.Key);
                AppendFingerprintValue(builder, pair.Value);
            }
        }
        builder.Append('}');
    }

    private static void AppendFingerprintValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("n;");
                break;
            case JsonElement element:
                builder.Append("j:");
                AppendFingerprintString(builder, element.GetRawText());
                break;
            case string text:
                builder.Append("s:");
                AppendFingerprintString(builder, text);
                break;
            case DocumentRegex regex:
                builder.Append("r:");
                AppendFingerprintString(builder, regex.Pattern);
                AppendFingerprintString(builder, regex.Options);
                break;
            case DocumentFilter filter:
                builder.Append("f:");
                AppendFingerprintFilter(builder, filter);
                break;
            case DocumentJsonType jsonType:
                builder.Append("t:").Append((int)jsonType).Append(';');
                break;
            case System.Collections.IEnumerable sequence:
                builder.Append("a[");
                foreach (object? item in sequence)
                    AppendFingerprintValue(builder, item);
                builder.Append(']');
                break;
            case IFormattable formattable:
                builder.Append("p:");
                AppendFingerprintString(builder, value.GetType().FullName);
                AppendFingerprintString(
                    builder,
                    formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                builder.Append("o:");
                AppendFingerprintString(builder, value.GetType().FullName);
                AppendFingerprintString(builder, value.ToString());
                break;
        }
    }

    private static void AppendFingerprintString(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        builder.Append(value.Length).Append(':').Append(value);
    }

    private static DocumentWriteResult BuildBulkBatchTooLargeResult(
        IReadOnlyList<DocumentBulkWriteOperation> operations,
        string? requestId,
        string message)
    {
        var error = new DocumentWriteError(
            -1,
            null,
            DocumentWriteErrorCodes.BatchTooLarge,
            message);
        var items = operations.Select(static (operation, index) =>
        {
            var itemError = new DocumentWriteError(
                index,
                operation.Id,
                DocumentWriteErrorCodes.NotAttempted,
                "bulk 批次超过原子提交预算，当前项未执行。");
            return new DocumentBulkWriteItemResult(
                index,
                BulkOperationName(operation.Type),
                operation.Id,
                DocumentBulkWriteItemStatuses.NotAttempted,
                Error: itemError);
        }).ToArray();
        return new DocumentWriteResult(
            errors: [error],
            items: items,
            requestId: requestId,
            committed: false);
    }

    private static byte[] EncodeBulkJournal(
        string requestId,
        string fingerprint,
        DocumentWriteResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("requestId", requestId);
            writer.WriteString("fingerprint", fingerprint);
            writer.WriteNumber("inserted", result.Inserted);
            writer.WriteNumber("matched", result.Matched);
            writer.WriteNumber("modified", result.Modified);
            writer.WriteNumber("deleted", result.Deleted);
            writer.WriteBoolean("committed", result.Committed);
            writer.WritePropertyName("errors");
            writer.WriteStartArray();
            foreach (var error in result.Errors)
                WriteBulkError(writer, error);
            writer.WriteEndArray();
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (var item in result.Items)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", item.Index);
                writer.WriteString("operation", item.Operation);
                if (item.Id is not null)
                    writer.WriteString("id", item.Id);
                writer.WriteString("status", item.Status);
                writer.WriteNumber("inserted", item.Inserted);
                writer.WriteNumber("matched", item.Matched);
                writer.WriteNumber("modified", item.Modified);
                writer.WriteNumber("deleted", item.Deleted);
                if (item.UpsertedId is not null)
                    writer.WriteString("upsertedId", item.UpsertedId);
                if (item.Error is not null)
                {
                    writer.WritePropertyName("error");
                    WriteBulkError(writer, item.Error);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteBulkError(Utf8JsonWriter writer, DocumentWriteError error)
    {
        writer.WriteStartObject();
        writer.WriteNumber("index", error.Index);
        if (error.Id is not null)
            writer.WriteString("id", error.Id);
        writer.WriteString("code", error.Code);
        writer.WriteString("message", error.Message);
        writer.WriteString("severity", error.Severity);
        writer.WriteEndObject();
    }

    private static BulkJournalEntry DecodeBulkJournal(ReadOnlySpan<byte> value)
    {
        using var document = JsonDocument.Parse(value.ToArray());
        JsonElement root = document.RootElement;
        string requestId = root.GetProperty("requestId").GetString()
            ?? throw new InvalidDataException("Document bulk journal requestId is invalid.");
        string fingerprint = root.GetProperty("fingerprint").GetString()
            ?? throw new InvalidDataException("Document bulk journal fingerprint is invalid.");
        var errors = root.GetProperty("errors").EnumerateArray().Select(ReadBulkError).ToArray();
        var items = root.GetProperty("items").EnumerateArray().Select(ReadBulkItem).ToArray();
        var result = new DocumentWriteResult(
            root.GetProperty("inserted").GetInt32(),
            root.GetProperty("matched").GetInt32(),
            root.GetProperty("modified").GetInt32(),
            root.GetProperty("deleted").GetInt32(),
            errors,
            items,
            requestId,
            replayed: false,
            committed: root.GetProperty("committed").GetBoolean());
        return new BulkJournalEntry(fingerprint, result);
    }

    private static DocumentWriteError ReadBulkError(JsonElement element)
        => new(
            element.GetProperty("index").GetInt32(),
            element.TryGetProperty("id", out var id) ? id.GetString() : null,
            element.GetProperty("code").GetString() ?? DocumentWriteErrorCodes.ValidationFailed,
            element.GetProperty("message").GetString() ?? string.Empty,
            element.GetProperty("severity").GetString() ?? DocumentWriteErrorSeverity.Error);

    private static DocumentBulkWriteItemResult ReadBulkItem(JsonElement element)
        => new(
            element.GetProperty("index").GetInt32(),
            element.GetProperty("operation").GetString() ?? "unknown",
            element.TryGetProperty("id", out var id) ? id.GetString() : null,
            element.GetProperty("status").GetString() ?? DocumentBulkWriteItemStatuses.Failed,
            element.GetProperty("inserted").GetInt32(),
            element.GetProperty("matched").GetInt32(),
            element.GetProperty("modified").GetInt32(),
            element.GetProperty("deleted").GetInt32(),
            element.TryGetProperty("upsertedId", out var upsertedId) ? upsertedId.GetString() : null,
            element.TryGetProperty("error", out var error) ? ReadBulkError(error) : null);

    private static DocumentWriteResult CopyBulkResult(DocumentWriteResult result, bool replayed)
        => new(
            result.Inserted,
            result.Matched,
            result.Modified,
            result.Deleted,
            result.Errors,
            result.Items,
            result.RequestId,
            replayed,
            result.Committed);

    private sealed record PlannedBulkOperation(
        IReadOnlyList<PendingDocumentMutation> Mutations,
        IReadOnlyDictionary<string, DocumentRow?> FinalRows,
        DocumentBulkWriteItemResult Item,
        IReadOnlyList<DocumentWriteError> Warnings);

    private sealed record BulkJournalEntry(string Fingerprint, DocumentWriteResult Result);

    private sealed class BulkPlanningException(string code, string? id, string message) : Exception(message)
    {
        public string Code { get; } = code;

        public string? Id { get; } = id;
    }
}
