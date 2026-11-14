# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | full |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 49 passed / 2 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | d90fed9cd1d19d7b66fd7b5734ed762e5fd17ed9 |
| GitHub run | 43577122659 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-b5f47b6f | 5 | 0 | 0 | 5 |
| document-b5b9ff61 | 5 | 0 | 0 | 5 |
| fulltext-d4a6dcf6 | 6 | 0 | 0 | 6 |
| graph-a046cba2 | 1 | 0 | 0 | 1 |
| kv-28b04da7 | 5 | 0 | 0 | 5 |
| mq-95c7e9df | 5 | 0 | 0 | 5 |
| object-5e31a68a | 5 | 0 | 0 | 5 |
| relational-351914d5 | 8 | 1 | 0 | 9 |
| tsdb-2cd8f6ff | 6 | 1 | 0 | 7 |
| vector-1590071a | 3 | 0 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-b5f47b6f | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-b5f47b6f | columnar_compression_ratio | performance metrics are warning only |
