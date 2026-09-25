# GH-Issue #189: bounded WITH RECURSIVE evidence (2026-09-25)

Status: implemented SQL behavior and candidate-row guard; the strict single-iteration peak-memory acceptance gate remains open. Do not mark the complete issue closed from this evidence alone.

## Scope

Implemented one query-local recursive CTE of the form `anchor UNION [ALL] recursive_member`, including an optional output-column list. Ordinary CTEs before and after it expand in declaration order. The member reads only the previous frontier; the final SELECT reads the accumulated result. `UNION` deduplicates across all levels, while `UNION ALL` retains duplicates. The implementation checks cancellation on each level and row, validates output width and runtime value types, and fails at 64 levels, 100,000 retained rows, 32 MiB of estimated retained row data, or the smaller active query/database memory budget. Each anchor/member query receives a `LIMIT(candidateLimit + 1)` probe; over 100,000 raw candidates per level fail explicitly, and `UNION ALL` also probes against the remaining retained-row capacity. Unsupported recursion shapes return explicit Chinese diagnostics. `EXPLAIN` reports the worktable and limits.

## Verification

| Command / path | Result | Evidence boundary |
|---|---|---|
| `dotnet build src/SonnetDB.Core/SonnetDB.Core.csproj -c Release` | PASS, 0 warnings, 0 errors | Core compile |
| Core test filter `SqlRecursiveCteTests\|SqlCteTests\|SqlSetOperationTests` | PASS, 24/24 | Parsing, branching tree, empty anchor, cycles, depth, large branch probe and cancellation, ordinary CTE ordering and forward-reference rejection, parameters, ordering/pagination, column/type diagnostics, EXPLAIN, embedded ADO, prior CTE/set regressions |
| Server test `RecursiveCte_RestNdjsonAndFrameHttp2_ReturnSameParameterizedRows` | PASS, 1/1 | Real Kestrel, REST ADO, raw REST NDJSON, exact HTTP/2 Frame read query and parameter/result mapping |
| `git diff --check` | PASS | Whitespace integrity |

The Frame test asserts an observed `/v1/frame` request on HTTP/2. Setup writes use the REST endpoint; this evidence does not claim Frame writes. The raw REST response is `application/x-ndjson` with a meta line, two row lines, and an end line. The endpoint test also compiles the Server in Release without warnings.

## Remaining boundaries

- The candidate probe bounds rows returned from a non-sorted branch via `TableSqlExecutor.ApplyPagination` or `RelationalSelectExecutor.ApplyPagination`. It does not hard-cap memory used before those calls: `RecursiveCteExecutor.ProbeBranch` calls `SqlExecutor.ExecuteSelect`; `RelationalSelectExecutor.Execute` may reach `NestedLoopJoin.JoinRows`, which executes `right.Rows.ToArray()` before the first result row, or `HashJoin.JoinRows`, which builds a hash table and may spill. `LoadSubquery` also clones an ordinary CTE result with `result.Rows.Select(...).ToArray()`. A single large projected value can exceed the estimated byte budget before the recursive accumulator sees it. Thus the candidate/retained-row contracts are verified, but a strict peak-memory resource contract is still open.
- Executable follow-up: introduce a streaming, budget-aware branch sink at the relational/table SELECT boundary; charge projected bytes before appending each row; route JOIN build inputs through the existing `SqlQueryResources` reservation/spill mechanism, including nested-loop right inputs and ordinary CTE subquery materialization; then add adversarial wide-row and large build-side tests with measured peak allocations and cancellation during materialization. This is a separate planner/executor change because a caller-side `LIMIT` cannot constrain allocations already made by those operators.
- Runtime type validation checks non-null values actually emitted; if the anchor is empty, a recursive member is not executed and its value types cannot be observed. Output width is validated before this early termination.
- Mutually recursive definitions, recursive-member subqueries, aggregates, DISTINCT, in-definition ordering/pagination, and multiple direct self references remain explicitly unsupported.
- No full repository suite, fixed-hardware performance run, NativeAOT publish, or remote GitHub status change was performed in this isolated worktree.
