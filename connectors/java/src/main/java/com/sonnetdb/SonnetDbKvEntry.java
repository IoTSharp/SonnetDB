package com.sonnetdb;

import java.util.Arrays;

/**
 * Materialized SonnetDB KV entry.
 */
public final class SonnetDbKvEntry {
    private final String key;
    private final byte[] value;
    private final long version;
    private final long expiresAtUnixMs;

    /**
     * Creates a KV entry snapshot.
     */
    public SonnetDbKvEntry(String key, byte[] value, long version, long expiresAtUnixMs) {
        this.key = key;
        this.value = Arrays.copyOf(value, value.length);
        this.version = version;
        this.expiresAtUnixMs = expiresAtUnixMs;
    }

    /**
     * Returns the entry key without namespace prefix.
     */
    public String key() {
        return key;
    }

    /**
     * Returns a copy of the binary value.
     */
    public byte[] value() {
        return Arrays.copyOf(value, value.length);
    }

    /**
     * Returns the monotonic write version.
     */
    public long version() {
        return version;
    }

    /**
     * Returns UTC expiration time in Unix milliseconds, or -1 when absent.
     */
    public long expiresAtUnixMs() {
        return expiresAtUnixMs;
    }
}
