# GH-Issue #195 INSERT SELECT acceptance (2026-09-25)

Source: [GH-Issue #195](https://github.com/IoTSharp/SonnetDB/issues/195), read from the live GitHub API on 2026-09-25. The published 3.1.0 package does not support this syntax. Current main already has a relation-table `INSERT INTO ... SELECT` path; this review covers gaps in that path. No release version is assigned to the changes in this worktree.

## Contract

- The source query is fully materialized before target mutations. A same-table copy therefore reads the pre-insert rows once. The target column count must match the SELECT projection; normal target type, nullability, generated-column, key and constraint checks apply.
- Source SELECT supports the existing WHERE, JOIN, aggregation, derived-query, parameter-binding and pagination contracts. After the source query materializes, `MaxTriggerTransitionRows` and `MaxTriggerTransitionBytes` reject an oversized result before target writes; they do not bound the source SELECT's peak materialization memory. Decimal projection uses invariant exact text at the target conversion boundary so it does not pass through binary floating point.
- An empty SELECT inserts zero rows and yields an empty RETURNING result. Duplicate keys fail the statement atomically unless an explicit `ON CONFLICT` action handles them. `RETURNING` sees generated identifiers and the final accepted rows. An explicit transaction sees its buffered source rows and rollback removes target writes.
- `returning` is treated as a clause after the source SELECT, not as an implicit projection/table alias. A literal alias of that name requires `AS returning`.
- Frame SQL itself remains a read-only endpoint. The remote ADO `frame-http2` transport sends writes over the REST SQL endpoint on the same HTTP/2 connection and reads through `/v1/frame`; this is the current transport contract, not direct Frame DML support.

## Evidence

| Gate | Result | Command / scope |
| --- | --- | --- |
| Core relation SQL | PASS 8/8 | `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~SqlInsertSelectTests" --verbosity quiet` |
| Embedded, real Kestrel REST ADO and exact HTTP/2 | PASS 5/5 | `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore --filter "FullyQualifiedName~InsertSelect_AsyncEmptyAndDuplicateCompositeKey\|FullyQualifiedName~InsertSelect_SameTableAsync\|FullyQualifiedName~FrameHttp2_InsertSelectAsync" --verbosity quiet` |
| Direct REST NDJSON | PASS 1/1 | `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore --filter "FullyQualifiedName~Rest_InsertSelectReturning_StreamsDeclaredMetaRowsAndEmptyResult" --verbosity quiet` |
| Related Kestrel regression | PASS 84/84 | `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore --filter "FullyQualifiedName~RemoteAdoEndToEndTests\|FullyQualifiedName~RemoteAdoHttp2TransportTests\|FullyQualifiedName~SqlFrameEndpointTests" --verbosity quiet` |

The Core tests cover exact DECIMAL, generated id and RETURNING, transaction read-your-writes and rollback, projection mismatch, `ON CONFLICT DO NOTHING`, empty SELECT, duplicate composite-key atomicity with `table_unique_violation`, same-table copy of two source rows, and parameterized JOIN with aggregation. The real Kestrel REST tests compare embedded/remote asynchronous empty-result reader/scalar/nonquery behavior, duplicate composite-key error code and statement atomicity, and parameterized same-table source snapshots. The direct REST test inspects NDJSON meta, generated-column schema, ordered rows, end row count/affected count, and the empty-source meta/end pair. The exact HTTP/2 test confirms ADO writes use REST SQL while reads use Frame; a direct HTTP/2 Frame `INSERT SELECT` request returns `bad_request` and leaves the target unchanged.

The published package and fixed-hardware capacity have not been independently validated by this report. GH-Issue #195 remains open until its complete acceptance is verified and delivered.
