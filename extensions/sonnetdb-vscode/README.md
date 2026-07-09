# SonnetDB for VS Code

This directory contains the first usable implementation slice of the official SonnetDB VS Code extension.

The extension is intended to be:

- remote-first for the first production slice
- TypeScript-native in the VS Code extension host
- able to reuse the existing SonnetDB HTTP, schema, Copilot, and MCP contracts
- compatible with a later managed local-server mode for opening local data roots

## Current scope

The current implementation focuses on the developer read-only surface from M29 #259. It provides:

- remote connection profiles stored in VS Code global state
- bearer tokens stored in VS Code `SecretStorage`
- Explorer loading databases, schema, KV keyspaces, vector indexes, full-text indexes, and MQ topics
- SQL execution through `POST /v1/db/{db}/sql`
- a Query Result webview with Table, Raw, and Chart tabs
- KV and MQ read-only preview panels
- vector and full-text search preview commands over the M29 #245 contracts
- a Copilot panel connected to `/v1/copilot/chat/stream`, defaulting to `read-only`

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
   │  └─ runQueryCommand.ts
   ├─ core/
   │  ├─ config.ts
   │  ├─ sonnetdbClient.ts
   │  └─ types.ts
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

## First implementation wave

The recommended first wave is:

1. `#99` extension bootstrap and manifest
2. `#100` remote connection model and secret storage
3. `#101` explorer tree and schema refresh
4. `#102` SQL execution flow and query command
5. `#103` result panel with table, raw, and chart tabs
6. `#104` Copilot stream panel
7. `#259` M29 multi-model read-only consumption

See [ROADMAP.md](./ROADMAP.md) for the detailed split.
