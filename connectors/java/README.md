# SonnetDB Java Connector

The Java connector is a dependency-free wrapper over the SonnetDB C ABI exposed by `connectors/c`.

It supports two native backends:

- `jni` (default): Java 8 compatible. Uses the small `SonnetDB.Java.Native` JNI bridge, which loads `SonnetDB.Native`.
- `ffm`: JDK 21+ optional backend. Uses the Foreign Function & Memory API from the multi-release jar.

Both backends expose SQL, bulk ingest, KV, and Document collection APIs synchronously over the same C ABI groups.

## Runtime Selection

Select the backend with:

```text
-Dsonnetdb.java.backend=jni|ffm|auto
```

Environment fallback:

```text
SONNETDB_JAVA_BACKEND=jni|ffm|auto
```

Native library paths:

```text
-Dsonnetdb.native.path=<SonnetDB.Native.dll|SonnetDB.Native.so>
-Dsonnetdb.jni.path=<SonnetDB.Java.Native.dll|libSonnetDB.Java.Native.so>
```

Environment fallbacks:

```text
SONNETDB_NATIVE_LIBRARY=<SonnetDB.Native.dll|SonnetDB.Native.so>
SONNETDB_JNI_LIBRARY=<SonnetDB.Java.Native.dll|libSonnetDB.Java.Native.so>
```

## Requirements

- Java 8+ to run the default JNI backend.
- JDK 21 to build and run the default dual-backend CMake configuration.
- JDK 21 FFM requires `--enable-preview` and `--enable-native-access=ALL-UNNAMED`.
- Build with `-DSONNETDB_JAVA_BUILD_FFM=OFF` for a Java 8-compatible JNI-only jar.
- A native SonnetDB C library built for the current platform:
  - Windows: `SonnetDB.Native.dll`
  - Linux: `SonnetDB.Native.so`

## Build

Build the C connector first:

```powershell
cmake -S connectors/c --preset windows-x64
cmake --build artifacts/connectors/c/win-x64 --config Release
```

Build the dual-backend Java connector:

```powershell
cmake -S connectors/java --preset windows-x64
cmake --build artifacts/connectors/java/windows-x64 --config Release
```

Build the Java 8-compatible JNI-only connector:

```powershell
cmake -S connectors/java --preset windows-x64-java8
cmake --build artifacts/connectors/java/windows-x64-java8 --config Release
```

Run the quickstart with JNI:

```powershell
cmake --build artifacts/connectors/java/windows-x64 --target run_sonnetdb_java_quickstart --config Release
```

Run the quickstart with FFM:

```powershell
cmake --build artifacts/connectors/java/windows-x64 --target run_sonnetdb_java_quickstart_ffm --config Release
```

On WSL / Linux x64:

```bash
sudo apt-get update
sudo apt-get install -y openjdk-21-jdk build-essential clang zlib1g-dev cmake

cmake -S connectors/c -B artifacts/connectors/c/linux-x64 -DSONNETDB_C_RID=linux-x64 -DCMAKE_BUILD_TYPE=Release
cmake --build artifacts/connectors/c/linux-x64

cmake -S connectors/java --preset linux-x64
cmake --build artifacts/connectors/java/linux-x64
cmake --build artifacts/connectors/java/linux-x64 --target run_sonnetdb_java_quickstart
cmake --build artifacts/connectors/java/linux-x64 --target run_sonnetdb_java_quickstart_ffm
```

## Manual Run

JNI backend:

```powershell
java `
  -Dsonnetdb.java.backend=jni `
  -Dsonnetdb.jni.path=artifacts/connectors/java/windows-x64/Release/SonnetDB.Java.Native.dll `
  -Dsonnetdb.native.path=artifacts/connectors/c/win-x64/Release/SonnetDB.Native.dll `
  -cp "artifacts/connectors/java/windows-x64/sonnetdb-java.jar;artifacts/connectors/java/windows-x64/example-classes" `
  com.sonnetdb.examples.Quickstart
```

FFM backend:

```powershell
java --enable-preview --enable-native-access=ALL-UNNAMED `
  -Dsonnetdb.java.backend=ffm `
  -Dsonnetdb.native.path=artifacts/connectors/c/win-x64/Release/SonnetDB.Native.dll `
  -cp "artifacts/connectors/java/windows-x64/sonnetdb-java.jar;artifacts/connectors/java/windows-x64/example-classes" `
  com.sonnetdb.examples.Quickstart
```

## API Sketch

```java
try (SonnetDbConnection connection = SonnetDbConnection.open("./data-java");
     SonnetDbResult result = connection.execute("SELECT time, host, usage FROM cpu LIMIT 10")) {
    while (result.next()) {
        long time = result.getLong(0);
        String host = result.getString(1);
        double usage = result.getDouble(2);
    }
}
```

## KV API

```java
try (SonnetDbConnection connection = SonnetDbConnection.open("./data-java");
     SonnetDbKeyValueStore kv = connection.openKeyValueStore("app-cache", "devices")) {
    long version = kv.set("edge-1", "online".getBytes(StandardCharsets.UTF_8));
    SonnetDbKvEntry entry = kv.get("edge-1");
    long[] counter = kv.increment("counter", 1);
    SonnetDbKvCasResult cas = kv.compareAndSet(
        "edge-1",
        version,
        "offline".getBytes(StandardCharsets.UTF_8));
}
```

## Bulk And Document API

```java
try (SonnetDbConnection connection = SonnetDbConnection.open("./data-java")) {
    int rows = connection.executeBulk(
        "ignored,host=edge-2 usage=0.81 1710000002000",
        new SonnetDbBulkOptions("cpu", "failfast", "false"));

    try (SonnetDbDocumentCollection documents = connection.openDocumentCollection("devices")) {
        String created = documents.createCollection("{\"ifNotExists\":true}");
        String inserted = documents.insert(
            "{\"id\":\"dev-1\",\"document\":{\"site\":\"north\",\"score\":7}}");
        String page = documents.findPage("{\"limit\":10}");
    }
}
```
