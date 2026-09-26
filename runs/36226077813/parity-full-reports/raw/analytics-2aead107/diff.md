# Parity Run analytics-2aead107

Started: 2026-09-26T07:20:31.0862891+00:00

| Scenario | sonnetdb | clickhouse | Diff |
|---|---|---|---|
| groupby_time_1b_rows_wallclock | ✅ pass (rows=120) | ✅ pass (rows=120) | ✅ within tolerance |
| window_avg_7day | ✅ pass (rows=28) | ✅ pass (rows=28) | ✅ within tolerance |
| topn_per_device | ✅ pass (rows=5) | ✅ pass (rows=5) | ✅ within tolerance |
| columnar_compression_ratio | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| percentile_accuracy_p50_p95_p99 | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |

## Capability gaps

| Scenario | Required | sonnetdb | clickhouse | SonnetDB gap |
|---|---|---|---|---|
| groupby_time_1b_rows_wallclock | Analytics, AnalyticsGroupByTime | pass | pass |  |
| window_avg_7day | Analytics, SqlWindowFunction | pass | pass |  |
| topn_per_device | Analytics, AnalyticsTopN | pass | pass |  |
| columnar_compression_ratio | Analytics, AnalyticsCompressionRatio | pass | pass |  |
| percentile_accuracy_p50_p95_p99 | Analytics, AccuracyPercentile | pass | pass |  |
