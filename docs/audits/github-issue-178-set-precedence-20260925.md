# GH-Issue #178 set-operation precedence (2026-09-25)

The previous implementation evaluated all set operations from left to right.
That makes `A UNION B INTERSECT C` behave as `(A UNION B) INTERSECT C`, contrary
to standard SQL precedence. The issue was reopened for this observable gap.

Execution now evaluates consecutive INTERSECT operands as a group before
applying UNION, UNION ALL or EXCEPT. Operators at the same precedence remain
left associative. Result ordering and pagination still apply after the full
set expression. The regression distinguishes both UNION and EXCEPT from the
old left-associative result and retains column-count diagnostics.

Release verification on the integrated tree:

- `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SqlSetOperationTests' --nologo`: 4/4 passed.
- `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --no-restore --nologo`: 1109/1109 passed before the precedence change; this is not a post-change full-server claim.

`INTERSECT ALL` and `EXCEPT ALL` remain unsupported, as documented in the SQL
reference. These tests are local Core evidence, not a fixed-hardware gate.

## Completion follow-up

The original issue's acceptance also requires input order for `UNION ALL`,
`NULL`/duplicate/multicolumn equality, invalid type diagnostics, and a large
input memory policy. The follow-up validates column types from branch metadata
even for empty results, inferring non-`NULL` value types when metadata is
unavailable. Differing known types fail with a column number and an explicit
`CAST` hint; this prevents lossy implicit numeric coercion. Decimal values
remain `decimal` during set equality and hashing, so distinct high-precision
values cannot collapse through a `double` conversion. The first branch
supplies output names, and the combined result keeps inferred type metadata.

Set results reserve an estimated row budget and hash-state budget through the
existing per-query/database blocking-operator resource manager. Exhaustion
rejects the query with a set-operation error; this operator does not spill.
Reservation is released on success and exception. The estimate is an admission
limit after branch execution, not a strict CLR heap-peak limit.

- Final `SqlSetOperationTests`: 11/11 passed (Release, local Core).
- Related CTE, recursive CTE, INSERT SELECT and blocking-operator spill tests:
  83/83 passed (Release, local Core).
- Final integrated Core suite: 5233/5233 passed (Release, local Core,
  `--no-build --no-restore`).
- Full Core first pass before the final decimal comparison and extra test cases:
  5229/5230 passed. The only failure was an unrelated socket-disposal race in
  `KvRedirectTests.Create_WithMixedRedirectPolicies`; both variants passed in
  the final 13-test focused run (11 set-operation cases plus 2 KV reruns).
  The full-suite count is not presented as a final-tree full pass.

## Additional mixed-expression regression coverage

Review base: `ee2f095f4a5f215357980690d0b33e92dc338886` (main).
The precedence fix is already present; this follow-up changes tests and
documentation only.

`SqlParser.ParseSelect` retains operands and operator kinds in source order,
then attaches ORDER BY and pagination to the compound statement. That flat
representation is sufficient: `SqlExecutor.ExecuteUnion` accumulates each
consecutive INTERSECT group before applying a pending UNION, UNION ALL or
EXCEPT. Lower-precedence operators are applied left-to-right, and ordering
and pagination are applied once to the combined result. No parser/AST
redesign or additional executor change is needed for this issue.

The additional 21 cases in `SqlSetOperationTests` cover:

| Cases | Coverage |
| --- | --- |
| 2 | Compound ORDER BY and both pagination syntaxes belong to the root AST, not the last operand. |
| 14 | UNION ALL with INTERSECT; consecutive and multiple intersection groups; mixed EXCEPT; left associativity of UNION/UNION ALL/EXCEPT; empty input and empty intersection groups. |
| 3 | UNION ALL branch order and multiplicity; final descending sort with LIMIT/OFFSET and OFFSET/FETCH; output name from the first branch. |
| 1 | A derived table explicitly groups UNION ALL before an outer INTERSECT. |
| 1 | NULL and multicolumn row comparison: INTERSECT deduplicates its group while outer UNION ALL retains duplicates. |

Expected rows are explicit, not calculated by another copy of the executor.
For example, with A={1,2}, B={2,3}, C={2,4},
`A UNION ALL B INTERSECT C` must yield {1,2,2}; the old left fold yields {2}.

Verification used .NET SDK 10.0.100 on Linux with the repository's declared
dependencies and warning/AOT analysis settings. Single-node MSBuild was used
because the environment's parallel restore exited without diagnostics.

```sh
dotnet restore tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --disable-parallel -m:1
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release \
  --no-restore -m:1 -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~SqlSetOperationTests'
```

- Current implementation: 32/32 passed, including all 21 additional cases.
- Mutation check: temporarily replacing intersection grouping with the old
  left-to-right fold produced 13 assertion failures (12 additional cases and
  the existing precedence regression); the other 19 cases passed. Column
  validation and memory accounting were retained in this mutation.
- The executor source was restored byte-for-byte after the mutation; no
  production-source change is part of this follow-up.
- Final rebuild and rerun after restoring the executor: 32/32 passed,
  0 skipped. `git diff --check` passed and `git diff -- src` was empty.

These are focused local Core tests. The earlier full Core and server-suite
figures above belong to the earlier tree, not this follow-up.
