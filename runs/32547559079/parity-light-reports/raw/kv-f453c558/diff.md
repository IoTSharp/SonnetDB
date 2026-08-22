# Parity Run kv-f453c558

Started: 2026-08-22T03:00:02.7992625+00:00

| Scenario | sonnetdb | redis | Diff |
|---|---|---|---|
| set_get_scan_throughput | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| ttl_accuracy | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| incr_concurrency_16_clients | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| cas_optimistic_lock | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| scan_cursor_10m_keys | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |

## Capability gaps

| Scenario | Required | sonnetdb | redis | SonnetDB gap |
|---|---|---|---|---|
| set_get_scan_throughput | Kv, KvRangeScan | pass | pass |  |
| ttl_accuracy | Kv | pass | pass |  |
| incr_concurrency_16_clients | Kv, KvIncr | pass | pass |  |
| cas_optimistic_lock | Kv, KvCas | pass | pass |  |
| scan_cursor_10m_keys | Kv, KvRangeScan | pass | pass |  |
