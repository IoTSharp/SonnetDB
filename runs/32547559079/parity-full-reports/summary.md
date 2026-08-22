# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | full |
| Status | failing |
| Pass rate | 68.63% |
| Scenarios | 29 passed / 6 skipped / 16 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | b86bf7c94b7e47f810bf1ff49387772adb922e5f |
| GitHub run | 32547559079 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-f4ff9deb | 0 | 5 | 0 | 5 |
| document-652d84c9 | 5 | 0 | 0 | 5 |
| fulltext-f383db16 | 0 | 0 | 6 | 6 |
| graph-008dce7d | 1 | 0 | 0 | 1 |
| kv-57dda432 | 5 | 0 | 0 | 5 |
| mq-0282dba9 | 5 | 0 | 0 | 5 |
| object-58b1216c | 5 | 0 | 0 | 5 |
| relational-eeb5f3ba | 8 | 1 | 0 | 9 |
| tsdb-253d4c14 | 0 | 0 | 7 | 7 |
| vector-82587a88 | 0 | 0 | 3 | 3 |

## Gate Failures

| Gate | Suite | Scenario | Gap reason | Reason |
|---|---|---|---|---|
| accuracy | fulltext-f383db16 | index_1m_documents | scenario_failed | backend reported fail |
| accuracy | fulltext-f383db16 | bm25_ranking_top10_overlap | scenario_failed | backend reported fail |
| accuracy | fulltext-f383db16 | cjk_tokenize_correctness | scenario_failed | backend reported fail |
| accuracy | fulltext-f383db16 | facet_filter_query | scenario_failed | backend reported fail |
| accuracy | fulltext-f383db16 | incremental_update_during_query | scenario_failed | backend reported fail |
| accuracy | fulltext-f383db16 | typo_tolerant_query | scenario_failed | backend reported fail |
| capability | tsdb-253d4c14 | ingest_1m_points | scenario_failed | backend reported fail |
| accuracy | tsdb-253d4c14 | groupby_time_window | scenario_failed | victoriametrics: row count mismatch: expected 2, actual 0 |
| accuracy | tsdb-253d4c14 | derivative_accuracy | scenario_failed | influxdb: row count mismatch: expected 30, actual 29; victoriametrics: row count mismatch: expected 30, actual 0 |
| accuracy | tsdb-253d4c14 | rate_irate_consistency | scenario_failed | influxdb: row count mismatch: expected 30, actual 29; victoriametrics: row count mismatch: expected 30, actual 0 |
| accuracy | tsdb-253d4c14 | holt_winters_forecast_recall | scenario_failed | influxdb: row 0 column 0 (time) mismatch: expected '1704067280000', actual '0'; influxdb: row 1 column 0 (time) mismatch: expected '1704067281000', actual '1'; influxdb: row 2 column 0 (time) mismatch: expected '1704067282000', actual '2'; influxdb: row 3 column 0 (time) mismatch: expected '1704067283000', actual '3'; influxdb: row 4 column 0 (time) mismatch: expected '1704067284000', actual '4'; influxdb: row 5 column 0 (time) mismatch: expected '1704067285000', actual '5' |
| accuracy | tsdb-253d4c14 | percentile_p95_tdigest_vs_quantile | scenario_failed | victoriametrics: row 0 column 0 (percentile) mismatch: expected '94.06', actual '0' |
| accuracy | tsdb-253d4c14 | distinct_count_hll_2pct_error | scenario_failed | victoriametrics: row 0 column 0 (distinct_count(value)) mismatch: expected '503', actual '0' |
| capability | vector-82587a88 | ann_recall_at_10 | scenario_failed | backend reported fail |
| accuracy | vector-82587a88 | filtered_search | scenario_failed | backend reported fail |
| capability | vector-82587a88 | upsert_during_query | scenario_failed | backend reported fail |
| capability | dotnet-test | parity | parity_test_failed | dotnet test exited with code 1 |

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-f4ff9deb | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-f4ff9deb | columnar_compression_ratio | performance metrics are warning only |
