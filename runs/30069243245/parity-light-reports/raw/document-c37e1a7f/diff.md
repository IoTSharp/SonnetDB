# Parity Run document-c37e1a7f

Started: 2026-07-24T05:26:13.8610664+00:00

| Scenario | sonnetdb | mongodb | Diff |
|---|---|---|---|
| document_crud_filter_projection_sort | ✅ pass (rows=3) | ⏭ skipped (gap_reason=mongodb_unreachable; MongoDB reference container is not running or PARITY_MONGO_URL is invalid) | n/a (single backend) |
| document_update_operators | ✅ pass (rows=1) | ⏭ skipped (gap_reason=mongodb_unreachable; MongoDB reference container is not running or PARITY_MONGO_URL is invalid) | n/a (single backend) |
| document_index_unique_ttl | ✅ pass (rows=1) | ⏭ skipped (gap_reason=mongodb_unreachable; MongoDB reference container is not running or PARITY_MONGO_URL is invalid) | n/a (single backend) |
| document_aggregation_group_average | ✅ pass (rows=2) | ⏭ skipped (gap_reason=mongodb_unreachable; MongoDB reference container is not running or PARITY_MONGO_URL is invalid) | n/a (single backend) |
| document_concurrent_write_recovery_index_consistency | ✅ pass (rows=1) | ⏭ skipped (gap_reason=mongodb_unreachable; MongoDB reference container is not running or PARITY_MONGO_URL is invalid) | n/a (single backend) |

## Capability gaps

| Scenario | Required | sonnetdb | mongodb | SonnetDB gap |
|---|---|---|---|---|
| document_crud_filter_projection_sort | Document | pass | skipped |  |
| document_update_operators | Document | pass | skipped |  |
| document_index_unique_ttl | Document | pass | skipped |  |
| document_aggregation_group_average | Document | pass | skipped |  |
| document_concurrent_write_recovery_index_consistency | Document | pass | skipped |  |
