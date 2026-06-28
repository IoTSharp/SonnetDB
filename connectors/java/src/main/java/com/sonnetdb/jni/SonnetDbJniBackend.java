package com.sonnetdb.jni;

import com.sonnetdb.SonnetDbException;
import com.sonnetdb.SonnetDbValueType;
import com.sonnetdb.internal.NativeBackend;

import java.io.File;

/**
 * Java 8 兼容的 JNI native 后端。
 */
public final class SonnetDbJniBackend implements NativeBackend {
    static {
        loadJniLibrary();
        SonnetDbJni.initialize(resolveNativeLibraryPath());
    }

    /**
     * 构造 JNI 后端。
     */
    public SonnetDbJniBackend() {
    }

    @Override
    public long open(String dataSource) {
        return SonnetDbJni.open(dataSource);
    }

    @Override
    public void close(long connection) {
        SonnetDbJni.close(connection);
    }

    @Override
    public long execute(long connection, String sql) {
        return SonnetDbJni.execute(connection, sql);
    }

    @Override
    public int bulkExecute(long connection, String payload, String measurement, String onError, String flush) {
        return SonnetDbJni.bulkExecute(connection, payload, measurement, onError, flush);
    }

    @Override
    public void resultFree(long result) {
        SonnetDbJni.resultFree(result);
    }

    @Override
    public int recordsAffected(long result) {
        return SonnetDbJni.recordsAffected(result);
    }

    @Override
    public int columnCount(long result) {
        return SonnetDbJni.columnCount(result);
    }

    @Override
    public String columnName(long result, int ordinal) {
        return SonnetDbJni.columnName(result, ordinal);
    }

    @Override
    public boolean next(long result) {
        return SonnetDbJni.next(result);
    }

    @Override
    public SonnetDbValueType valueType(long result, int ordinal) {
        return SonnetDbValueType.fromCode(SonnetDbJni.valueType(result, ordinal));
    }

    @Override
    public long valueInt64(long result, int ordinal) {
        return SonnetDbJni.valueInt64(result, ordinal);
    }

    @Override
    public double valueDouble(long result, int ordinal) {
        return SonnetDbJni.valueDouble(result, ordinal);
    }

    @Override
    public boolean valueBool(long result, int ordinal) {
        return SonnetDbJni.valueBool(result, ordinal);
    }

    @Override
    public String valueText(long result, int ordinal) {
        return SonnetDbJni.valueText(result, ordinal);
    }

    @Override
    public long kvOpen(long connection, String keyspace, String namespaceName) {
        return SonnetDbJni.kvOpen(connection, keyspace, namespaceName);
    }

    @Override
    public void kvClose(long kv) {
        SonnetDbJni.kvClose(kv);
    }

    @Override
    public long kvGet(long kv, String key) {
        return SonnetDbJni.kvGet(kv, key);
    }

    @Override
    public long kvSet(long kv, String key, byte[] value, long expiresAtUnixMs) {
        return SonnetDbJni.kvSet(kv, key, value, expiresAtUnixMs);
    }

    @Override
    public boolean kvDelete(long kv, String key) {
        return SonnetDbJni.kvDelete(kv, key);
    }

    @Override
    public long kvScanPrefix(long kv, String prefix, int limit) {
        return SonnetDbJni.kvScanPrefix(kv, prefix, limit);
    }

    @Override
    public long kvTtl(long kv, String key, long[] expiresAtUnixMs) {
        return SonnetDbJni.kvTtl(kv, key, expiresAtUnixMs);
    }

    @Override
    public boolean kvExpireAt(long kv, String key, long expiresAtUnixMs) {
        return SonnetDbJni.kvExpireAt(kv, key, expiresAtUnixMs);
    }

    @Override
    public boolean kvPersist(long kv, String key) {
        return SonnetDbJni.kvPersist(kv, key);
    }

    @Override
    public void kvIncr(long kv, String key, long delta, long[] valueAndVersion) {
        SonnetDbJni.kvIncr(kv, key, delta, valueAndVersion);
    }

