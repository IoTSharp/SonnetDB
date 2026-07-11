# Language Service Sidecar

The first #107 slice is implemented by `src/SonnetDB.LanguageServer` and the extension-host client in `src/core/sqlLanguageServerClient.ts`.

Current responsibilities:

- parse complete SQL scripts with the C# `SqlParser`
- return lexical and syntax diagnostics with source offsets
- use one JSON object per stdin/stdout line
- remain stateless and receive no credentials or database paths

The packaged VSIX carries a framework-dependent .NET 10 build under `language-server/`. The TypeScript delimiter scanner remains active as a fallback. Standard LSP framing, richer completion, signature help, and repair suggestions remain later #107 work.
