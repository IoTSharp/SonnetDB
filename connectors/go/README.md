# SonnetDB Go Connector

The Go connector is a cgo wrapper over the stable SonnetDB C ABI from `connectors/c`.

It exposes:

- `sonnetdb.Open` / `Connection.Execute` / `Connection.ExecuteNonQuery`
- `Connection.ExecuteBulk` with measurement/onerror/flush options
- `Connection.OpenKV` with get/set/delete/scan/ttl/incr/CAS helpers
- `Connection.OpenDocumentCollection` with create/drop/insert/update/delete/find/aggregate helpers
- forward-only result cursors with typed getters
- `Connection.Flush`, `sonnetdb.Version`, and `sonnetdb.LastError`
- a `database/sql` driver registered as `sonnetdb`

The first version intentionally does not implement SQL parameters because the native ABI currently accepts a single SQL string.

## Requirements

- Go 1.22+
- cgo enabled and a platform C compiler available
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
cd connectors/go
$native = (Resolve-Path ../c/native/SonnetDB.Native/bin/Release/net10.0/win-x64/native).Path
$env:CGO_ENABLED = "1"
$env:CGO_LDFLAGS = "-L$native"
$env:PATH = "$native;$env:PATH"
go run ./examples/quickstart
```

## Run the Quickstart on Linux

```bash
cmake -S connectors/c --preset linux-x64
cmake --build artifacts/connectors/c/linux-x64

cd connectors/go
native="$(realpath ../../artifacts/connectors/c/linux-x64)"
CGO_ENABLED=1 CGO_LDFLAGS="-L$native" LD_LIBRARY_PATH="$native:${LD_LIBRARY_PATH:-}" \
  go run ./examples/quickstart
```

## API Sketch

```go
connection, err := sonnetdb.Open("./data-go")
if err != nil {
    return err
}
defer connection.Close()

_, err = connection.ExecuteNonQuery("CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)")
if err != nil {
    return err
}

result, err := connection.Execute("SELECT time, host, usage FROM cpu LIMIT 10")
if err != nil {
    return err
}
defer result.Close()

for {
    ok, err := result.Next()
    if err != nil || !ok {
        break
    }
    ts, _ := result.Int64(0)
    host, _, _ := result.Text(1)
    usage, _ := result.Double(2)
    fmt.Println(ts, host, usage)
}
```

## KV API

```go
kv, err := connection.OpenKV("app-cache", "devices")
if err != nil {
    return err
}
defer kv.Close()

version, err := kv.Set("edge-1", []byte("online"))
entry, err := kv.Get("edge-1")
counter, counterVersion, err := kv.Incr("counter", 1)
cas, err := kv.CAS("edge-1", version, []byte("offline"))
rows, err := kv.ScanPrefix("edge-", 100)
_, _, _, _ = entry, counter, counterVersion, cas
_ = rows
```

## Bulk And Document API

```go
rows, err := connection.ExecuteBulk(
    "ignored,host=edge-2 usage=0.81 1710000002000",
    sonnetdb.BulkOptions{Measurement: "cpu", OnError: "failfast", Flush: "false"})

documents, err := connection.OpenDocumentCollection("devices")
if err != nil {
    return err
}
defer documents.Close()

created, err := documents.CreateCollection(`{"ifNotExists":true}`)
inserted, err := documents.Insert(`{"id":"dev-1","document":{"site":"north","score":7}}`)
page, err := documents.FindPage(`{"limit":10}`)
_, _, _ = rows, created, inserted
_ = page
```

## database/sql

Import the package for its side-effect registration, then use the data directory as the DSN:

```go
import (
    "database/sql"

    _ "github.com/sonnetdb/sonnetdb/connectors/go"
)

db, err := sql.Open("sonnetdb", "./data-go")
```