    @Override
    public boolean kvCas(
        long kv,
        String key,
        long expectedVersion,
        byte[] value,
        long expiresAtUnixMs,
        long[] currentAndNewVersion) {
        return SonnetDbJni.kvCas(kv, key, expectedVersion, value, expiresAtUnixMs, currentAndNewVersion);
    }

    @Override
    public void kvEntryFree(long entry) {
        SonnetDbJni.kvEntryFree(entry);
    }

    @Override
    public String kvEntryKey(long entry) {
        return SonnetDbJni.kvEntryKey(entry);
    }

    @Override
    public byte[] kvEntryValue(long entry) {
        return SonnetDbJni.kvEntryValue(entry);
    }

    @Override
    public long kvEntryVersion(long entry) {
        return SonnetDbJni.kvEntryVersion(entry);
    }

    @Override
    public long kvEntryExpiresAtUnixMs(long entry) {
        return SonnetDbJni.kvEntryExpiresAtUnixMs(entry);
    }

    @Override
    public boolean kvScanNext(long scan) {
        return SonnetDbJni.kvScanNext(scan);
    }

    @Override
    public String kvScanKey(long scan) {
        return SonnetDbJni.kvScanKey(scan);
    }

    @Override
    public byte[] kvScanValue(long scan) {
        return SonnetDbJni.kvScanValue(scan);
    }

    @Override
    public long kvScanVersion(long scan) {
        return SonnetDbJni.kvScanVersion(scan);
    }

    @Override
    public long kvScanExpiresAtUnixMs(long scan) {
        return SonnetDbJni.kvScanExpiresAtUnixMs(scan);
    }

    @Override
    public void kvScanFree(long scan) {
        SonnetDbJni.kvScanFree(scan);
    }

    @Override
    public long docOpen(long connection, String collection) {
        return SonnetDbJni.docOpen(connection, collection);
    }

    @Override
    public void docClose(long document) {
        SonnetDbJni.docClose(document);
    }

    @Override
    public String docCreateCollection(long document, String optionsJson) {
        return SonnetDbJni.docCreateCollection(document, optionsJson);
    }

    @Override
    public boolean docDropCollection(long document) {
        return SonnetDbJni.docDropCollection(document);
    }

    @Override
    public String docInsert(long document, String payloadJson) {
        return SonnetDbJni.docInsert(document, payloadJson);
    }

    @Override
    public String docUpdate(long document, String payloadJson) {
        return SonnetDbJni.docUpdate(document, payloadJson);
    }

    @Override
    public String docDelete(long document, String payloadJson) {
        return SonnetDbJni.docDelete(document, payloadJson);
    }

    @Override
    public String docFindPage(long document, String payloadJson) {
        return SonnetDbJni.docFindPage(document, optionsOrEmpty(payloadJson));
    }

    @Override
    public String docAggregate(long document, String payloadJson) {
        return SonnetDbJni.docAggregate(document, payloadJson);
    }

    @Override
    public void flush(long connection) {
        SonnetDbJni.flush(connection);
    }

    @Override
    public String version() {
        return SonnetDbJni.version();
    }

    @Override
    public String lastError() {
        return SonnetDbJni.lastError();
    }

    private static String resolveNativeLibraryPath() {
        String path = System.getProperty("sonnetdb.native.path");
        if (path == null || path.trim().isEmpty()) {
            path = System.getenv("SONNETDB_NATIVE_LIBRARY");
        }
        return path == null || path.trim().isEmpty()
            ? null
            : new File(path).getAbsolutePath();
    }

    private static String optionsOrEmpty(String value) {
        return value == null ? "" : value;
    }

    private static void loadJniLibrary() {
        String path = System.getProperty("sonnetdb.jni.path");
        if (path == null || path.trim().isEmpty()) {
            path = System.getenv("SONNETDB_JNI_LIBRARY");
        }

        try {
            if (path != null && !path.trim().isEmpty()) {
                System.load(new File(path).getAbsolutePath());
            } else {
                System.loadLibrary("SonnetDB.Java.Native");
            }
        } catch (UnsatisfiedLinkError ex) {
            throw new SonnetDbException(
                "Cannot load SonnetDB JNI bridge. Set -Dsonnetdb.jni.path or SONNETDB_JNI_LIBRARY.",
                ex);
        }
    }
}
