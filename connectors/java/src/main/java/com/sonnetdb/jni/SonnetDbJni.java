package com.sonnetdb.jni;

/**
 * JNI native 方法声明。
 */
final class SonnetDbJni {
    private SonnetDbJni() {
    }

    static native void initialize(String nativeLibraryPath);

    static native long open(String dataSource);

    static native void close(long connection);

    static native long execute(long connection, String sql);

    static native int bulkExecute(
        long connection,
        String payload,
        String measurement,
        String onError,
        String flush);

    static native void resultFree(long result);

    static native int recordsAffected(long result);

    static native int columnCount(long result);

    static native String columnName(long result, int ordinal);

    static native boolean next(long result);

    static native int valueType(long result, int ordinal);

    static native long valueInt64(long result, int ordinal);

    static native double valueDouble(long result, int ordinal);

    static native boolean valueBool(long result, int ordinal);

    static native String valueText(long result, int ordinal);

    static native long kvOpen(long connection, String keyspace, String namespaceName);

    static native void kvClose(long kv);

    static native long kvGet(long kv, String key);

    static native long kvSet(long kv, String key, byte[] value, long expiresAtUnixMs);

    static native boolean kvDelete(long kv, String key);

    static native long kvScanPrefix(long kv, String prefix, int limit);

    static native long kvTtl(long kv, String key, long[] expiresAtUnixMs);

    static native boolean kvExpireAt(long kv, String key, long expiresAtUnixMs);

    static native boolean kvPersist(long kv, String key);

    static native void kvIncr(long kv, String key, long delta, long[] valueAndVersion);

    static native boolean kvCas(
        long kv,
        String key,
        long expectedVersion,
        byte[] value,
        long expiresAtUnixMs,
        long[] currentAndNewVersion);

    static native void kvEntryFree(long entry);

    static native String kvEntryKey(long entry);

    static native byte[] kvEntryValue(long entry);

    static native long kvEntryVersion(long entry);

    static native long kvEntryExpiresAtUnixMs(long entry);

    static native boolean kvScanNext(long scan);

    static native String kvScanKey(long scan);

    static native byte[] kvScanValue(long scan);

    static native long kvScanVersion(long scan);

    static native long kvScanExpiresAtUnixMs(long scan);

    static native void kvScanFree(long scan);

    static native long docOpen(long connection, String collection);

    static native void docClose(long document);

    static native String docCreateCollection(long document, String optionsJson);

    static native boolean docDropCollection(long document);

    static native String docInsert(long document, String payloadJson);

    static native String docUpdate(long document, String payloadJson);

    static native String docDelete(long document, String payloadJson);

    static native String docFindPage(long document, String payloadJson);

    static native String docAggregate(long document, String payloadJson);

    static native void flush(long connection);

    static native String version();

    static native String lastError();
}
