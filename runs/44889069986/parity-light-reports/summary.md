# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | 41abaa23807ea103e74b985e13045dcdcb0e03ae |
| GitHub run | 44889069986 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-19ab5af3 | 0 | 5 | 0 | 5 |
| document-416d0fa4 | 0 | 5 | 0 | 5 |
| fulltext-def26deb | 0 | 6 | 0 | 6 |
| graph-983e56be | 1 | 0 | 0 | 1 |
| kv-0aeb9100 | 5 | 0 | 0 | 5 |
| mq-138eaea1 | 5 | 0 | 0 | 5 |
| object-46bdc5a1 | 5 | 0 | 0 | 5 |
| relational-5daa227f | 8 | 1 | 0 | 9 |
| tsdb-1414ad92 | 0 | 7 | 0 | 7 |
| vector-3b00edad | 0 | 3 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-19ab5af3 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-19ab5af3 | columnar_compression_ratio | performance metrics are warning only |
