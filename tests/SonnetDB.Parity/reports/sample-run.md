# SonnetDB Parity Sample Run

| Field | Value |
|---|---|
| Profile | full |
| Status | passing |
| Pass rate | 100% |
| Scenarios | 38 passed or structured skipped / 0 failed |
| Gate policy | capability, reliability, and accuracy failures block; performance metrics warn only |

## Suites

| Suite | Scope | Competitor | Gate | Sample scenarios |
|---|---|---|---|---|
| relational | SQL tables and transactions | PostgreSQL | capability | `relational_hello_world`, `groupby_having`, `isolation_read_committed`, `alter_table_evolution` |
| tsdb | time-series write/query/aggregates | InfluxDB, VictoriaMetrics | capability + accuracy | `ingest_1m_points`, `groupby_time_window`, `derivative_accuracy`, `percentile_p95_tdigest_vs_quantile` |
| kv | key-value cache semantics | Redis | capability | `set_get_scan_throughput`, `ttl_accuracy`, `incr_concurrency_16_clients`, `cas_optimistic_lock` |
| vector | ANN and filtered vector search | Qdrant | accuracy | `ann_recall_at_10`, `filtered_search`, `upsert_during_query` |
| object | S3-compatible object operations | MinIO | capability | `putget_1gb_object`, `multipart_upload_5gb`, `range_read_offsets`, `list_objects_v2_pagination` |
| mq | topic, consumer group, replay | NATS JetStream | reliability + capability | `publish_consume_ack`, `consumer_group_offset`, `replay_after_restart`, `fan_out_10p_10c` |
| fulltext | BM25, facets, update behavior | Meilisearch | accuracy | `index_1m_documents`, `bm25_ranking_top10_overlap`, `facet_filter_query`, `incremental_update_during_query` |
| analytics | aggregate correctness and metrics | ClickHouse | accuracy; performance warning only | `groupby_time_1b_rows_wallclock`, `window_avg_7day`, `topn_per_device`, `percentile_accuracy_p50_p95_p99` |
| reliability | crash and recovery injection | local crash harness | reliability | `crash_kill9_during_fsync`, `disk_full_during_wal_append`, `power_loss_torn_record` |

## Gate Failures

No capability, reliability, or accuracy gate failures.

## Performance Warnings

| Suite | Scenario | Note |
|---|---|---|
| analytics | `groupby_time_1b_rows_wallclock` | wall-clock time is reported for trend tracking and does not block merge |
| analytics | `columnar_compression_ratio` | compression ratio is reported for trend tracking and does not block merge |

## Capability Gaps

Structured skips are allowed only when the report includes a `gap_reason`. They count as visible parity debt, not as hidden pass/fail noise. Current examples include features that SonnetDB intentionally has not claimed yet, such as typo-tolerant full-text search.
