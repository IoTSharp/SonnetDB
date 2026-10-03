# Parity Run vector-3d29d2e5

Started: 2026-10-03T07:51:42.3075442+00:00

| Scenario | sonnetdb | qdrant | Diff |
|---|---|---|---|
| ann_recall_at_10 | ✅ pass (rows=1) | ⏭ skipped (qdrant unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| filtered_search | ✅ pass (rows=1) | ⏭ skipped (qdrant unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| upsert_during_query | ✅ pass (rows=1) | ⏭ skipped (qdrant unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |

## Capability gaps

| Scenario | Required | sonnetdb | qdrant | SonnetDB gap |
|---|---|---|---|---|
| ann_recall_at_10 | Vector | pass | skipped |  |
| filtered_search | Vector, HnswFiltered | pass | skipped |  |
| upsert_during_query | Vector | pass | skipped |  |
