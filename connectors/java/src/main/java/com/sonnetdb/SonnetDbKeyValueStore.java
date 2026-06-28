package com.sonnetdb;

import com.sonnetdb.internal.NativeBackend;

import java.util.ArrayList;
import java.util.List;
import java.util.Objects;

/**
 * SonnetDB KV keyspace/namespace handle.
 */
public final class SonnetDbKeyValueStore implements AutoCloseable {
    private final NativeBackend backend;
    private long handle;

    SonnetDbKeyValueStore(NativeBackend backend, long handle) {
        this.backend = backend;
        this.handle = handle;
    }

    /**
     * Reads a KV entry, or returns null when the key is missing.
     */
    public SonnetDbKvEntry get(String key) {
        Objects.requireNonNull(key, "key");
        ensureOpen();
        long entry = backend.kvGet(handle, key);
        if (entry == 0L) {
            return null;
        }

        try {
            return materializeEntry(entry);
        } finally {
            backend.kvEntryFree(entry);
        }
    }

    /**
     * Writes a binary value without expiration and returns the written version.
     */
    public long set(String key, byte[] value) {
        return set(key, value, -1L);
    }

    /**
     * Writes a binary value and returns the written version.
     */
    public long set(String key, byte[] value, long expiresAtUnixMs) {
        Objects.requireNonNull(key, "key");
        Objects.requireNonNull(value, "value");
        ensureOpen();
        return backend.kvSet(handle, key, value, expiresAtUnixMs);
    }

    /**
     * Deletes a key and returns whether a value was removed.
     */
    public boolean delete(String key) {
        Objects.requireNonNull(key, "key");
        ensureOpen();
        return backend.kvDelete(handle, key);
    }

    /**
     * Scans entries matching a prefix. limit <= 0 uses the keyspace default.
     */
    public List<SonnetDbKvEntry> scanPrefix(String prefix, int limit) {
        Objects.requireNonNull(prefix, "prefix");
        ensureOpen();
        long scan = backend.kvScanPrefix(handle, prefix, limit);
        try {
            List<SonnetDbKvEntry> entries = new ArrayList<>();
            while (backend.kvScanNext(scan)) {
                entries.add(new SonnetDbKvEntry(
                    backend.kvScanKey(scan),
                    backend.kvScanValue(scan),
                    backend.kvScanVersion(scan),
                    backend.kvScanExpiresAtUnixMs(scan)));
            }
            return entries;
        } finally {
            backend.kvScanFree(scan);
        }
    }

    /**
     * Returns the remaining TTL in milliseconds.
     */
    public SonnetDbKvTtl ttl(String key) {
        Objects.requireNonNull(key, "key");
        ensureOpen();
        long[] expires = new long[] { -1L };
        long milliseconds = backend.kvTtl(handle, key, expires);
        return new SonnetDbKvTtl(milliseconds, expires[0]);
    }

    /**
     * Sets an absolute UTC expiration time in Unix milliseconds.
     */
    public boolean expireAt(String key, long expiresAtUnixMs) {
        Objects.requireNonNull(key, "key");
        ensureOpen();
        return backend.kvExpireAt(handle, key, expiresAtUnixMs);
    }

    /**
     * Removes a key expiration.
     */
    public boolean persist(String key) {
        Objects.requireNonNull(key, "key");
        ensureOpen();
        return backend.kvPersist(handle, key);
    }

    /**
     * Atomically increments a UTF-8 integer value and returns value/version.
     */
    public long[] increment(String key, long delta) {
        Objects.requireNonNull(key, "key");
        ensureOpen();
        long[] result = new long[] { 0L, 0L };
        backend.kvIncr(handle, key, delta, result);
        return result;
    }

    /**
     * Compares a key version and swaps in a new value on match.
     */
    public SonnetDbKvCasResult compareAndSet(String key, long expectedVersion, byte[] value) {
        return compareAndSet(key, expectedVersion, value, -1L);
    }

    /**
     * Compares a key version and swaps in a new value on match.
     */
    public SonnetDbKvCasResult compareAndSet(
        String key,
        long expectedVersion,
        byte[] value,
        long expiresAtUnixMs) {
        Objects.requireNonNull(key, "key");
        Objects.requireNonNull(value, "value");
        ensureOpen();
        long[] versions = new long[] { 0L, -1L };
        boolean swapped = backend.kvCas(handle, key, expectedVersion, value, expiresAtUnixMs, versions);
        return new SonnetDbKvCasResult(swapped, versions[0], versions[1]);
    }

    /**
     * Closes the KV handle.
     */
    @Override
    public void close() {
        long current = handle;
        handle = 0L;
        if (current != 0L) {
            backend.kvClose(current);
        }
    }

    private SonnetDbKvEntry materializeEntry(long entry) {
        return new SonnetDbKvEntry(
            backend.kvEntryKey(entry),
            backend.kvEntryValue(entry),
            backend.kvEntryVersion(entry),
            backend.kvEntryExpiresAtUnixMs(entry));
    }

    private void ensureOpen() {
        if (handle == 0L) {
            throw new SonnetDbException("SonnetDB KV handle is closed.");
        }
    }
}
