# Parity Run tsdb-86e2a129

Started: 2026-09-26T07:20:32.3073530+00:00

| Scenario | sonnetdb | influxdb | victoriametrics | Diff |
|---|---|---|---|---|
| ingest_1m_points | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| groupby_time_window | ✅ pass (rows=2) | ✅ pass (rows=2) | ✅ pass (rows=2) | ✅ within tolerance |
| derivative_accuracy | ✅ pass (rows=30) | ✅ pass (rows=30) | ✅ pass (rows=30) | ✅ within tolerance |
| rate_irate_consistency | ✅ pass (rows=30) | ✅ pass (rows=30) | ✅ pass (rows=30) | ✅ within tolerance |
| holt_winters_forecast_recall | ✅ pass (rows=6) | ✅ pass (rows=6) | ⏭ skipped (backend 'victoriametrics' lacks required capabilities: TimeSeriesHoltWinters) | ✅ within tolerance |
| percentile_p95_tdigest_vs_quantile | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| distinct_count_hll_2pct_error | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |

## Capability gaps

| Scenario | Required | sonnetdb | influxdb | victoriametrics | SonnetDB gap |
|---|---|---|---|---|---|
| ingest_1m_points | TimeSeries, TimeSeriesRemoteWrite | pass | pass | pass |  |
| groupby_time_window | TimeSeries, TimeSeriesGroupByTime | pass | pass | pass |  |
| derivative_accuracy | TimeSeries, TimeSeriesDerivative | pass | pass | pass |  |
| rate_irate_consistency | TimeSeries, TimeSeriesRateIrate | pass | pass | pass |  |
| holt_winters_forecast_recall | TimeSeries, TimeSeriesHoltWinters | pass | pass | skipped |  |
| percentile_p95_tdigest_vs_quantile | TimeSeries, TimeSeriesQuantile | pass | pass | pass |  |
| distinct_count_hll_2pct_error | TimeSeries, TimeSeriesDistinctCount | pass | pass | pass |  |
