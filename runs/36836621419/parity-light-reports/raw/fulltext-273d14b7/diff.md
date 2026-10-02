# Parity Run fulltext-273d14b7

Started: 2026-10-01T08:35:59.5350773+00:00

| Scenario | sonnetdb | meilisearch | Diff |
|---|---|---|---|
| index_1m_documents | ✅ pass (rows=1) | ⏭ skipped (meilisearch unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| bm25_ranking_top10_overlap | ✅ pass (rows=1) | ⏭ skipped (meilisearch unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| cjk_tokenize_correctness | ✅ pass (rows=1) | ⏭ skipped (meilisearch unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| facet_filter_query | ✅ pass (rows=1) | ⏭ skipped (meilisearch unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| incremental_update_during_query | ✅ pass (rows=1) | ⏭ skipped (meilisearch unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| typo_tolerant_query | ✅ pass (rows=1) | ⏭ skipped (meilisearch unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |

## Capability gaps

| Scenario | Required | sonnetdb | meilisearch | SonnetDB gap |
|---|---|---|---|---|
| index_1m_documents | Fulltext | pass | skipped |  |
| bm25_ranking_top10_overlap | Fulltext | pass | skipped |  |
| cjk_tokenize_correctness | Fulltext, FulltextCjk | pass | skipped |  |
| facet_filter_query | Fulltext, FulltextFacetFilter | pass | skipped |  |
| incremental_update_during_query | Fulltext | pass | skipped |  |
| typo_tolerant_query | Fulltext, FulltextTypoTolerant | pass | skipped |  |
