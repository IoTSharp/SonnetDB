# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | full |
| Status | failing |
| Pass rate | 100% |
| Scenarios | 49 passed / 2 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | c194debdcac2b497bf60c20d1de032d97d4ee30c |
| GitHub run | 39010884412 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-070ec167 | 5 | 0 | 0 | 5 |
| document-661894a7 | 5 | 0 | 0 | 5 |
| fulltext-4e855864 | 6 | 0 | 0 | 6 |
| graph-7063b1fb | 1 | 0 | 0 | 1 |
| kv-f923d542 | 5 | 0 | 0 | 5 |
| mq-85a5b404 | 5 | 0 | 0 | 5 |
| object-572587bd | 5 | 0 | 0 | 5 |
| relational-5d17e911 | 8 | 1 | 0 | 9 |
| tsdb-2e7edca6 | 6 | 1 | 0 | 7 |
| vector-30ff7bc9 | 3 | 0 | 0 | 3 |

## Gate Failures

| Gate | Suite | Scenario | Gap reason | Reason |
|---|---|---|---|---|
| reliability | dotnet-test | crash-reliability | reliability_test_failed | crash reliability test exited with code 1 |

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-070ec167 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-070ec167 | columnar_compression_ratio | performance metrics are warning only |
