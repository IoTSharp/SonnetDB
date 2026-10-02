# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | full |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 49 passed / 2 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | cf8c70e0032e2015590eef32300584b077831396 |
| GitHub run | 36836621419 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-218ff5f0 | 5 | 0 | 0 | 5 |
| document-435f5963 | 5 | 0 | 0 | 5 |
| fulltext-90aefeb6 | 6 | 0 | 0 | 6 |
| graph-2180f7e0 | 1 | 0 | 0 | 1 |
| kv-bacc6633 | 5 | 0 | 0 | 5 |
| mq-3b8bdb9d | 5 | 0 | 0 | 5 |
| object-702b6193 | 5 | 0 | 0 | 5 |
| relational-a7967821 | 8 | 1 | 0 | 9 |
| tsdb-adb24c6f | 6 | 1 | 0 | 7 |
| vector-5c1be68e | 3 | 0 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-218ff5f0 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-218ff5f0 | columnar_compression_ratio | performance metrics are warning only |
