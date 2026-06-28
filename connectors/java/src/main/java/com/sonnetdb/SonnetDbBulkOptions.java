package com.sonnetdb;

/**
 * Options for one SonnetDB bulk ingest operation.
 */
public final class SonnetDbBulkOptions {
    private final String measurement;
    private final String onError;
    private final String flush;

    /**
     * Creates empty bulk options.
     */
    public SonnetDbBulkOptions() {
        this("", "", "");
    }

    /**
     * Creates bulk options.
     */
    public SonnetDbBulkOptions(String measurement, String onError, String flush) {
        this.measurement = measurement == null ? "" : measurement;
        this.onError = onError == null ? "" : onError;
        this.flush = flush == null ? "" : flush;
    }

    /**
     * Returns the measurement override.
     */
    public String measurement() {
        return measurement;
    }

    /**
     * Returns the error handling mode.
     */
    public String onError() {
        return onError;
    }

    /**
     * Returns the flush mode.
     */
    public String flush() {
        return flush;
    }
}
