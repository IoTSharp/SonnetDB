# Architecture Notes

## 1. Runtime model

The extension should have three layers:

1. VS Code host layer
   - commands
   - tree views
   - webview panels
   - secret storage
2. SonnetDB transport layer
   - HTTP client
   - NDJSON parser
   - Copilot SSE client
3. Managed local runtime
   - start and stop a local SonnetDB server process
   - point that server at a selected data root
4. SQL language sidecar
   - reuse the C# `SqlParser` without duplicating grammar in TypeScript
   - exchange stateless JSON-line validation requests over stdin/stdout
   - fall back to lightweight TypeScript diagnostics when unavailable

## 2. Why remote-first

Remote-first is the fastest production path because SonnetDB already exposes the required server contracts:

- list databases
- fetch schema
- fetch M29 management metadata for KV, vector, full-text, and MQ
- execute SQL
- ingest bulk payloads
- stream Copilot events
- expose MCP endpoints

That means the extension can ship useful database features without inventing a new transport.

## 3. Why not start with SonnetDB.Data inside VS Code

Using `SonnetDB.Data` directly inside the extension host would require:

- a .NET sidecar process or native bridge
- process lifecycle management
- IPC or stdio protocol work
- platform packaging complexity

The implemented language sidecar references only `SonnetDB.Core` and receives SQL text, while local database access continues to use the managed Server path.

## 4. Local mode

The implemented local-mode design is:

- user picks a local SonnetDB data root
- extension starts a managed SonnetDB server process
- extension connects to that local server through the same HTTP client used for remote mode
- extension polls `/healthz`, captures stdout/stderr in a dedicated output channel, and stops owned children on exit by default

Benefits:

- one transport model
- reuse of existing auth, schema, SQL, and Copilot endpoints
- lower implementation risk

## 5. UI surfaces

Recommended VS Code surfaces:

- activity bar container: explorer tree and connection actions
- command palette: add connection, run query, open result panel, open Copilot
- custom result webview: table, raw JSON, chart
- read-only multi-model preview commands: KV scan, MQ browse, vector search, full-text search/analyze
- optional future notebook: query workbook for demos and onboarding
- optional future chat participant: SonnetDB-aware assistant entry

## 6. Security model

- connection metadata can live in extension global state
- tokens should live in VS Code `SecretStorage`
- Copilot defaults to `read-only`
- `read-write` Copilot mode should require explicit user action

## 7. Testing strategy

- unit tests for NDJSON parsing and endpoint payload normalization
- integration tests against a local SonnetDB server process
- smoke tests for explorer tree loading and query execution
- VSIX packaging in CI; full Electron workbench automation remains a release follow-up

## 8. Language-service boundary

The TypeScript host provides schema completion, lightweight delimiter diagnostics, keyword hover, and `EXPLAIN` commands. The optional packaged sidecar now reuses the full C# parser for lexical and syntax diagnostics through an AOT-friendly JSON-line protocol. Signature help, repair suggestions, and a standard LSP transport remain later #107 slices.
