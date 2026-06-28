package com.sonnetdb;

import com.sonnetdb.internal.NativeBackend;

import java.util.Objects;

/**
 * SonnetDB document collection handle.
 */
public final class SonnetDbDocumentCollection implements AutoCloseable {
    private final NativeBackend backend;
    private long handle;

    SonnetDbDocumentCollection(NativeBackend backend, long handle) {
        this.backend = backend;
        this.handle = handle;
    }

    /**
     * Creates the collection with default options.
     */
    public String createCollection() {
        return createCollection("");
    }

    /**
     * Creates the collection and returns the native JSON response.
     */
    public String createCollection(String optionsJson) {
        ensureOpen();
        return backend.docCreateCollection(handle, normalizeOptional(optionsJson));
    }

    /**
     * Drops the collection and returns whether it existed.
     */
    public boolean dropCollection() {
        ensureOpen();
        return backend.docDropCollection(handle);
    }

    /**
     * Inserts documents from a JSON request and returns the native JSON response.
     */
    public String insert(String payloadJson) {
        Objects.requireNonNull(payloadJson, "payloadJson");
        ensureOpen();
        return backend.docInsert(handle, payloadJson);
    }

    /**
     * Updates documents from a JSON request and returns the native JSON response.
     */
    public String update(String payloadJson) {
        Objects.requireNonNull(payloadJson, "payloadJson");
        ensureOpen();
        return backend.docUpdate(handle, payloadJson);
    }

    /**
     * Deletes documents from a JSON request and returns the native JSON response.
     */
    public String delete(String payloadJson) {
        Objects.requireNonNull(payloadJson, "payloadJson");
        ensureOpen();
        return backend.docDelete(handle, payloadJson);
    }

    /**
     * Finds a page using default options.
     */
    public String findPage() {
        return findPage("");
    }

    /**
     * Finds a page of documents and returns the native JSON response.
     */
    public String findPage(String payloadJson) {
        ensureOpen();
        return backend.docFindPage(handle, normalizeOptional(payloadJson));
    }

    /**
     * Runs an aggregation pipeline and returns the native JSON response.
     */
    public String aggregate(String payloadJson) {
        Objects.requireNonNull(payloadJson, "payloadJson");
        ensureOpen();
        return backend.docAggregate(handle, payloadJson);
    }

    /**
     * Closes the document collection handle.
     */
    @Override
    public void close() {
        long current = handle;
        handle = 0L;
        if (current != 0L) {
            backend.docClose(current);
        }
    }

    private void ensureOpen() {
        if (handle == 0L) {
            throw new SonnetDbException("SonnetDB document collection is closed.");
        }
    }

    private static String normalizeOptional(String value) {
        return value == null ? "" : value;
    }
}
