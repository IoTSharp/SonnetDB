# API Contract Reuse

This document maps SonnetDB server endpoints to VS Code extension features.

## Existing endpoints to reuse

| Endpoint | Extension usage |
|----------|-----------------|
| `GET /v1/db` | connection health, database list, explorer roots |
| `GET /v1/db/{db}/schema` | schema explorer, SQL completion seed data |
| `POST /v1/db/{db}/sql` | query execution, DDL and DML entry point |
| `POST /v1/db/{db}/measurements/{m}/lp` | line protocol import |
| `POST /v1/db/{db}/measurements/{m}/json` | JSON points import |
| `POST /v1/db/{db}/measurements/{m}/bulk` | bulk VALUES import |
| `POST /v1/copilot/chat/stream` | Copilot panel event stream |
| `GET /v1/copilot/models` | Copilot model picker |
| `GET /v1/copilot/knowledge/status` | Copilot readiness / info card |
| `GET /healthz` | connection probe |
| `GET /v1/setup/status` | detect first-run setup state |
| `/mcp/{db}` | future VS Code agent and MCP integration |

## M29 #245 management contracts consumed by VS Code

| Endpoint | Extension usage |
|----------|-----------------|
| `POST /v1/db/{db}/kv/keyspaces` | KV keyspace section in Explorer |
| `POST /v1/db/{db}/kv/{keyspace}/scan` | KV read-only preview and first-page tree expansion |
| `POST /v1/db/{db}/vector/indexes` | vector index section in Explorer |
| `POST /v1/db/{db}/vector/search-preview` | vector top-K preview command |
| `POST /v1/db/{db}/vector/embed-preview` | optional text-to-vector query input for vector preview |
| `POST /v1/db/{db}/fulltext/indexes` | full-text index section in Explorer |
| `POST /v1/db/{db}/fulltext/search-preview` | full-text preview command |
| `POST /v1/db/{db}/fulltext/analyze` | tokenizer analyze command |
| `POST /v1/db/{db}/mq/topics` | MQ topic section in Explorer |
| `POST /v1/db/{db}/mq/{topic}/browse` | MQ read-only message preview |
| `POST /v1/db/{db}/mq/{topic}/monitor` | MQ retained window and lag metadata for previews |
| `POST /v1/db/{db}/documents/{collection}/find` | read-only Document filter/projection/sort query panel and cursor paging |

The extension deliberately keeps this surface read-only. Full per-model editing, import/export, approval flows, and governance remain Web Admin / Studio responsibilities.

## Reuse from existing web admin code

The VS Code extension should copy or adapt these ideas from the current web admin:

- NDJSON result parsing from `web/src/api/sql.ts`
- schema loading from `web/src/api/schema.ts`
- SonnetDB SQL dialect keywords from `web/src/components/sonnetdb-dialect.ts`
- chart heuristics from `web/src/components/SqlResultChart.vue`
- Copilot request shape from `web/src/api/copilot.ts`

## Data models worth mirroring in the extension

- `SqlResultSet`
- `SchemaResponse`
- `MeasurementInfo`
- `ColumnInfo`
- `CopilotChatRequest`
- `CopilotChatEvent`

## Optional future endpoints

The first extension wave can ship without server changes.

Possible future additions if product gaps appear:

- a compact `sample_rows` REST endpoint for explorer previews
- query-history persistence endpoints
- a dedicated explain endpoint for editor diagnostics
- local-runtime bootstrap endpoints for extension-managed setup flows
