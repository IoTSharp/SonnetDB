# Parity Run tsdb-9cbe334b

Started: 2026-07-24T05:26:17.2987248+00:00

| Scenario | sonnetdb | influxdb | victoriametrics | Diff |
|---|---|---|---|---|
| ingest_1m_points | ✅ pass (rows=1) | ⏭ skipped (influxdb unreachable (compose full 未启动或 PARITY_* 未配置)) | ⏭ skipped (victoriametrics unreachable (compose full 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| groupby_time_window | ✅ pass (rows=2) | ⏭ skipped (influxdb unreachable (compose full 未启动或 PARITY_* 未配置)) | ⏭ skipped (victoriametrics unreachable (compose full 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| derivative_accuracy | ✅ pass (rows=30) | ⏭ skipped (influxdb unreachable (compose full 未启动或 PARITY_* 未配置)) | ⏭ skipped (victoriametrics unreachable (compose full 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| rate_irate_consistency | ✅ pass (rows=30) | ⏭ skipped (influxdb unreachable (compose full 未启动或 PARITY_* 未配置)) | ⏭ skipped (victoriametrics unreachable (compose full 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| holt_winters_forecast_recall | ✅ pass (rows=6) | ⏭ skipped (influxdb unreachable (compose full 未启动或 PARITY_* 未配置)) | ⏭ skipped (victoriametrics unreachable (compose full 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| percentile_p95_tdigest_vs_quantile | ✅ pass (rows=1) | ⏭ skipped (influxdb unreachable (compose full 未启动或 PARITY_* 未配置)) | ⏭ skipped (victoriametrics unreachable (compose full 未启动或 PARITY_* 未配置)) | n/a (single backend) |
| distinct_count_hll_2pct_error | ✅ pass (rows=1) | ⏭ skipped (influxdb unreachable (compose full 未启动或 PARITY_* 未配置)) | ⏭ skipped (victoriametrics unreachable (compose full 未启动或 PARITY_* 未配置)) | n/a (single backend) |

## Capability gaps

| Scenario | Required | sonnetdb | influxdb | victoriametrics | SonnetDB gap |
|---|---|---|---|---|---|
| ingest_1m_points | TimeSeries, TimeSeriesRemoteWrite | pass | skipped | skipped |  |
| groupby_time_window | TimeSeries, TimeSeriesGroupByTime | pass | skipped | skipped |  |
| derivative_accuracy | TimeSeries, TimeSeriesDerivative | pass | skipped | skipped |  |
| rate_irate_consistency | TimeSeries, TimeSeriesRateIrate | pass | skipped | skipped |  |
| holt_winters_forecast_recall | TimeSeries, TimeSeriesHoltWinters | pass | skipped | skipped |  |
| percentile_p95_tdigest_vs_quantile | TimeSeries, TimeSeriesQuantile | pass | skipped | skipped |  |
| distinct_count_hll_2pct_error | TimeSeries, TimeSeriesDistinctCount | pass | skipped | skipped |  |
