# Parity Run relational-8ef2430b

Started: 2026-07-24T05:27:03.7625014+00:00

| Scenario | sonnetdb | postgres | Diff |
|---|---|---|---|
| relational_hello_world | ✅ pass (rows=2) | ✅ pass (rows=2) | ✅ within tolerance |
| tpcc_lite | ⏭ skipped (backend 'sonnetdb' lacks required capabilities: RelationalTpccLite) | ⏭ skipped (long profile disabled; set PARITY_TPCC_FULL=1 to run 5 warehouses for 30 minutes) | n/a (single backend) |
| fk_cascade_constraint | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| isolation_read_committed | ✅ pass (rows=2) | ✅ pass (rows=2) | ✅ within tolerance |
| subquery_correlated | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| groupby_having | ✅ pass (rows=2) | ✅ pass (rows=2) | ✅ within tolerance |
| information_schema_introspection | ✅ pass (rows=3) | ✅ pass (rows=3) | ✅ within tolerance |
| update_returning_count | ✅ pass (rows=2) | ✅ pass (rows=2) | ✅ within tolerance |
| alter_table_evolution | ✅ pass (rows=2) | ✅ pass (rows=2) | ✅ within tolerance |

## Capability gaps

| Scenario | Required | sonnetdb | postgres | SonnetDB gap |
|---|---|---|---|---|
| relational_hello_world | Relational | pass | pass |  |
| tpcc_lite | Relational, RelationalTpccLite | skipped | skipped | backend 'sonnetdb' lacks required capabilities: RelationalTpccLite |
| fk_cascade_constraint | Relational, SqlForeignKey, SqlCascadeDelete | pass | pass |  |
| isolation_read_committed | Relational, SqlReadCommitted | pass | pass |  |
| subquery_correlated | Relational, SqlSubquery, SqlCorrelatedSubquery | pass | pass |  |
| groupby_having | Relational, SqlGroupBy, SqlHaving | pass | pass |  |
| information_schema_introspection | Relational, SqlInformationSchema | pass | pass |  |
| update_returning_count | Relational, SqlUpdateCount | pass | pass |  |
| alter_table_evolution | Relational, SqlAlterTable | pass | pass |  |
