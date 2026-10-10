# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | c40fa670e3a170c5fac8cefa181d618631862270 |
| GitHub run | 38037066438 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-c62a3d4e | 0 | 5 | 0 | 5 |
| document-8e4e6449 | 0 | 5 | 0 | 5 |
| fulltext-8fc843f0 | 0 | 6 | 0 | 6 |
| graph-d1945e22 | 1 | 0 | 0 | 1 |
| kv-46953630 | 5 | 0 | 0 | 5 |
| mq-c3e96191 | 5 | 0 | 0 | 5 |
| object-03edc8c6 | 5 | 0 | 0 | 5 |
| relational-c311f7c8 | 8 | 1 | 0 | 9 |
| tsdb-bec34ad6 | 0 | 7 | 0 | 7 |
| vector-8ffc8d06 | 0 | 3 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-c62a3d4e | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-c62a3d4e | columnar_compression_ratio | performance metrics are warning only |
