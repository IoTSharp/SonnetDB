# SonnetDB for VS Code

This directory contains the preview implementation of the official SonnetDB VS Code extension.

The extension is intended to be:

- remote-first for the first production slice
- TypeScript-native in the VS Code extension host
- able to reuse the existing SonnetDB HTTP, schema, Copilot, and MCP contracts
- able to start a managed local SonnetDB Server for a selected data root

## Current scope

The current implementation provides:

- remote connection profiles stored in VS Code global state
- bearer tokens stored in VS Code `SecretStorage`
- connection health/setup probing, active connection/database status, and connection editing
- Explorer loading databases, measurements/columns, tables/columns, KV keyspaces, vector indexes, full-text indexes, and MQ topics
- current-statement and selection SQL execution with schema-aware completion, diagnostics, hover, and `EXPLAIN`
- a reusable Query Result webview with Table, Raw, and Chart tabs, local history, and CSV/JSON export
- KV and MQ read-only preview panels
- vector and full-text search preview commands over the M29 #245 contracts
- a Copilot panel connected to `/v1/copilot/chat/stream`, defaulting to `read-only`, with model, knowledge, and citation status
- extension-managed local Server start/stop, output, health polling, and data-root profiles
- create-measurement drafts, confirmed bulk import, starter snippets, and SQL reference links

## Install a local VSIX

```powershell
npm ci
npm run package:vsix
code --install-extension dist/sonnetdb-vscode.vsix
```

Open the SonnetDB activity bar, add a remote connection, select a database, then open a SQL document. `Ctrl+Enter` runs the statement under the cursor; `Ctrl+Shift+Enter` runs the selection.

Connection metadata is stored in VS Code global state. Bearer tokens are stored only in `SecretStorage`. Copilot remains read-only until the user confirms a read-write mode switch. Bulk import displays its target and payload size in a native modal before sending data.

## Managed local Server

Run `SonnetDB: Start Managed Local Server`, select a data root, and select a Server executable, DLL, or project when auto-discovery cannot find one. The extension uses the same HTTP client and Explorer as remote mode. It stops child processes on extension shutdown unless `sonnetdb.managedLocal.keepRunningOnExit` is enabled.

Relevant settings:

- `sonnetdb.managedLocal.baseUrl`
- `sonnetdb.managedLocal.serverPath`
- `sonnetdb.managedLocal.keepRunningOnExit`

## Directory layout

```text
extensions/sonnetdb-vscode/
├─ README.md
├─ ROADMAP.md
├─ package.json
├─ tsconfig.json
├─ .gitignore
├─ .vscodeignore
├─ media/
│  └─ sonnetdb.svg
├─ docs/
│  ├─ architecture.md
│  └─ api-contract.md
└─ src/
   ├─ extension.ts
   ├─ commands/
   ├─ language/
   ├─ ui/
   ├─ core/
   │  ├─ connectionService.ts
   │  ├─ managedServerService.ts
   │  ├─ sonnetdbClient.ts
   │  └─ sqlText.ts
   ├─ panels/
   │  ├─ copilotPanel.ts
   │  └─ queryResultPanel.ts
   └─ tree/
      └─ sonnetdbTreeDataProvider.ts
```

## Working principles

- Phase 1 reuses `POST /v1/db/{db}/sql`, `GET /v1/db/{db}/schema`, `GET /v1/db`, and `POST /v1/copilot/chat/stream`.
- M29 #259 also reuses `POST /v1/db/{db}/kv/keyspaces`, `POST /v1/db/{db}/kv/{keyspace}/scan`, `POST /v1/db/{db}/vector/indexes`, `POST /v1/db/{db}/vector/search-preview`, `POST /v1/db/{db}/fulltext/indexes`, `POST /v1/db/{db}/fulltext/search-preview`, `POST /v1/db/{db}/fulltext/analyze`, `POST /v1/db/{db}/mq/topics`, `POST /v1/db/{db}/mq/{topic}/browse`, and `POST /v1/db/{db}/mq/{topic}/monitor`.
- Tokens should live in VS Code `SecretStorage`, not in plain-text workspace settings.
- The first local-mode implementation should start a managed SonnetDB server process for a selected data root instead of embedding the .NET engine directly into the Node extension host.
- The existing web admin code is the primary reference for NDJSON parsing, SQL dialect keywords, schema completion, chart rendering, and Copilot request payloads.

## Verification

```powershell
npm test
npm run package:vsix
```

See [ROADMAP.md](./ROADMAP.md) for the detailed split.
