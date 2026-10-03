# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | full |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 49 passed / 2 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | 54e75dd6360a154b05a19bf61c593484ed26bdcc |
| GitHub run | 37107364528 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-603260d4 | 5 | 0 | 0 | 5 |
| document-884227c2 | 5 | 0 | 0 | 5 |
| fulltext-189f0acc | 6 | 0 | 0 | 6 |
| graph-5c91c598 | 1 | 0 | 0 | 1 |
| kv-432e7350 | 5 | 0 | 0 | 5 |
| mq-54b44fc7 | 5 | 0 | 0 | 5 |
| object-d3531d32 | 5 | 0 | 0 | 5 |
| relational-6ff9b5c4 | 8 | 1 | 0 | 9 |
| tsdb-da2b391d | 6 | 1 | 0 | 7 |
| vector-b970049d | 3 | 0 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-603260d4 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-603260d4 | columnar_compression_ratio | performance metrics are warning only |
