# Parity Run vector-061104b2

Started: 2026-11-21T09:34:51.2809698+00:00

| Scenario | sonnetdb | qdrant | Diff |
|---|---|---|---|
| ann_recall_at_10 | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| filtered_search | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| upsert_during_query | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |

## Capability gaps

| Scenario | Required | sonnetdb | qdrant | SonnetDB gap |
|---|---|---|---|---|
| ann_recall_at_10 | Vector | pass | pass |  |
| filtered_search | Vector, HnswFiltered | pass | pass |  |
| upsert_during_query | Vector | pass | pass |  |
