# Changelog

All notable changes to the SonnetDB VS Code extension are documented here.

## [0.4.1] - 2026-07-14

### Changed

- Rewrite the Marketplace page as an end-user guide covering installation, connections, queries, result tabs, multi-model previews, Copilot, managed local Server usage, commands, settings, and troubleshooting.
- Move directory layout, working principles, build, test, and packaging instructions into repository-only contributor documentation excluded from the VSIX.
- Use the canonical SonnetDB brand mark from `web/public/favicon` for both the Marketplace and Activity Bar assets instead of an extension-specific drawing.

## [0.4.0] - 2026-07-14

### Added

- GEOPOINT-aware `Trajectory` view in the Query Result webview, with automatic GeoJSON / `POINT(...)` parsing, time ordering, low-cardinality grouping, coordinate bounds, and VS Code theme-aware track rendering.
- Unit coverage for geographic value parsing, grouped trajectory inference, ordering, bounds, and vector-column false-positive prevention.

### Changed

- Extension Host smoke now verifies that the packaged Query Result panel contains the Trajectory surface.

## [0.3.0] - 2026-07-13

### Added

- Read-only Object Bucket Explorer nodes and object metadata previews.
- Runtime monitor snapshots for instance health and SonnetMQ lag, retention, and dead-letter state.
- Isolated VS Code Extension Host smoke coverage for activation, commands, diagnostics, signature help, and quick fixes.

### Changed

- Move the C# SQL parser sidecar transport to JSON-RPC 2.0 messages with standard LSP `Content-Length` framing.
- Add a Windows Extension Host job to the management workbench workflow.

## [0.2.0] - 2026-07-12

### Added

- Packaged C# SQL parser diagnostics with a lightweight TypeScript fallback.
- SonnetDB SQL function signature help and delimiter quick fixes.
- Explorer and read-only previews for Document, KV, vector, full-text, and MQ data.
- Managed local Server lifecycle commands, query history, export, and bulk import confirmation.

### Changed

- Publish the language server as portable .NET 10 DLLs for one cross-platform VSIX.
- Add the Marketplace icon and release metadata required by publisher `iotsharp`.

## [0.1.0] - 2026-07-11

### Added

- Initial public preview with connection profiles, SecretStorage tokens, schema Explorer, SQL execution, result views, and read-only Copilot.

[0.2.0]: https://github.com/IoTSharp/SonnetDB/compare/vscode-v0.1.0...vscode-v0.2.0
[0.3.0]: https://github.com/IoTSharp/SonnetDB/compare/vscode-v0.2.0...vscode-v0.3.0
[0.4.0]: https://github.com/IoTSharp/SonnetDB/compare/vscode-v0.3.0...vscode-v0.4.0
[0.4.1]: https://github.com/IoTSharp/SonnetDB/compare/vscode-v0.4.0...vscode-v0.4.1
[0.1.0]: https://marketplace.visualstudio.com/items?itemName=iotsharp.sonnetdb-vscode
