# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | 54e75dd6360a154b05a19bf61c593484ed26bdcc |
| GitHub run | 37107364528 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-29efea69 | 0 | 5 | 0 | 5 |
| document-5039d2b5 | 0 | 5 | 0 | 5 |
| fulltext-44782e07 | 0 | 6 | 0 | 6 |
| graph-01b0a98d | 1 | 0 | 0 | 1 |
| kv-2dfbee6a | 5 | 0 | 0 | 5 |
| mq-fb358972 | 5 | 0 | 0 | 5 |
| object-45593c74 | 5 | 0 | 0 | 5 |
| relational-436862af | 8 | 1 | 0 | 9 |
| tsdb-c32b051f | 0 | 7 | 0 | 7 |
| vector-3d29d2e5 | 0 | 3 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-29efea69 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-29efea69 | columnar_compression_ratio | performance metrics are warning only |
