# SonnetDB VS Code Extension Roadmap

This roadmap is the implementation plan for the official SonnetDB VS Code extension.

## Product direction

The extension should feel like an official database product surface, not only a SQL syntax helper.

Core goals:

- connect to remote SonnetDB servers
- browse databases, measurements, and columns
- run SonnetDB SQL with schema-aware completion
- render query results in table, raw, and chart views
- expose SonnetDB Copilot inside VS Code
- add managed local-server mode for opening local data directories

## Design constraints

- The VS Code host stays TypeScript-first.
- Phase 1 does not require `SonnetDB.Data` inside the extension host.
- Phase 1 reuses existing SonnetDB HTTP endpoints.
- Local mode is implemented as "managed server for a selected data root", not as direct embedded-engine hosting in Node.
- The extension should keep the write path explicit and safe, especially for Copilot.

## PR plan

| PR | Theme | Status |
|----|-------|--------|
| #99 | Extension bootstrap: `package.json`, commands, activity bar container, tree view scaffold, base TypeScript structure | Implemented |
| #100 | Remote connection profiles: connection model, `SecretStorage`, health check, setup-state detection, active connection selection | Implemented |
| #101 | Explorer tree: connections -> databases -> measurements -> columns, schema refresh, sample rows entry point | Implemented |
| #102 | SQL execution: run current statement, run selection, NDJSON parser, raw result model, schema-aware completion bootstrap | Implemented |
| #103 | Result UI: webview panel with table/raw/chart tabs, query history, export-to-file hooks | Implemented |
| #104 | Copilot panel: `/v1/copilot/chat/stream`, mode switch (`read-only` / `read-write`), model selector, citation view | Implemented |
| #105 | Managed local mode: start/stop SonnetDB server for a selected data root, port detection, bootstrap flow | Implemented |
| #106 | Productivity features: create-measurement wizard, bulk import flow, starter snippets, open help from editor context | Implemented |
| #107 | Language-service sidecar: SQL diagnostics, hover, richer completion, explain and repair hooks | In progress: TypeScript diagnostics/hover/explain are implemented; C# parser sidecar remains |
| #108 | Packaging and release: tests, CI, VSIX build, Marketplace metadata, docs, screenshots | In progress: repeatable VSIX/CI/docs/metadata are implemented; Marketplace publish and Electron screenshots remain |

## First wave acceptance criteria

The first wave is `#99` through `#103`.

It is complete when:

- a user can add a remote SonnetDB server connection
- the explorer can show databases and schema
- the editor can run SQL against a selected database
- result sets render in a dedicated panel
- time-series results can be switched to a chart view

## Suggested execution order

```text
#99 -> #100 -> #101 -> #102 -> #103
                    -> #104
          -> #105
               -> #106 -> #107 -> #108
```

## Dependency map to existing server contracts

- `GET /v1/db`
- `GET /v1/db/{db}/schema`
- `POST /v1/db/{db}/sql`
- `POST /v1/db/{db}/measurements/{m}/lp|json|bulk`
- `GET /v1/copilot/models`
- `POST /v1/copilot/chat/stream`
- `GET /v1/copilot/knowledge/status`
- `GET /healthz`
- `GET /v1/setup/status`

## M29 #259 extension bridge

The M29 #259 slice extends this roadmap without turning VS Code into the full Web Admin workbench:

- consumes #245 management metadata for KV, vector, full-text, and MQ
- adds read-only preview commands for KV scan and MQ browse
- adds vector search-preview and full-text search/analyze commands
- adds a read-only Document find panel with filter/projection/sort and cursor paging
- reuses the Query Result panel for multi-model previews
- keeps write operations and governance workflows in Web Admin / Studio

## Notes on local mode

Local mode is implemented by `#105`.

Reason:

- it does not embed the .NET engine in the extension host
- it reuses the server endpoints already proven by Web Admin and ADO.NET remote mode
- it gives the extension a single transport model for remote and managed-local scenarios
