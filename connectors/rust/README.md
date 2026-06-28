# SonnetDB Rust Connector

The Rust connector wraps the stable SonnetDB C ABI from `connectors/c` with a small safe API.

It exposes:

- `Connection::open` / `Connection::execute` / `Connection::execute_non_query`
- `Connection::execute_bulk` with measurement/on_error/flush options
- `Connection::open_kv` with get/set/delete/scan/ttl/incr/compare-and-set helpers
- `Connection::open_document_collection` with create/drop/insert/update/delete/find/aggregate helpers
- `ResultSet` forward-only cursors with typed getters
- `Connection::flush`, `sonnetdb::version`, and `sonnetdb::last_error`
- hand-maintained FFI bindings for `connectors/c/include/sonnetdb.h`

The first version intentionally does not implement SQL parameters because the native ABI currently accepts a single SQL string.

## Requirements

- Rust 1.75+ recommended
- a native SonnetDB C library built for the current platform:
  - Windows: `SonnetDB.Native.dll` plus `SonnetDB.Native.lib`
  - Linux: `SonnetDB.Native.so`

Build the C connector first:

```powershell
cmake -S connectors/c --preset windows-x64
cmake --build artifacts/connectors/c/win-x64 --config Release
```

## Run the Quickstart on Windows

```powershell
cd connectors/rust
$native = (Resolve-Path ../c/native/SonnetDB.Native/bin/Release/net10.0/win-x64/native).Path
$env:SONNETDB_NATIVE_LIB_DIR = $native
$env:PATH = "$native;$env:PATH"
cargo run --example quickstart
```

## Run the Quickstart on Linux

```bash
cmake -S connectors/c --preset linux-x64
cmake --build artifacts/connectors/c/linux-x64

cd connectors/rust
native="$(realpath ../../artifacts/connectors/c/linux-x64)"
SONNETDB_NATIVE_LIB_DIR="$native" LD_LIBRARY_PATH="$native:${LD_LIBRARY_PATH:-}" \
  cargo run --example quickstart
```

## API Sketch

```rust
let connection = sonnetdb::Connection::open("./data-rust")?;
connection.execute_non_query("CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)")?;

let mut result = connection.execute("SELECT time, host, usage FROM cpu LIMIT 10")?;
while result.next()? {
    let ts = result.get_i64(0)?;
    let host = result.get_text(1)?.unwrap_or_default();
    let usage = result.get_f64(2)?;
    println!("{ts}\t{host}\t{usage:.3}");
}
```

## KV API

```rust
let kv = connection.open_kv("app-cache", Some("devices"))?;
let version = kv.set("edge-1", b"online", None)?;
let entry = kv.get("edge-1")?;
let (counter, counter_version) = kv.incr("counter", 1)?;
let cas = kv.compare_and_set("edge-1", version, b"offline", None)?;
let rows = kv.scan_prefix("edge-", 100)?;
```

## Bulk And Document API

```rust
let rows = connection.execute_bulk(
    "ignored,host=edge-2 usage=0.81 1710000002000",
    Some(&sonnetdb::BulkOptions {
        measurement: Some("cpu".into()),
        on_error: Some("failfast".into()),
        flush: Some("false".into()),
    }),
)?;

let documents = connection.open_document_collection("devices")?;
let created = documents.create_collection(Some("{\"ifNotExists\":true}"))?;
let inserted = documents.insert("{\"id\":\"dev-1\",\"document\":{\"site\":\"north\",\"score\":7}}")?;
let page = documents.find_page(Some("{\"limit\":10}"))?;
```
