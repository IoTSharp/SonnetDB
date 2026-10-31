# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | full |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 49 passed / 2 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | 3c40730a35140a24589e606310bcf1e2dae88ec3 |
| GitHub run | 41183248043 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-864632cc | 5 | 0 | 0 | 5 |
| document-823a417c | 5 | 0 | 0 | 5 |
| fulltext-988f19e9 | 6 | 0 | 0 | 6 |
| graph-b639b297 | 1 | 0 | 0 | 1 |
| kv-fca68925 | 5 | 0 | 0 | 5 |
| mq-62922c64 | 5 | 0 | 0 | 5 |
| object-9c370f29 | 5 | 0 | 0 | 5 |
| relational-6e4a32dd | 8 | 1 | 0 | 9 |
| tsdb-493e1098 | 6 | 1 | 0 | 7 |
| vector-450c4ac5 | 3 | 0 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-864632cc | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-864632cc | columnar_compression_ratio | performance metrics are warning only |
