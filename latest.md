# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | full |
| Status | failing |
| Pass rate | 68% |
| Scenarios | 28 passed / 6 skipped / 16 failed / 50 total |
| Warning-only performance scenarios | 2 |
| Commit | 4d5ba246ac2dc6dcfa51ae079340f52db6a7948a |
| GitHub run | 30069243245 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-d3b57fc2 | 0 | 5 | 0 | 5 |
| document-55f2d355 | 5 | 0 | 0 | 5 |
| fulltext-5e76b952 | 0 | 0 | 6 | 6 |
| kv-37045fe6 | 5 | 0 | 0 | 5 |
| mq-ebec3fb3 | 5 | 0 | 0 | 5 |
| object-3b41e36f | 5 | 0 | 0 | 5 |
| relational-8ef2430b | 8 | 1 | 0 | 9 |
| tsdb-7ac23153 | 0 | 0 | 7 | 7 |
| vector-cfc208ed | 0 | 0 | 3 | 3 |

## Gate Failures

| Gate | Suite | Scenario | Gap reason | Reason |
|---|---|---|---|---|
| accuracy | fulltext-5e76b952 | index_1m_documents | scenario_failed | backend reported fail |
| accuracy | fulltext-5e76b952 | bm25_ranking_top10_overlap | scenario_failed | backend reported fail |
| accuracy | fulltext-5e76b952 | cjk_tokenize_correctness | scenario_failed | backend reported fail |
| accuracy | fulltext-5e76b952 | facet_filter_query | scenario_failed | backend reported fail |
| accuracy | fulltext-5e76b952 | incremental_update_during_query | scenario_failed | backend reported fail |
| accuracy | fulltext-5e76b952 | typo_tolerant_query | scenario_failed | backend reported fail |
| capability | tsdb-7ac23153 | ingest_1m_points | scenario_failed | backend reported fail |
| accuracy | tsdb-7ac23153 | groupby_time_window | scenario_failed | victoriametrics: row count mismatch: expected 2, actual 0 |
| accuracy | tsdb-7ac23153 | derivative_accuracy | scenario_failed | influxdb: row count mismatch: expected 30, actual 29; victoriametrics: row count mismatch: expected 30, actual 0 |
| accuracy | tsdb-7ac23153 | rate_irate_consistency | scenario_failed | influxdb: row count mismatch: expected 30, actual 29; victoriametrics: row count mismatch: expected 30, actual 0 |
| accuracy | tsdb-7ac23153 | holt_winters_forecast_recall | scenario_failed | influxdb: row 0 column 0 (time) mismatch: expected '1704067280000', actual '0'; influxdb: row 1 column 0 (time) mismatch: expected '1704067281000', actual '1'; influxdb: row 2 column 0 (time) mismatch: expected '1704067282000', actual '2'; influxdb: row 3 column 0 (time) mismatch: expected '1704067283000', actual '3'; influxdb: row 4 column 0 (time) mismatch: expected '1704067284000', actual '4'; influxdb: row 5 column 0 (time) mismatch: expected '1704067285000', actual '5' |
| accuracy | tsdb-7ac23153 | percentile_p95_tdigest_vs_quantile | scenario_failed | victoriametrics: row 0 column 0 (percentile) mismatch: expected '94.06', actual '0' |
| accuracy | tsdb-7ac23153 | distinct_count_hll_2pct_error | scenario_failed | victoriametrics: row 0 column 0 (distinct_count(value)) mismatch: expected '503', actual '0' |
| capability | vector-cfc208ed | ann_recall_at_10 | scenario_failed | backend reported fail |
| accuracy | vector-cfc208ed | filtered_search | scenario_failed | backend reported fail |
| capability | vector-cfc208ed | upsert_during_query | scenario_failed | backend reported fail |
| capability | dotnet-test | parity | parity_test_failed | dotnet test exited with code 1 |

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-d3b57fc2 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-d3b57fc2 | columnar_compression_ratio | performance metrics are warning only |
