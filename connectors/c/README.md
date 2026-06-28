# SonnetDB C Connector

The C connector publishes `SonnetDB.Native` as a native shared library through .NET Native AOT and exposes a small C ABI. Internally, the native layer uses `SonnetDB.Data`, so the same SQL-only ABI can open both embedded databases and remote SonnetDB servers.

## ABI Scope

The initial ABI intentionally keeps only opaque handles and primitive values:

- open and close a connection from either a `SonnetDB.Data` connection string or a plain embedded database directory
- execute one SQL statement
- read result metadata and rows
- read typed values as `int64`, `double`, `bool`, or UTF-8 text
- fetch the last error for the current native thread
- trigger a flush for embedded connections

The ABI does not expose SonnetDB file format structs, C# objects, or internal engine pointers.

`sonnetdb_flush` remains an embedded durability helper. Remote writes should use SQL semantics or the future bulk/API-specific connector functions.

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

The current C ABI is still SQL-only. Bulk ingest, KV, Document, Object storage, and MQ APIs should be added as separate ABI groups so existing SQL connectors remain stable.

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
- selecting rows through the result cursor
- closing all native handles
