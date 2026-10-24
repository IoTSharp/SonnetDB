# Issue #348 ADO schema/table metadata audit (2026-10-24)

## Scope

This slice starts from `origin/main` at `66fb071f8c930534eb540fb16e6d44b69720a36c` (the #347 merge).
The production implementation from `a9787390` already carries relation-table schema identity through the
ADO `Tables`, `Columns`, `Indexes`, and `ForeignKeys` collections, the Server schema response, and the
remote ADO DTO/client. This audit adds the missing consumer-facing regression coverage and documents the
contract instead of introducing a second metadata path.

## Contract checked by inspection

- An unqualified relation table belongs to `main`; two-part names retain the saved schema spelling.
- `TABLE_SCHEMA` and `TABLE_NAME` are emitted together in all relation-table ADO collections. Foreign-key
  rows additionally expose `PRINCIPAL_TABLE_SCHEMA` (appended after the legacy columns).
- Restriction slots follow `[catalog, schema, table, column/index/constraint]` and match saved names
  ordinally. This prevents `Archive.orders` from being returned by an `archive` restriction.
- `/v1/db/{db}/schema` includes table `SchemaName` and foreign-key `PrincipalSchemaName`; the remote client
  restores those values into `TableSchema` before building ADO rows.
- View and document catalogs are currently single-schema catalogs. Their ADO rows intentionally report
  `main`; the provider does not infer a schema that the core catalog cannot represent.

## Changes in this slice

- `TsdbAdoApiTests` now covers two same-named tables in different schemas, mixed-case schema restriction,
  columns/indexes/cross-schema foreign-key identity, and metadata after close/reopen.
- `RemoteAdoEndToEndTests` covers the same table/column/index/foreign-key identity over REST and after a
  remote connection reopen; existing embedded/remote matrix assertions also pin the default `main` output
  for views and foreign keys.
- `docs/ado-net.md` documents the DbFirst metadata collections, restrictions, default `main`, quoted
  identity behavior, remote schema transport, and the current view/document boundary.
- `CHANGELOG.md` records the #348 validation slice and its HTTP/2/formal-package evidence boundary.

## Verification boundary

- `git diff --check`: run before commit (result recorded with the final commit).
- `dotnet restore SonnetDB.slnx`, the required `dotnet format` check, and Core/Server test suites are
  **NOT_RUN** in this environment because the .NET SDK is unavailable (`dotnet: command not found`).
- Exact HTTP/2 frame transport and the external FreeSql provider package remain separate environment/release
  evidence; this repository slice does not claim those checks passed.

No SQL, catalog, wire-format, or storage-format behavior is changed by this audit slice.
