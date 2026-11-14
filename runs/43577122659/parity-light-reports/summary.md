# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | d90fed9cd1d19d7b66fd7b5734ed762e5fd17ed9 |
| GitHub run | 43577122659 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-b4d1822a | 0 | 5 | 0 | 5 |
| document-e8006de0 | 0 | 5 | 0 | 5 |
| fulltext-63429b41 | 0 | 6 | 0 | 6 |
| graph-5bc13ce8 | 1 | 0 | 0 | 1 |
| kv-bbbc94c8 | 5 | 0 | 0 | 5 |
| mq-6c8bb853 | 5 | 0 | 0 | 5 |
| object-e796383c | 5 | 0 | 0 | 5 |
| relational-ae3bc025 | 8 | 1 | 0 | 9 |
| tsdb-3e506729 | 0 | 7 | 0 | 7 |
| vector-2c95ef4c | 0 | 3 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-b4d1822a | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-b4d1822a | columnar_compression_ratio | performance metrics are warning only |
