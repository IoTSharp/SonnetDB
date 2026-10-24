# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | 4772ad4b6a0bfe5c994646a7aafc4524f7921bab |
| GitHub run | 40072260151 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-900c1c67 | 0 | 5 | 0 | 5 |
| document-cc31556a | 0 | 5 | 0 | 5 |
| fulltext-b13ac9e2 | 0 | 6 | 0 | 6 |
| graph-57d3e005 | 1 | 0 | 0 | 1 |
| kv-345a6a70 | 5 | 0 | 0 | 5 |
| mq-412b2120 | 5 | 0 | 0 | 5 |
| object-d3b6cf14 | 5 | 0 | 0 | 5 |
| relational-7500b856 | 8 | 1 | 0 | 9 |
| tsdb-31f121de | 0 | 7 | 0 | 7 |
| vector-68205d1b | 0 | 3 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-900c1c67 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-900c1c67 | columnar_compression_ratio | performance metrics are warning only |
