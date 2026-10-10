# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | full |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 49 passed / 2 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | c40fa670e3a170c5fac8cefa181d618631862270 |
| GitHub run | 38037066438 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-5895d1c3 | 5 | 0 | 0 | 5 |
| document-ddeaf6fd | 5 | 0 | 0 | 5 |
| fulltext-03e2db23 | 6 | 0 | 0 | 6 |
| graph-bd537347 | 1 | 0 | 0 | 1 |
| kv-e0b8aff3 | 5 | 0 | 0 | 5 |
| mq-a7619df1 | 5 | 0 | 0 | 5 |
| object-b4782259 | 5 | 0 | 0 | 5 |
| relational-a64d0d09 | 8 | 1 | 0 | 9 |
| tsdb-5f3be059 | 6 | 1 | 0 | 7 |
| vector-c862915e | 3 | 0 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-5895d1c3 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-5895d1c3 | columnar_compression_ratio | performance metrics are warning only |
