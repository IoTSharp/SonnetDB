package com.sonnetdb;

/**
 * SonnetDB KV compare-and-set result.
 */
public final class SonnetDbKvCasResult {
    private final boolean swapped;
    private final long currentVersion;
    private final long newVersion;

    /**
     * Creates a CAS result snapshot.
     */
    public SonnetDbKvCasResult(boolean swapped, long currentVersion, long newVersion) {
        this.swapped = swapped;
        this.currentVersion = currentVersion;
        this.newVersion = newVersion;
    }

    /**
     * Returns whether the value was swapped.
     */
    public boolean swapped() {
        return swapped;
    }

    /**
     * Returns the version observed before the CAS decision.
     */
    public long currentVersion() {
        return currentVersion;
    }

    /**
     * Returns the new version after a successful swap, or -1 on mismatch.
     */
    public long newVersion() {
        return newVersion;
    }
}
