# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | failing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | b86bf7c94b7e47f810bf1ff49387772adb922e5f |
| GitHub run | 32547559079 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-de83d2ca | 0 | 5 | 0 | 5 |
| document-ef0c3e1d | 0 | 5 | 0 | 5 |
| fulltext-e61041cc | 0 | 6 | 0 | 6 |
| graph-fd0ccb8a | 1 | 0 | 0 | 1 |
| kv-f453c558 | 5 | 0 | 0 | 5 |
| mq-c47c090f | 5 | 0 | 0 | 5 |
| object-c9fef9a4 | 5 | 0 | 0 | 5 |
| relational-b9901042 | 8 | 1 | 0 | 9 |
| tsdb-a0360d8f | 0 | 7 | 0 | 7 |
| vector-c6edd4ee | 0 | 3 | 0 | 3 |

## Gate Failures

| Gate | Suite | Scenario | Gap reason | Reason |
|---|---|---|---|---|
| capability | dotnet-test | parity | parity_test_failed | dotnet test exited with code 1 |

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-de83d2ca | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-de83d2ca | columnar_compression_ratio | performance metrics are warning only |
