# Parity Run fulltext-18267f90

Started: 2026-09-26T07:21:32.0539368+00:00

| Scenario | sonnetdb | meilisearch | Diff |
|---|---|---|---|
| index_1m_documents | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| bm25_ranking_top10_overlap | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| cjk_tokenize_correctness | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| facet_filter_query | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| incremental_update_during_query | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| typo_tolerant_query | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |

## Capability gaps

| Scenario | Required | sonnetdb | meilisearch | SonnetDB gap |
|---|---|---|---|---|
| index_1m_documents | Fulltext | pass | pass |  |
| bm25_ranking_top10_overlap | Fulltext | pass | pass |  |
| cjk_tokenize_correctness | Fulltext, FulltextCjk | pass | pass |  |
| facet_filter_query | Fulltext, FulltextFacetFilter | pass | pass |  |
| incremental_update_during_query | Fulltext | pass | pass |  |
| typo_tolerant_query | Fulltext, FulltextTypoTolerant | pass | pass |  |
