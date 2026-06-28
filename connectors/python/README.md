# SonnetDB Python Connector

The Python connector is a dependency-free `ctypes` wrapper over the stable SonnetDB C ABI from `connectors/c`.

It exposes:

- `sonnetdb.connect` / `Connection.execute` / `Connection.execute_non_query`
- `Connection.execute_bulk()` with measurement/on_error/flush options
- `Connection.open_kv()` with get/set/delete/scan/ttl/incr/CAS helpers
- `Connection.open_document_collection()` with create/drop/insert/update/delete/find/aggregate helpers
- forward-only result cursors with typed getters and tuple iteration
- `Connection.flush`, `sonnetdb.version`, and `sonnetdb.last_error`
- a light DB-API-style `Connection.cursor()` facade

`Connection.close()` releases the native handle and does not report best-effort shutdown diagnostics. Call `Connection.flush()` when you need an explicit durability check before closing.

The first version intentionally does not implement SQL parameters because the native ABI currently accepts a single SQL string.

## Requirements

- Python 3.10+
- a native SonnetDB C library built for the current platform:
  - Windows: `SonnetDB.Native.dll`
  - Linux: `SonnetDB.Native.so`

Build the C connector first:

```powershell
cmake -S connectors/c --preset windows-x64
cmake --build artifacts/connectors/c/win-x64 --config Release
```

## Run the Quickstart on Windows

```powershell
cd connectors/python
$native = (Resolve-Path ../c/native/SonnetDB.Native/bin/Release/net10.0/win-x64/native).Path
$env:SONNETDB_NATIVE_LIB_DIR = $native
python examples/quickstart.py
```

## Run the Quickstart on Linux

```bash
cmake -S connectors/c --preset linux-x64
cmake --build artifacts/connectors/c/linux-x64

cd connectors/python
native="$(realpath ../../artifacts/connectors/c/linux-x64)"
SONNETDB_NATIVE_LIB_DIR="$native" LD_LIBRARY_PATH="$native:${LD_LIBRARY_PATH:-}" \
  python examples/quickstart.py
```

## API Sketch

```python
import sonnetdb

with sonnetdb.connect("./data-python") as connection:
    connection.execute_non_query("CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)")
    connection.execute_non_query(
        "INSERT INTO cpu (time, host, usage) VALUES "
        "(1710000000000, 'edge-1', 0.42),"
        "(1710000001000, 'edge-1', 0.73)"
    )

    with connection.execute("SELECT time, host, usage FROM cpu LIMIT 10") as result:
        print(result.columns)
        for timestamp, host, usage in result:
            print(timestamp, host, usage)
```

## KV API

```python
with sonnetdb.connect("./data-python") as connection:
    with connection.open_kv("app-cache", "devices") as kv:
        version = kv.set("edge-1", b"online")
        entry = kv.get("edge-1")
        counter, counter_version = kv.incr("counter", 1)
        cas = kv.cas("edge-1", version, b"offline")
        rows = kv.scan_prefix("edge-", 100)
```

## Bulk And Document API

```python
with sonnetdb.connect("./data-python") as connection:
    rows = connection.execute_bulk(
        "ignored,host=edge-2 usage=0.81 1710000002000",
        measurement="cpu",
        on_error="failfast",
        flush="false",
    )

    with connection.open_document_collection("devices") as documents:
        created = documents.create_collection('{"ifNotExists":true}')
        inserted = documents.insert(
            '{"id":"dev-1","document":{"site":"north","score":7}}'
        )
        page = documents.find_page('{"limit":10}')
```

## DB-API-Style Cursor

```python
with sonnetdb.connect("./data-python") as connection:
    with connection.cursor() as cursor:
        cursor.execute("SELECT time, host, usage FROM cpu")
        rows = cursor.fetchall()
```

The cursor facade is intentionally small. Non-empty `parameters` passed to `cursor.execute(sql, parameters)` raise `NotSupportedError` until the native ABI gains parameter binding.

## Native Library Resolution

The connector looks for the native library in this order:

1. `SONNETDB_NATIVE_LIBRARY` as a full path, or a directory containing the library.
2. `SONNETDB_NATIVE_LIB_DIR`.
3. Common local build outputs under `artifacts/connectors/c` and `connectors/c/native/SonnetDB.Native/bin`.

Set one of the environment variables when running from outside the SonnetDB repository.

## Tests

```powershell
cd connectors/python
$env:SONNETDB_NATIVE_LIB_DIR = (Resolve-Path ../c/native/SonnetDB.Native/bin/Release/net10.0/win-x64/native).Path
python -m unittest discover -s tests
```
