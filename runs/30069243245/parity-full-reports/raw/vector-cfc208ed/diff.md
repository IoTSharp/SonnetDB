# Parity Run vector-cfc208ed

Started: 2026-07-24T05:26:52.7292671+00:00

| Scenario | sonnetdb | qdrant | Diff |
|---|---|---|---|
| ann_recall_at_10 | ✅ pass (rows=1) | ❌ fail (rows=0) | n/a (single backend) |
| filtered_search | ✅ pass (rows=1) | ❌ fail (rows=0) | n/a (single backend) |
| upsert_during_query | ✅ pass (rows=1) | ❌ fail (rows=0) | n/a (single backend) |

## Capability gaps

| Scenario | Required | sonnetdb | qdrant | SonnetDB gap |
|---|---|---|---|---|
| ann_recall_at_10 | Vector | pass | fail |  |
| filtered_search | Vector, HnswFiltered | pass | fail |  |
| upsert_during_query | Vector | pass | fail |  |
