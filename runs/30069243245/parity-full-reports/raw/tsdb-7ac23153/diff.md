# Parity Run tsdb-7ac23153

Started: 2026-07-24T05:26:48.6092411+00:00

| Scenario | sonnetdb | influxdb | victoriametrics | Diff |
|---|---|---|---|---|
| ingest_1m_points | ✅ pass (rows=1) | ✅ pass (rows=1) | ❌ fail (rows=1) | ✅ within tolerance |
| groupby_time_window | ✅ pass (rows=2) | ✅ pass (rows=2) | ✅ pass (rows=0) | ❌ out of tolerance |
| &nbsp; | victoriametrics: row count mismatch: expected 2, actual 0 |  |  |  |
| derivative_accuracy | ✅ pass (rows=30) | ✅ pass (rows=29) | ✅ pass (rows=0) | ❌ out of tolerance |
| &nbsp; | influxdb: row count mismatch: expected 30, actual 29 |  |  |  |
| &nbsp; | victoriametrics: row count mismatch: expected 30, actual 0 |  |  |  |
| rate_irate_consistency | ✅ pass (rows=30) | ✅ pass (rows=29) | ✅ pass (rows=0) | ❌ out of tolerance |
| &nbsp; | influxdb: row count mismatch: expected 30, actual 29 |  |  |  |
| &nbsp; | victoriametrics: row count mismatch: expected 30, actual 0 |  |  |  |
| holt_winters_forecast_recall | ✅ pass (rows=6) | ✅ pass (rows=6) | ⏭ skipped (backend 'victoriametrics' lacks required capabilities: TimeSeriesHoltWinters) | ❌ out of tolerance |
| &nbsp; | influxdb: row 0 column 0 (time) mismatch: expected '1704067280000', actual '0' |  |  |  |
| &nbsp; | influxdb: row 1 column 0 (time) mismatch: expected '1704067281000', actual '1' |  |  |  |
| &nbsp; | influxdb: row 2 column 0 (time) mismatch: expected '1704067282000', actual '2' |  |  |  |
| &nbsp; | influxdb: row 3 column 0 (time) mismatch: expected '1704067283000', actual '3' |  |  |  |
| &nbsp; | influxdb: row 4 column 0 (time) mismatch: expected '1704067284000', actual '4' |  |  |  |
| &nbsp; | influxdb: row 5 column 0 (time) mismatch: expected '1704067285000', actual '5' |  |  |  |
| percentile_p95_tdigest_vs_quantile | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ pass (rows=1) | ❌ out of tolerance |
| &nbsp; | victoriametrics: row 0 column 0 (percentile) mismatch: expected '94.06', actual '0' |  |  |  |
| distinct_count_hll_2pct_error | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ pass (rows=1) | ❌ out of tolerance |
| &nbsp; | victoriametrics: row 0 column 0 (distinct_count(value)) mismatch: expected '503', actual '0' |  |  |  |

## Capability gaps

| Scenario | Required | sonnetdb | influxdb | victoriametrics | SonnetDB gap |
|---|---|---|---|---|---|
| ingest_1m_points | TimeSeries, TimeSeriesRemoteWrite | pass | pass | fail |  |
| groupby_time_window | TimeSeries, TimeSeriesGroupByTime | pass | pass | pass |  |
| derivative_accuracy | TimeSeries, TimeSeriesDerivative | pass | pass | pass |  |
| rate_irate_consistency | TimeSeries, TimeSeriesRateIrate | pass | pass | pass |  |
| holt_winters_forecast_recall | TimeSeries, TimeSeriesHoltWinters | pass | pass | skipped |  |
| percentile_p95_tdigest_vs_quantile | TimeSeries, TimeSeriesQuantile | pass | pass | pass |  |
| distinct_count_hll_2pct_error | TimeSeries, TimeSeriesDistinctCount | pass | pass | pass |  |
