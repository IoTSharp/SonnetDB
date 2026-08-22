# Parity Run analytics-f4ff9deb

Started: 2026-08-22T03:00:37.9246752+00:00

| Scenario | sonnetdb | clickhouse | Diff |
|---|---|---|---|
| groupby_time_1b_rows_wallclock | ✅ pass (rows=120) | ⏭ skipped (clickhouse unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| window_avg_7day | ✅ pass (rows=28) | ⏭ skipped (clickhouse unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| topn_per_device | ✅ pass (rows=5) | ⏭ skipped (clickhouse unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| columnar_compression_ratio | ✅ pass (rows=1) | ⏭ skipped (clickhouse unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| percentile_accuracy_p50_p95_p99 | ✅ pass (rows=1) | ⏭ skipped (clickhouse unreachable (compose full/light 未启动或 PARITY_* 未配置)) | n/a (single backend) |

## Capability gaps

| Scenario | Required | sonnetdb | clickhouse | SonnetDB gap |
|---|---|---|---|---|
| groupby_time_1b_rows_wallclock | Analytics, AnalyticsGroupByTime | pass | skipped |  |
| window_avg_7day | Analytics, SqlWindowFunction | pass | skipped |  |
| topn_per_device | Analytics, AnalyticsTopN | pass | skipped |  |
| columnar_compression_ratio | Analytics, AnalyticsCompressionRatio | pass | skipped |  |
| percentile_accuracy_p50_p95_p99 | Analytics, AccuracyPercentile | pass | skipped |  |
