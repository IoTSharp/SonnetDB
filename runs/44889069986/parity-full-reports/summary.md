# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | full |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 49 passed / 2 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | 41abaa23807ea103e74b985e13045dcdcb0e03ae |
| GitHub run | 44889069986 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-c918c103 | 5 | 0 | 0 | 5 |
| document-9365d8f5 | 5 | 0 | 0 | 5 |
| fulltext-2cca648e | 6 | 0 | 0 | 6 |
| graph-973c1e30 | 1 | 0 | 0 | 1 |
| kv-f1dde8b2 | 5 | 0 | 0 | 5 |
| mq-fe88dcbe | 5 | 0 | 0 | 5 |
| object-395068cf | 5 | 0 | 0 | 5 |
| relational-63add562 | 8 | 1 | 0 | 9 |
| tsdb-98a2b360 | 6 | 1 | 0 | 7 |
| vector-061104b2 | 3 | 0 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-c918c103 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-c918c103 | columnar_compression_ratio | performance metrics are warning only |
