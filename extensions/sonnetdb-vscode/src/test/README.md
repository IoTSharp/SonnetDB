# Extension smoke tests

`npm test` compiles the extension and runs the HTTP consumer and language-helper smoke tests. The consumer smoke starts an ephemeral loopback server and verifies that `SonnetDbClient` consumes the shared SonnetDB contracts for schema, SQL NDJSON, KV, vector, full-text, and MQ without loading the VS Code extension host. Pure tests also cover SQL statement extraction, delimiter diagnostics, function signature context, quick-fix edits, and the sidecar JSON-line client.

Release validation additionally installs the generated VSIX into an isolated VS Code user-data and extension directory. Automated Electron workbench journeys remain a follow-up; they do not replace the HTTP contract and package-content gates.
