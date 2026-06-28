# SonnetDB C Connector

The C connector publishes `SonnetDB.Native` as a native shared library through .NET Native AOT and exposes a small C ABI. Internally, the native layer uses `SonnetDB.Data`, so the same ABI can open both embedded databases and remote SonnetDB servers.

## ABI Scope

The initial ABI intentionally keeps only opaque handles and primitive values:

- open and close a connection from either a `SonnetDB.Data` connection string or a plain embedded database directory
- execute one SQL statement
- create and execute a bulk ingest handle for Line Protocol, JSON points, or Bulk VALUES payloads
- set bulk ingest options: `measurement`, `onerror`, and `flush`
- open a Document collection handle
- create/drop collections and run insert/update/delete/find-page/aggregate with UTF-8 JSON payloads
- open a KV keyspace/namespace handle
- get, set, delete, scan prefix, TTL, increment, and CAS KV entries
- open an Object Storage bucket handle
- create/list/delete buckets, put/head/get/list/delete objects, and run multipart upload basics
- stream object content through chunk writer/reader handles so large values do not cross the ABI as one buffer
- open an MQ topic handle
- publish messages, pull by consumer group, ack offsets, and read topic stats
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

The SQL ABI remains stable. Bulk ingest, Document, KV, Object Storage, and MQ access are exposed as their own function groups so existing SQL connectors remain stable.

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

## Document Collections

Document access uses an opaque `sonnetdb_doc` handle opened from an existing connection and a collection name. All requests and responses cross the ABI as UTF-8 JSON text; the C ABI does not expose SonnetDB document structs or internal engine pointers.

```c
sonnetdb_doc* docs = sonnetdb_doc_open(conn, "devices");
sonnetdb_doc_result* result = sonnetdb_doc_create_collection(docs, "{\"ifNotExists\":true}");

result = sonnetdb_doc_insert(
    docs,
    "{\"documents\":["
    "{\"id\":\"dev-1\",\"document\":{\"site\":\"north\",\"score\":7}},"
    "{\"id\":\"dev-2\",\"document\":{\"site\":\"south\",\"score\":3}}"
    "]}");

result = sonnetdb_doc_find_page(
    docs,
    "{\"limit\":10,\"filter\":{\"path\":\"$.site\",\"op\":\"eq\",\"value\":\"north\"}}");
```

Document result handles contain one compact JSON response. Use `sonnetdb_doc_result_json_length` to size a buffer and `sonnetdb_doc_result_copy_json` to copy a null-terminated UTF-8 string.

Payload shapes:

- `sonnetdb_doc_create_collection`: optional `{"ifNotExists":true,"validator":{...}}`, or `NULL` for defaults.
- `sonnetdb_doc_insert`: either `{"id":"dev-1","document":{...}}` or `{"documents":[{"id":"dev-1","document":{...}}],"ordered":true}`.
- `sonnetdb_doc_update`: replace one document with `{"id":"dev-1","document":{...}}`, replace many with `{"documents":[...]}`, or use update operators with `{"id":"dev-1","update":{"set":{"$.status":"ok"},"inc":{"$.score":1}}}`. Set `"multi":true` to apply an operator update to every matching document.
- `sonnetdb_doc_delete`: either `{"id":"dev-1"}` or `{"ids":["dev-1","dev-2"],"ordered":true}`.
- `sonnetdb_doc_find_page`: accepts the same JSON shape as `SndbDocumentFindOptions`: `id`, `ids`, `limit`, `skip`, `filter`, `projection`, `sort`, and `continuationToken`.
- `sonnetdb_doc_aggregate`: accepts either a pipeline array or `{"pipeline":[...]}`. Stage property names match the server API, such as `$match`, `$project`, `$group`, `$sort`, `$limit`, `$skip`, `$unwind`, `$count`, and `$distinct`.

Return conventions:

- functions returning `sonnetdb_doc_result*` return `NULL` on error and set `last_error`.
- `sonnetdb_doc_drop_collection` returns `1` when dropped, `0` when missing, and `-1` on error.
- result JSON strings are owned by `sonnetdb_doc_result` and should be copied before `sonnetdb_doc_result_free`.
- `sonnetdb_doc_find_page` returns `documents`, `count`, `continuationToken`, `hasMore`, `batchSize`, `snapshotVersion`, and `cursorExpiresAtUtc`.
- write operations return `collection`, `inserted`, `matched`, `modified`, `deleted`, and optional per-item `errors`.

## Object Storage Buckets

Object Storage access uses an opaque `sonnetdb_obj` bucket handle opened from an existing connection. Metadata-style operations return compact JSON through `sonnetdb_obj_result_*`, while object content flows through `sonnetdb_obj_writer` and `sonnetdb_obj_reader` chunk handles.

