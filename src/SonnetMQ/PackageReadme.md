# SonnetMQ

`SonnetMQ` is the embedded message queue library used by SonnetDB.

It provides append-only topic logs, consumer group offsets, pull/ack semantics, retention, and replay after restart. The package is intended for local embedded scenarios and has no third-party runtime dependency.

## Install

```bash
dotnet add package SonnetMQ
```

For service APIs, SQL integration, and admin UI support, install or run SonnetDB.
