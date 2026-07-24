# Parity Run document-55f2d355

Started: 2026-07-24T05:26:46.2299205+00:00

| Scenario | sonnetdb | mongodb | Diff |
|---|---|---|---|
| document_crud_filter_projection_sort | ✅ pass (rows=3) | ✅ pass (rows=3) | ✅ within tolerance |
| document_update_operators | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| document_index_unique_ttl | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| document_aggregation_group_average | ✅ pass (rows=2) | ✅ pass (rows=2) | ✅ within tolerance |
| document_concurrent_write_recovery_index_consistency | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |

## Capability gaps

| Scenario | Required | sonnetdb | mongodb | SonnetDB gap |
|---|---|---|---|---|
| document_crud_filter_projection_sort | Document | pass | pass |  |
| document_update_operators | Document | pass | pass |  |
| document_index_unique_ttl | Document | pass | pass |  |
| document_aggregation_group_average | Document | pass | pass |  |
| document_concurrent_write_recovery_index_consistency | Document | pass | pass |  |
