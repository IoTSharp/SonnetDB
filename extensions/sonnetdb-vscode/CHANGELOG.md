# Changelog

All notable changes to the SonnetDB VS Code extension are documented here.

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
[0.1.0]: https://marketplace.visualstudio.com/items?itemName=iotsharp.sonnetdb-vscode
