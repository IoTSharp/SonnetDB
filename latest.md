# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | full |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 49 passed / 2 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | 55bf87158d182147a6cb801e02bf8bf62d0ff942 |
| GitHub run | 42355658053 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-4784544d | 5 | 0 | 0 | 5 |
| document-d2df1b86 | 5 | 0 | 0 | 5 |
| fulltext-b047e9f8 | 6 | 0 | 0 | 6 |
| graph-49df254d | 1 | 0 | 0 | 1 |
| kv-e043e921 | 5 | 0 | 0 | 5 |
| mq-be675f4e | 5 | 0 | 0 | 5 |
| object-4a2bfcb4 | 5 | 0 | 0 | 5 |
| relational-32a480e8 | 8 | 1 | 0 | 9 |
| tsdb-f128a80e | 6 | 1 | 0 | 7 |
| vector-649414bd | 3 | 0 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-4784544d | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-4784544d | columnar_compression_ratio | performance metrics are warning only |