```c
sonnetdb_obj* objects = sonnetdb_obj_open(conn, "artifacts");
sonnetdb_obj_result* created = sonnetdb_obj_create_bucket(objects, "artifact");

sonnetdb_obj_writer* writer = sonnetdb_obj_writer_create(
    "text/plain",
    "{\"source\":\"c\"}",
    "{\"kind\":\"demo\"}");
sonnetdb_obj_writer_write(writer, "hello ", 6);
sonnetdb_obj_writer_write(writer, "object", 6);
sonnetdb_obj_result* put = sonnetdb_obj_put(objects, "logs/hello.txt", writer);
sonnetdb_obj_writer_free(writer);

sonnetdb_obj_reader* reader = sonnetdb_obj_get(objects, "logs/hello.txt", 6, 6);
char buffer[32];
int32_t read = sonnetdb_obj_reader_read(reader, buffer, sizeof(buffer));
sonnetdb_obj_reader_free(reader);
```

Object result JSON shapes:

- `sonnetdb_obj_list_buckets`: `{"buckets":[...],"count":N}`
- `sonnetdb_obj_create_bucket`: `{"bucket":{...}}`
- `sonnetdb_obj_put` / `sonnetdb_obj_head` / multipart complete: `{"object":{...}}`
- `sonnetdb_obj_list`: includes `bucket`, `prefix`, `maxKeys`, `continuationToken`, `nextContinuationToken`, `isTruncated`, `objects`, and `count`
- `sonnetdb_obj_delete`: `{"bucket":"...","key":"...","status":"deleted"}`
- `sonnetdb_obj_delete_many`: `{"bucket":"...","deleted":[...]}`
- multipart initiate: includes `bucket`, `key`, `uploadId`, `contentType`, `initiatedUtc`, `expiresUtc`, `metadata`, and `tags`

Return conventions:

- functions returning `sonnetdb_obj_result*`, `sonnetdb_obj_reader*`, or `sonnetdb_obj_writer*` return `NULL` on error and set `last_error`.
- `sonnetdb_obj_get` and `sonnetdb_obj_head` also return `NULL` for a missing object; check `sonnetdb_last_error` to distinguish a miss from an error.
- `sonnetdb_obj_get(..., offset, length)` treats `offset=0,length=-1` as a full read. Non-negative `length` performs a range read.
- `sonnetdb_obj_reader_read` returns bytes read, `0` at EOF, and `-1` on error.
- JSON arguments such as `metadata_json`, `tags_json`, `keys_json`, and `part_numbers_json` are UTF-8 JSON objects or arrays.

## Message Queue Topics

MQ access uses an opaque `sonnetdb_mq` handle opened from an existing connection and a topic name. The native layer calls `SonnetDB.Data.Mq.SndbMqClient`, so embedded and remote connections share the same topic, consumer group, offset, and ack semantics.

```c
sonnetdb_mq* mq = sonnetdb_mq_open(conn, "events.demo");
const char* payload = "pump online";
int64_t offset = sonnetdb_mq_publish(
    mq,
    payload,
    11,
    "{\"source\":\"c\"}");

sonnetdb_mq_pull_result* pull = sonnetdb_mq_pull(mq, "workers", 10);
while (sonnetdb_mq_pull_next(pull) == 1) {
    char buffer[128];
    int64_t len = sonnetdb_mq_pull_payload_length(pull);
    sonnetdb_mq_pull_copy_payload(pull, buffer, sizeof(buffer));
    buffer[len] = '\0';
    printf("%lld %s\n", (long long)sonnetdb_mq_pull_offset(pull), buffer);
}
sonnetdb_mq_ack(mq, "workers", offset);
sonnetdb_mq_pull_result_free(pull);
sonnetdb_mq_close(mq);
```

Semantics:

- offsets are monotonically increasing per topic and start at `0`.
- `sonnetdb_mq_pull(queue, consumer_group, max_count)` reads from that group's next unacked offset; `max_count <= 0` uses the client default of `100`.
- `sonnetdb_mq_ack(queue, consumer_group, offset)` marks all messages through `offset` as processed and returns the group's next offset.
- headers are passed as optional UTF-8 JSON objects and are returned from pull messages as compact JSON.
- payloads are binary buffers; pull payload copy helpers return the full required byte length and truncate only when the caller buffer is smaller.
- `sonnetdb_mq_stats` returns JSON with `topic`, `messageCount`, `nextOffset`, and `consumerOffsets`.

Return conventions:

- `sonnetdb_mq_publish` and `sonnetdb_mq_ack` return an offset, or `-1` on error.
- functions returning `sonnetdb_mq*`, `sonnetdb_mq_pull_result*`, or `sonnetdb_mq_result*` return `NULL` on error and set `last_error`.
- strings returned by `sonnetdb_mq_pull_topic` are owned by the pull result handle and remain valid until `sonnetdb_mq_pull_result_free`.

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
- using Document collection create/insert/find/update/aggregate/delete through JSON payloads
- using KV set/get/ttl/incr/cas/scan/delete through a KV handle
- using Object Storage bucket/object put/range/list/delete and multipart upload through chunk handles
- using MQ publish/pull/ack/stats through a topic handle
- selecting rows through the result cursor
- closing all native handles
