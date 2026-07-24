# Parity Run fulltext-5e76b952

Started: 2026-07-24T05:26:54.7045387+00:00

| Scenario | sonnetdb | meilisearch | Diff |
|---|---|---|---|
| index_1m_documents | ✅ pass (rows=1) | ❌ fail (rows=0) | n/a (single backend) |
| bm25_ranking_top10_overlap | ✅ pass (rows=1) | ❌ fail (rows=0) | n/a (single backend) |
| cjk_tokenize_correctness | ✅ pass (rows=1) | ❌ fail (rows=0) | n/a (single backend) |
| facet_filter_query | ✅ pass (rows=1) | ❌ fail (rows=0) | n/a (single backend) |
| incremental_update_during_query | ✅ pass (rows=1) | ❌ fail (rows=0) | n/a (single backend) |
| typo_tolerant_query | ✅ pass (rows=1) | ❌ fail (rows=0) | n/a (single backend) |

## Capability gaps

| Scenario | Required | sonnetdb | meilisearch | SonnetDB gap |
|---|---|---|---|---|
| index_1m_documents | Fulltext | pass | fail |  |
| bm25_ranking_top10_overlap | Fulltext | pass | fail |  |
| cjk_tokenize_correctness | Fulltext, FulltextCjk | pass | fail |  |
| facet_filter_query | Fulltext, FulltextFacetFilter | pass | fail |  |
| incremental_update_during_query | Fulltext | pass | fail |  |
| typo_tolerant_query | Fulltext, FulltextTypoTolerant | pass | fail |  |
