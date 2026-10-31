# SonnetDB Parity Summary

| Field | Value |
|---|---|
| Profile | light |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 24 passed / 27 skipped / 0 failed / 51 total |
| Warning-only performance scenarios | 2 |
| Commit | 3c40730a35140a24589e606310bcf1e2dae88ec3 |
| GitHub run | 41183248043 |

## Suites

| Suite | Passed | Skipped | Failed | Total |
|---|---:|---:|---:|---:|
| analytics-071c8983 | 0 | 5 | 0 | 5 |
| document-e262c86d | 0 | 5 | 0 | 5 |
| fulltext-02d915e7 | 0 | 6 | 0 | 6 |
| graph-be9bb18b | 1 | 0 | 0 | 1 |
| kv-c45096db | 5 | 0 | 0 | 5 |
| mq-bfa51988 | 5 | 0 | 0 | 5 |
| object-deec2412 | 5 | 0 | 0 | 5 |
| relational-ad4ff1d8 | 8 | 1 | 0 | 9 |
| tsdb-ec20b100 | 0 | 7 | 0 | 7 |
| vector-e03fa155 | 0 | 3 | 0 | 3 |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics-071c8983 | groupby_time_1b_rows_wallclock | performance metrics are warning only |
| analytics-071c8983 | columnar_compression_ratio | performance metrics are warning only |
