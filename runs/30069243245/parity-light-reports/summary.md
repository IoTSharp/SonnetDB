# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | failing |
| Pass rate | 100% |
| Scenarios | 23 passed / 27 skipped / 0 failed / 50 total |
| Warning-only performance scenarios | 2 |
| Commit | 4d5ba246ac2dc6dcfa51ae079340f52db6a7948a |
| GitHub run | 30069243245 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-d5dcd706 | 0 | 5 | 0 | 5 |
| document-c37e1a7f | 0 | 5 | 0 | 5 |
| fulltext-1931b614 | 0 | 6 | 0 | 6 |
| kv-526e080b | 5 | 0 | 0 | 5 |
| mq-741c98e5 | 5 | 0 | 0 | 5 |
| object-88b7c1a7 | 5 | 0 | 0 | 5 |
| relational-1ece6835 | 8 | 1 | 0 | 9 |
| tsdb-9cbe334b | 0 | 7 | 0 | 7 |
| vector-4aa41b30 | 0 | 3 | 0 | 3 |

## Gate Failures

| Gate | Suite | Scenario | Gap reason | Reason |
|---|---|---|---|---|
| capability | dotnet-test | parity | parity_test_failed | dotnet test exited with code 1 |

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-d5dcd706 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-d5dcd706 | columnar_compression_ratio | performance metrics are warning only |
