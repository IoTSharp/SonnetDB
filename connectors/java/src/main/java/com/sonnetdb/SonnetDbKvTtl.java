package com.sonnetdb;

/**
 * Redis-style SonnetDB KV TTL result.
 */
public final class SonnetDbKvTtl {
    private final long milliseconds;
    private final long expiresAtUnixMs;

    /**
     * Creates a TTL snapshot.
     */
    public SonnetDbKvTtl(long milliseconds, long expiresAtUnixMs) {
        this.milliseconds = milliseconds;
        this.expiresAtUnixMs = expiresAtUnixMs;
    }

    /**
     * Returns remaining milliseconds; -2 means missing key and -1 means no expiration.
     */
    public long milliseconds() {
        return milliseconds;
    }

    /**
     * Returns UTC expiration time in Unix milliseconds, or -1 when absent.
     */
    public long expiresAtUnixMs() {
        return expiresAtUnixMs;
    }
}
