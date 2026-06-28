package com.sonnetdb;

import com.sonnetdb.internal.NativeBackend;
import com.sonnetdb.internal.NativeBackendLoader;

import java.util.Objects;

/**
 * SonnetDB connection facade.
 */
public final class SonnetDbConnection implements AutoCloseable {
    private static final NativeBackend Backend = NativeBackendLoader.load();

    private long handle;

    private SonnetDbConnection(long handle) {
        this.handle = handle;
    }

    /**
     * Opens a SonnetDB data source.
     */
    public static SonnetDbConnection open(String dataSource) {
        Objects.requireNonNull(dataSource, "dataSource");
        return new SonnetDbConnection(Backend.open(dataSource));
    }

    /**
     * Returns the loaded SonnetDB native library version.
     */
    public static String version() {
        return Backend.version();
    }

    /**
     * Executes one SQL statement.
     */
    public SonnetDbResult execute(String sql) {
        Objects.requireNonNull(sql, "sql");
        ensureOpen();
        return new SonnetDbResult(Backend, Backend.execute(handle, sql));
    }

    /**
     * Executes SQL and returns the affected row count.
     */
    public int executeNonQuery(String sql) {
        try (SonnetDbResult result = execute(sql)) {
            return result.recordsAffected();
        }
    }

    /**
     * Synchronously ingests a bulk payload and returns the affected row count.
     */
    public int executeBulk(String payload) {
        return executeBulk(payload, new SonnetDbBulkOptions());
    }

    /**
     * Synchronously ingests a bulk payload and returns the affected row count.
     */
    public int executeBulk(String payload, SonnetDbBulkOptions options) {
        Objects.requireNonNull(payload, "payload");
        Objects.requireNonNull(options, "options");
        ensureOpen();
        return Backend.bulkExecute(
            handle,
            payload,
            options.measurement(),
            options.onError(),
            options.flush());
    }

    /**
     * Opens a document collection handle.
     */
    public SonnetDbDocumentCollection openDocumentCollection(String collection) {
        Objects.requireNonNull(collection, "collection");
        ensureOpen();
        return new SonnetDbDocumentCollection(
            Backend,
            Backend.docOpen(handle, collection));
    }

    /**
     * Opens the root namespace for a KV keyspace.
     */
    public SonnetDbKeyValueStore openKeyValueStore(String keyspace) {
        return openKeyValueStore(keyspace, "");
    }

    /**
     * Opens a KV keyspace/namespace handle.
     */
    public SonnetDbKeyValueStore openKeyValueStore(String keyspace, String namespaceName) {
        Objects.requireNonNull(keyspace, "keyspace");
        Objects.requireNonNull(namespaceName, "namespaceName");
        ensureOpen();
        return new SonnetDbKeyValueStore(
            Backend,
            Backend.kvOpen(handle, keyspace, namespaceName));
    }

    /**
     * Forces pending data to durable storage.
     */
    public void flush() {
        ensureOpen();
        Backend.flush(handle);
    }

    /**
     * Closes the connection.
     */
    @Override
    public void close() {
        long current = handle;
        handle = 0L;
        if (current != 0L) {
            Backend.close(current);
        }
    }

    private void ensureOpen() {
        if (handle == 0L) {
            throw new SonnetDbException("SonnetDB connection is closed.");
        }
    }
}
