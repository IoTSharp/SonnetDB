# GH-Issue #189: bounded WITH RECURSIVE evidence (2026-09-25)

Status: implemented SQL behavior, candidate-row guard, and conservative blocking-row admission for relational SELECT/JOIN/subquery paths; the strict single-iteration CLR peak-memory acceptance gate remains open. Do not mark the complete issue closed from this evidence alone.

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

- A follow-up adds `RecursiveCteBranchBudget` around anchor/member/final SELECT and charges conservative row bytes before retaining relation/table projections, nested-loop right rows, hash build rows, recursive source copies, ordinary CTE subquery copies, and sort/aggregate inputs. The branch reservation remains alive until candidate rows are transferred into the recursive worktable, and parallel charges are synchronized. A single branch rejects above 32 MiB or the active query/database reservation, with cancellation checked for each retained row. Wide-string SQL tests prove admission failure in nested-loop build and ordinary CTE materialization before their full row collections are kept, verify budget release and successful retry; direct budget tests cover a single value above 32 MiB, cancellation between rows, and concurrent budget admission. Core recursive/CTE/set/JOIN filter: 37/37 on Release. Real Kestrel REST/NDJSON/HTTP2 Frame test: 1/1 on Release after initializing the two pinned submodules in this isolated worktree. Release builds reported 0 warnings.
- This is an estimated blocking-row budget, not a measured heap ceiling. Table decoding, expression evaluation, temporary JOIN output rows, and non-relational SELECT dispatch may allocate before the admission point. A strict peak-memory acceptance claim requires adversarial allocation measurement across those paths and a bounded strategy for each remaining pre-admission allocation. Until then #189 stays open.
- Runtime type validation checks non-null values actually emitted; if the anchor is empty, a recursive member is not executed and its value types cannot be observed. Output width is validated before this early termination.
- Mutually recursive definitions, recursive-member subqueries, aggregates, DISTINCT, in-definition ordering/pagination, and multiple direct self references remain explicitly unsupported.
- No full repository suite, fixed-hardware performance run, NativeAOT publish, or remote GitHub status change was performed in this isolated worktree.
