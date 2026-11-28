# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | f9a6c17e604194fe5dbb596e130d589af8ec5b63 |
| GitHub run | 46132645717 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-02e04607 | 0 | 5 | 0 | 5 |
| document-65bdd539 | 0 | 5 | 0 | 5 |
| fulltext-8b981e87 | 0 | 6 | 0 | 6 |
| graph-93c6015c | 1 | 0 | 0 | 1 |
| kv-1a6563f3 | 5 | 0 | 0 | 5 |
| mq-9606ea78 | 5 | 0 | 0 | 5 |
| object-1f0076d5 | 5 | 0 | 0 | 5 |
| relational-6f3ec146 | 8 | 1 | 0 | 9 |
| tsdb-e738f1e7 | 0 | 7 | 0 | 7 |
| vector-eb2f8546 | 0 | 3 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-02e04607 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-02e04607 | columnar_compression_ratio | performance metrics are warning only |
