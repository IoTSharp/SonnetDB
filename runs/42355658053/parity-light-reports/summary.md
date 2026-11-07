# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | 55bf87158d182147a6cb801e02bf8bf62d0ff942 |
| GitHub run | 42355658053 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-4c895027 | 0 | 5 | 0 | 5 |
| document-7a881874 | 0 | 5 | 0 | 5 |
| fulltext-b2b54c0e | 0 | 6 | 0 | 6 |
| graph-dcd4f261 | 1 | 0 | 0 | 1 |
| kv-1b119927 | 5 | 0 | 0 | 5 |
| mq-4a08dca4 | 5 | 0 | 0 | 5 |
| object-0ec1684b | 5 | 0 | 0 | 5 |
| relational-b40ce9d9 | 8 | 1 | 0 | 9 |
| tsdb-c76acaa6 | 0 | 7 | 0 | 7 |
| vector-5d97c4b9 | 0 | 3 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-4c895027 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-4c895027 | columnar_compression_ratio | performance metrics are warning only |
