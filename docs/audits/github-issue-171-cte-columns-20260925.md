# GH-Issue #171: non-recursive CTE output columns

The original [Issue #171](https://github.com/IoTSharp/SonnetDB/issues/171) requires a positional output-column list in a non-recursive CTE. The earlier closure explicitly left this syntax unsupported. This follow-up completes that local SQL contract without changing the bounded recursive CTE contract tracked by #189.

The parser already recorded the list. The ordinary CTE expander now checks duplicate names and carries the list to the CTE query result. The SQL executor validates the final result width, including empty results, and renames columns after the CTE's own sorting, pagination, DISTINCT and set operations. The existing derived-table path then exposes those names to later CTEs, JOIN, `IN` and `EXISTS`. Row values and declared projection types are unchanged.

| Verification | Result | Scope |
|---|---|---|
| Release Core `SqlCteTests` | PASS 13/13 | Parser order, single/multiple CTEs, JOIN, aggregate, parameters, `IN`/`EXISTS`, CTE and outer ordering/pagination, `UNION ALL`, empty-result width mismatch, duplicate names, relation/measurement/document sources, embedded ADO name and Int64 type. |
| Release Core CTE/recursive/set/ADO filter | 39/40 | Only `SqlRecursiveCteTests.Execute_AlternatingWideRows_RejectsAtCumulativeStringBudget` failed: it expects the cumulative result-byte diagnostic, while the existing shared query budget rejects first. That test is unchanged in this branch and is tracked in the #189 integration. |

The local Core result does not establish real REST/Frame parity or published-package compatibility. Those require separate service and release evidence. Historical audit snapshots retain their original closure-time state.
