# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | failing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | c194debdcac2b497bf60c20d1de032d97d4ee30c |
| GitHub run | 39010884412 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-3341584f | 0 | 5 | 0 | 5 |
| document-3b3fec69 | 0 | 5 | 0 | 5 |
| fulltext-d130e04a | 0 | 6 | 0 | 6 |
| graph-a03ab73f | 1 | 0 | 0 | 1 |
| kv-daec33fc | 5 | 0 | 0 | 5 |
| mq-69818841 | 5 | 0 | 0 | 5 |
| object-509910f9 | 5 | 0 | 0 | 5 |
| relational-ec20e3d2 | 8 | 1 | 0 | 9 |
| tsdb-bdf9ac5c | 0 | 7 | 0 | 7 |
| vector-8e373781 | 0 | 3 | 0 | 3 |

## Gate Failures

| Gate | Suite | Scenario | Gap reason | Reason |
|---|---|---|---|---|
| reliability | dotnet-test | crash-reliability | reliability_test_failed | crash reliability test exited with code 1 |

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-3341584f | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-3341584f | columnar_compression_ratio | performance metrics are warning only |
