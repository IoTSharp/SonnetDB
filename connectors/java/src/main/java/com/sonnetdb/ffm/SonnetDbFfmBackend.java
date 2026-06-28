package com.sonnetdb.ffm;

import com.sonnetdb.SonnetDbException;
import com.sonnetdb.SonnetDbValueType;
import com.sonnetdb.internal.NativeBackend;

/**
 * Java 8 基础 jar 中的 FFM 后端占位实现。
 *
 * <p>真正的 FFM 实现在 multi-release jar 的 Java 21 版本目录中。</p>
 */
public final class SonnetDbFfmBackend implements NativeBackend {
    /**
     * 构造 FFM 占位后端。
     */
    public SonnetDbFfmBackend() {
        throw unsupported();
    }

    @Override
    public long open(String dataSource) {
        throw unsupported();
    }

    @Override
    public void close(long connection) {
        throw unsupported();
    }

    @Override
    public long execute(long connection, String sql) {
        throw unsupported();
    }

    @Override
    public int bulkExecute(long connection, String payload, String measurement, String onError, String flush) {
        throw unsupported();
    }

    @Override
    public void resultFree(long result) {
        throw unsupported();
    }

    @Override
    public int recordsAffected(long result) {
        throw unsupported();
    }

    @Override
    public int columnCount(long result) {
        throw unsupported();
    }

    @Override
    public String columnName(long result, int ordinal) {
        throw unsupported();
    }

    @Override
    public boolean next(long result) {
        throw unsupported();
    }

    @Override
    public SonnetDbValueType valueType(long result, int ordinal) {
        throw unsupported();
    }

    @Override
    public long valueInt64(long result, int ordinal) {
        throw unsupported();
    }

    @Override
    public double valueDouble(long result, int ordinal) {
        throw unsupported();
    }

    @Override
    public boolean valueBool(long result, int ordinal) {
        throw unsupported();
    }

    @Override
    public String valueText(long result, int ordinal) {
        throw unsupported();
    }

    @Override
    public long kvOpen(long connection, String keyspace, String namespaceName) {
        throw unsupported();
    }

    @Override
    public void kvClose(long kv) {
        throw unsupported();
    }

    @Override
    public long kvGet(long kv, String key) {
        throw unsupported();
    }

    @Override
    public long kvSet(long kv, String key, byte[] value, long expiresAtUnixMs) {
        throw unsupported();
    }

    @Override
    public boolean kvDelete(long kv, String key) {
        throw unsupported();
    }

    @Override
    public long kvScanPrefix(long kv, String prefix, int limit) {
        throw unsupported();
    }

    @Override
    public long kvTtl(long kv, String key, long[] expiresAtUnixMs) {
        throw unsupported();
    }

    @Override
    public boolean kvExpireAt(long kv, String key, long expiresAtUnixMs) {
        throw unsupported();
    }

    @Override
    public boolean kvPersist(long kv, String key) {
        throw unsupported();
    }

    @Override
    public void kvIncr(long kv, String key, long delta, long[] valueAndVersion) {
        throw unsupported();
    }

    @Override
    public boolean kvCas(
        long kv,
        String key,
        long expectedVersion,
        byte[] value,
        long expiresAtUnixMs,
        long[] currentAndNewVersion) {
        throw unsupported();
    }

    @Override
    public void kvEntryFree(long entry) {
        throw unsupported();
    }

    @Override
    public String kvEntryKey(long entry) {
        throw unsupported();
    }

    @Override
    public byte[] kvEntryValue(long entry) {
        throw unsupported();
    }

    @Override
    public long kvEntryVersion(long entry) {
        throw unsupported();
    }

    @Override
    public long kvEntryExpiresAtUnixMs(long entry) {
        throw unsupported();
    }

    @Override
    public boolean kvScanNext(long scan) {
        throw unsupported();
    }

    @Override
    public String kvScanKey(long scan) {
        throw unsupported();
    }

    @Override
    public byte[] kvScanValue(long scan) {
        throw unsupported();
    }

    @Override
    public long kvScanVersion(long scan) {
        throw unsupported();
    }

    @Override
    public long kvScanExpiresAtUnixMs(long scan) {
        throw unsupported();
    }

    @Override
    public void kvScanFree(long scan) {
        throw unsupported();
    }

    @Override
    public long docOpen(long connection, String collection) {
        throw unsupported();
    }

    @Override
    public void docClose(long document) {
        throw unsupported();
    }

    @Override
    public String docCreateCollection(long document, String optionsJson) {
        throw unsupported();
    }

    @Override
    public boolean docDropCollection(long document) {
        throw unsupported();
    }

    @Override
    public String docInsert(long document, String payloadJson) {
        throw unsupported();
    }

    @Override
    public String docUpdate(long document, String payloadJson) {
        throw unsupported();
    }

    @Override
    public String docDelete(long document, String payloadJson) {
        throw unsupported();
    }

    @Override
    public String docFindPage(long document, String payloadJson) {
        throw unsupported();
    }

    @Override
    public String docAggregate(long document, String payloadJson) {
        throw unsupported();
    }

    @Override
    public void flush(long connection) {
        throw unsupported();
    }

    @Override
    public String version() {
        throw unsupported();
    }

    @Override
    public String lastError() {
        throw unsupported();
    }

    private static SonnetDbException unsupported() {
        return new SonnetDbException("SonnetDB FFM backend requires JDK 21+ and a multi-release jar build.");
    }
}
