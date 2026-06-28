# SonnetDB C Connector

The C connector publishes `SonnetDB.Native` as a native shared library through .NET Native AOT and exposes a small C ABI. Internally, the native layer uses `SonnetDB.Data`, so the same ABI can open both embedded databases and remote SonnetDB servers.

## ABI Scope

The initial ABI intentionally keeps only opaque handles and primitive values:

- open and close a connection from either a `SonnetDB.Data` connection string or a plain embedded database directory
- execute one SQL statement
- create and execute a bulk ingest handle for Line Protocol, JSON points, or Bulk VALUES payloads
- set bulk ingest options: `measurement`, `onerror`, and `flush`
- open a KV keyspace/namespace handle
- get, set, delete, scan prefix, TTL, increment, and CAS KV entries
- read result metadata and rows
- read typed values as `int64`, `double`, `bool`, or UTF-8 text
- fetch the last error for the current native thread
- trigger a flush for embedded connections

The ABI does not expose SonnetDB file format structs, C# objects, or internal engine pointers.

`sonnetdb_flush` remains an embedded durability helper. Remote bulk writes should use `sonnetdb_bulk_execute` with `sonnetdb_bulk_set_flush`.

## Connection Strings

For backward compatibility, a plain data directory still opens an embedded database:

```c
sonnetdb_connection* conn = sonnetdb_open("./data-c");
```

To be explicit, pass a `SonnetDB.Data` connection string:

```c
sonnetdb_connection* embedded = sonnetdb_open("Data Source=./data-c;Mode=Embedded");
sonnetdb_connection* remote = sonnetdb_open("Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=...;Mode=Remote");
```

The SQL ABI remains stable. Bulk ingest and KV access are exposed as their own function groups, and future Document, Object storage, and MQ APIs should follow the same pattern so existing SQL connectors remain stable.

## Bulk Ingest

Bulk ingest uses an opaque `sonnetdb_bulk` handle. The payload is passed as UTF-8 text and is dispatched through `SonnetDB.Data` `CommandType.TableDirect`, which means embedded and remote connections share the same ingest path.

```c
sonnetdb_bulk* bulk = sonnetdb_bulk_create(
    "ignored,host=edge-2 usage=0.81 1710000002000\n"
    "ignored,host=edge-2 usage=0.86 1710000003000");
sonnetdb_bulk_set_measurement(bulk, "cpu");
sonnetdb_bulk_set_onerror(bulk, "skip");
sonnetdb_bulk_set_flush(bulk, "false");
sonnetdb_result* result = sonnetdb_bulk_execute(conn, bulk);
printf("bulk rows: %d\n", sonnetdb_result_records_affected(result));
sonnetdb_result_free(result);
sonnetdb_bulk_free(bulk);
```

Supported payloads:

- Line Protocol
- JSON points
- Bulk VALUES: `INSERT INTO measurement(columns...) VALUES (...)`

Options:

- `measurement`: overrides the payload measurement or supplies the endpoint path for remote Line Protocol.
- `onerror`: use `skip` to skip malformed rows; any other value uses fail-fast behavior.
- `flush`: `false` / unset means no explicit flush, `async` signals background flush, `true` / `sync` performs a synchronous flush.

## KV Keyspace

KV access uses an opaque `sonnetdb_kv` handle opened from an existing connection. The keyspace name is required; the namespace pointer may be `NULL` or an empty string for the root namespace. Values are binary buffers, while keys and namespaces are UTF-8 strings.

```c
sonnetdb_kv* kv = sonnetdb_kv_open(conn, "app-cache", "quickstart");
const char* value = "online";
int64_t version = sonnetdb_kv_set(kv, "device:edge-1", value, 6, -1);

sonnetdb_kv_entry* entry = sonnetdb_kv_get(kv, "device:edge-1");
char buffer[64];
int32_t required = sonnetdb_kv_entry_copy_value(entry, buffer, sizeof(buffer));
printf("%s version=%lld bytes=%d\n",
       sonnetdb_kv_entry_key(entry),
       (long long)sonnetdb_kv_entry_version(entry),
       required);
sonnetdb_kv_entry_free(entry);

int64_t counter = 0;
int64_t counter_version = 0;
sonnetdb_kv_incr(kv, "counter", 1, &counter, &counter_version);
sonnetdb_kv_close(kv);
```

Return conventions:

- `sonnetdb_kv_set` returns the written version, or `-1` on error.
- `sonnetdb_kv_get` returns `NULL` for a missing key and leaves `last_error` empty; errors also return `NULL` but set `last_error`.
- `sonnetdb_kv_delete`, `sonnetdb_kv_expire_at`, `sonnetdb_kv_persist`, and `sonnetdb_kv_cas` return `1` for success, `0` for a normal no-op or CAS miss, and `-1` for errors.
- `sonnetdb_kv_scan_prefix` treats `limit <= 0` as the keyspace default scan limit.
- `sonnetdb_kv_ttl` returns Redis-style TTL milliseconds: `-2` for missing keys, `-1` for no expiration, and `-3` on error.
- Value copy helpers return the full required byte length. If the provided buffer is smaller, the value is truncated to the buffer length.
- Strings returned by `sonnetdb_kv_entry_key` and `sonnetdb_kv_scan_key` are owned by the entry/scan handle and remain valid until that handle is freed.

## Build With CMake

The CMake build publishes the .NET Native AOT library and then links the C quickstart against it.

```powershell
cmake --preset windows-x64
cmake --build --preset windows-x64
```

Supported presets:

- `windows-x64`
- `windows-x86`
- `windows-arm64` / `windows-xarm`
- `linux-x64`

`windows-xarm` is an alias for the .NET RID `win-arm64`. Building it requires the Visual Studio C++ ARM64 toolchain (`Hostx64/arm64`) to be installed.

For generators other than Visual Studio, configure the RID explicitly:

```powershell
cmake -S connectors/c -B artifacts/connectors/c/win-x64 -DSONNETDB_C_RID=win-x64
cmake --build artifacts/connectors/c/win-x64 --config Release
```

On WSL / Linux x64:

```bash
cmake -S connectors/c -B artifacts/connectors/c/linux-x64 -DSONNETDB_C_RID=linux-x64 -DCMAKE_BUILD_TYPE=Release
cmake --build artifacts/connectors/c/linux-x64
./artifacts/connectors/c/linux-x64/sonnetdb_quickstart
```

The build output contains:

- `sonnetdb_quickstart` / `sonnetdb_quickstart.exe`
- `SonnetDB.Native.dll` on Windows, or `SonnetDB.Native.so` on Linux
- the import library `SonnetDB.Native.lib` for Windows linkers

## C Example

`examples/quickstart.c` is built by default. Disable it with:

```powershell
cmake -S connectors/c -B artifacts/connectors/c/win-x64 -DSONNETDB_C_RID=win-x64 -DSONNETDB_C_BUILD_EXAMPLES=OFF
```

The example demonstrates:

- opening an embedded database directory
- creating a measurement
- inserting rows
- writing a Line Protocol payload through the bulk handle
- using KV set/get/ttl/incr/cas/scan/delete through a KV handle
- selecting rows through the result cursor
- closing all native handles
