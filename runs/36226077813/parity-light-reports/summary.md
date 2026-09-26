# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | 67d61f307d333743b50dce82b043291f141ec950 |
| GitHub run | 36226077813 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-025acd4b | 0 | 5 | 0 | 5 |
| document-34a713f8 | 0 | 5 | 0 | 5 |
| fulltext-eb17ad3f | 0 | 6 | 0 | 6 |
| graph-00006586 | 1 | 0 | 0 | 1 |
| kv-1c6d0af1 | 5 | 0 | 0 | 5 |
| mq-9f895f50 | 5 | 0 | 0 | 5 |
| object-15f969b7 | 5 | 0 | 0 | 5 |
| relational-0053e4b6 | 8 | 1 | 0 | 9 |
| tsdb-bb9c316e | 0 | 7 | 0 | 7 |
| vector-9c3c5715 | 0 | 3 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-025acd4b | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-025acd4b | columnar_compression_ratio | performance metrics are warning only |
