# Extension smoke tests

`npm test` compiles the extension and runs the HTTP consumer smoke. The smoke starts an ephemeral loopback server and verifies that `SonnetDbClient` consumes the shared SonnetDB contracts for schema, SQL NDJSON, KV, vector, full-text, and MQ without loading the VS Code extension host.

VS Code workbench UI automation remains part of the later packaging and Marketplace task (#108). The M29 #260 gate focuses on the shared HTTP contract consumed by the current developer subset.
