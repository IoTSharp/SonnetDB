# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | cf8c70e0032e2015590eef32300584b077831396 |
| GitHub run | 36836621419 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-8a1ba180 | 0 | 5 | 0 | 5 |
| document-59b1665f | 0 | 5 | 0 | 5 |
| fulltext-273d14b7 | 0 | 6 | 0 | 6 |
| graph-01b898d4 | 1 | 0 | 0 | 1 |
| kv-1b0d5085 | 5 | 0 | 0 | 5 |
| mq-68382b1e | 5 | 0 | 0 | 5 |
| object-72e974f1 | 5 | 0 | 0 | 5 |
| relational-0e5ec5ea | 8 | 1 | 0 | 9 |
| tsdb-04fdab8d | 0 | 7 | 0 | 7 |
| vector-1f0fdafd | 0 | 3 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-8a1ba180 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-8a1ba180 | columnar_compression_ratio | performance metrics are warning only |
