package com.sonnetdb.ffm;

import com.sonnetdb.SonnetDbException;
import com.sonnetdb.SonnetDbValueType;
import com.sonnetdb.internal.NativeBackend;

import java.lang.foreign.Arena;
import java.lang.foreign.FunctionDescriptor;
import java.lang.foreign.Linker;
import java.lang.foreign.MemorySegment;
import java.lang.foreign.SymbolLookup;
import java.lang.foreign.ValueLayout;
import java.lang.invoke.MethodHandle;
import java.nio.file.Path;

/**
 * JDK 21+ Foreign Function & Memory 后端。
 */
public final class SonnetDbFfmBackend implements NativeBackend {
    private static final Linker LINKER;

    private static final MethodHandle OPEN;
    private static final MethodHandle CLOSE;
    private static final MethodHandle EXECUTE;
    private static final MethodHandle BULK_CREATE;
    private static final MethodHandle BULK_SET_MEASUREMENT;
    private static final MethodHandle BULK_SET_ONERROR;
    private static final MethodHandle BULK_SET_FLUSH;
    private static final MethodHandle BULK_EXECUTE;
    private static final MethodHandle BULK_FREE;
    private static final MethodHandle RESULT_FREE;
    private static final MethodHandle RECORDS_AFFECTED;
    private static final MethodHandle COLUMN_COUNT;
    private static final MethodHandle COLUMN_NAME;
    private static final MethodHandle NEXT;
    private static final MethodHandle VALUE_TYPE;
    private static final MethodHandle VALUE_INT64;
    private static final MethodHandle VALUE_DOUBLE;
    private static final MethodHandle VALUE_BOOL;
    private static final MethodHandle VALUE_TEXT;
    private static final MethodHandle KV_OPEN;
    private static final MethodHandle KV_CLOSE;
    private static final MethodHandle KV_GET;
    private static final MethodHandle KV_SET;
    private static final MethodHandle KV_DELETE;
    private static final MethodHandle KV_SCAN_PREFIX;
    private static final MethodHandle KV_TTL;
    private static final MethodHandle KV_EXPIRE_AT;
    private static final MethodHandle KV_PERSIST;
    private static final MethodHandle KV_INCR;
    private static final MethodHandle KV_CAS;
    private static final MethodHandle KV_ENTRY_FREE;
    private static final MethodHandle KV_ENTRY_KEY;
    private static final MethodHandle KV_ENTRY_VALUE_LENGTH;
    private static final MethodHandle KV_ENTRY_COPY_VALUE;
    private static final MethodHandle KV_ENTRY_VERSION;
    private static final MethodHandle KV_ENTRY_EXPIRES_AT_UNIX_MS;
    private static final MethodHandle KV_SCAN_NEXT;
    private static final MethodHandle KV_SCAN_KEY;
    private static final MethodHandle KV_SCAN_VALUE_LENGTH;
    private static final MethodHandle KV_SCAN_COPY_VALUE;
    private static final MethodHandle KV_SCAN_VERSION;
    private static final MethodHandle KV_SCAN_EXPIRES_AT_UNIX_MS;
    private static final MethodHandle KV_SCAN_FREE;
    private static final MethodHandle DOC_OPEN;
    private static final MethodHandle DOC_CLOSE;
    private static final MethodHandle DOC_CREATE_COLLECTION;
    private static final MethodHandle DOC_DROP_COLLECTION;
    private static final MethodHandle DOC_INSERT;
    private static final MethodHandle DOC_UPDATE;
    private static final MethodHandle DOC_DELETE;
    private static final MethodHandle DOC_FIND_PAGE;
    private static final MethodHandle DOC_AGGREGATE;
    private static final MethodHandle DOC_RESULT_FREE;
    private static final MethodHandle DOC_RESULT_JSON_LENGTH;
    private static final MethodHandle DOC_RESULT_COPY_JSON;
    private static final MethodHandle FLUSH;
    private static final MethodHandle VERSION;
    private static final MethodHandle LAST_ERROR;

    static {
        loadNativeLibrary();
        LINKER = Linker.nativeLinker();
        OPEN = downcall("sonnetdb_open", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        CLOSE = downcall("sonnetdb_close", FunctionDescriptor.ofVoid(ValueLayout.ADDRESS));
        EXECUTE = downcall("sonnetdb_execute", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        BULK_CREATE = downcall("sonnetdb_bulk_create", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        BULK_SET_MEASUREMENT = downcall("sonnetdb_bulk_set_measurement", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        BULK_SET_ONERROR = downcall("sonnetdb_bulk_set_onerror", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        BULK_SET_FLUSH = downcall("sonnetdb_bulk_set_flush", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        BULK_EXECUTE = downcall("sonnetdb_bulk_execute", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        BULK_FREE = downcall("sonnetdb_bulk_free", FunctionDescriptor.ofVoid(ValueLayout.ADDRESS));
        RESULT_FREE = downcall("sonnetdb_result_free", FunctionDescriptor.ofVoid(ValueLayout.ADDRESS));
        RECORDS_AFFECTED = downcall("sonnetdb_result_records_affected", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS));
        COLUMN_COUNT = downcall("sonnetdb_result_column_count", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS));
        COLUMN_NAME = downcall("sonnetdb_result_column_name", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
        NEXT = downcall("sonnetdb_result_next", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS));
        VALUE_TYPE = downcall("sonnetdb_result_value_type", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
        VALUE_INT64 = downcall("sonnetdb_result_value_int64", FunctionDescriptor.of(ValueLayout.JAVA_LONG, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
        VALUE_DOUBLE = downcall("sonnetdb_result_value_double", FunctionDescriptor.of(ValueLayout.JAVA_DOUBLE, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
        VALUE_BOOL = downcall("sonnetdb_result_value_bool", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
        VALUE_TEXT = downcall("sonnetdb_result_value_text", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
        KV_OPEN = downcall("sonnetdb_kv_open", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        KV_CLOSE = downcall("sonnetdb_kv_close", FunctionDescriptor.ofVoid(ValueLayout.ADDRESS));
        KV_GET = downcall("sonnetdb_kv_get", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        KV_SET = downcall("sonnetdb_kv_set", FunctionDescriptor.of(ValueLayout.JAVA_LONG, ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.JAVA_INT, ValueLayout.JAVA_LONG));
        KV_DELETE = downcall("sonnetdb_kv_delete", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        KV_SCAN_PREFIX = downcall("sonnetdb_kv_scan_prefix", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
        KV_TTL = downcall("sonnetdb_kv_ttl", FunctionDescriptor.of(ValueLayout.JAVA_LONG, ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        KV_EXPIRE_AT = downcall("sonnetdb_kv_expire_at", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.JAVA_LONG));
        KV_PERSIST = downcall("sonnetdb_kv_persist", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        KV_INCR = downcall("sonnetdb_kv_incr", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.JAVA_LONG, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        KV_CAS = downcall("sonnetdb_kv_cas", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.JAVA_LONG, ValueLayout.ADDRESS, ValueLayout.JAVA_INT, ValueLayout.JAVA_LONG, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        KV_ENTRY_FREE = downcall("sonnetdb_kv_entry_free", FunctionDescriptor.ofVoid(ValueLayout.ADDRESS));
        KV_ENTRY_KEY = downcall("sonnetdb_kv_entry_key", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        KV_ENTRY_VALUE_LENGTH = downcall("sonnetdb_kv_entry_value_length", FunctionDescriptor.of(ValueLayout.JAVA_LONG, ValueLayout.ADDRESS));
        KV_ENTRY_COPY_VALUE = downcall("sonnetdb_kv_entry_copy_value", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
        KV_ENTRY_VERSION = downcall("sonnetdb_kv_entry_version", FunctionDescriptor.of(ValueLayout.JAVA_LONG, ValueLayout.ADDRESS));
        KV_ENTRY_EXPIRES_AT_UNIX_MS = downcall("sonnetdb_kv_entry_expires_at_unix_ms", FunctionDescriptor.of(ValueLayout.JAVA_LONG, ValueLayout.ADDRESS));
        KV_SCAN_NEXT = downcall("sonnetdb_kv_scan_next", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS));
        KV_SCAN_KEY = downcall("sonnetdb_kv_scan_key", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        KV_SCAN_VALUE_LENGTH = downcall("sonnetdb_kv_scan_value_length", FunctionDescriptor.of(ValueLayout.JAVA_LONG, ValueLayout.ADDRESS));
        KV_SCAN_COPY_VALUE = downcall("sonnetdb_kv_scan_copy_value", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
        KV_SCAN_VERSION = downcall("sonnetdb_kv_scan_version", FunctionDescriptor.of(ValueLayout.JAVA_LONG, ValueLayout.ADDRESS));
        KV_SCAN_EXPIRES_AT_UNIX_MS = downcall("sonnetdb_kv_scan_expires_at_unix_ms", FunctionDescriptor.of(ValueLayout.JAVA_LONG, ValueLayout.ADDRESS));
        KV_SCAN_FREE = downcall("sonnetdb_kv_scan_free", FunctionDescriptor.ofVoid(ValueLayout.ADDRESS));
        DOC_OPEN = downcall("sonnetdb_doc_open", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        DOC_CLOSE = downcall("sonnetdb_doc_close", FunctionDescriptor.ofVoid(ValueLayout.ADDRESS));
        DOC_CREATE_COLLECTION = downcall("sonnetdb_doc_create_collection", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        DOC_DROP_COLLECTION = downcall("sonnetdb_doc_drop_collection", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS));
        DOC_INSERT = downcall("sonnetdb_doc_insert", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        DOC_UPDATE = downcall("sonnetdb_doc_update", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        DOC_DELETE = downcall("sonnetdb_doc_delete", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        DOC_FIND_PAGE = downcall("sonnetdb_doc_find_page", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        DOC_AGGREGATE = downcall("sonnetdb_doc_aggregate", FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.ADDRESS));
        DOC_RESULT_FREE = downcall("sonnetdb_doc_result_free", FunctionDescriptor.ofVoid(ValueLayout.ADDRESS));
        DOC_RESULT_JSON_LENGTH = downcall("sonnetdb_doc_result_json_length", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS));
        DOC_RESULT_COPY_JSON = downcall("sonnetdb_doc_result_copy_json", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
        FLUSH = downcall("sonnetdb_flush", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS));
        VERSION = downcall("sonnetdb_version", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
        LAST_ERROR = downcall("sonnetdb_last_error", FunctionDescriptor.of(ValueLayout.JAVA_INT, ValueLayout.ADDRESS, ValueLayout.JAVA_INT));
    }

    /**
     * 构造 FFM 后端。
     */
    public SonnetDbFfmBackend() {
    }

    @Override
    public long open(String dataSource) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment dataSourceAddress = arena.allocateUtf8String(dataSource);
            MemorySegment connection = (MemorySegment) OPEN.invoke(dataSourceAddress);
            if (isNull(connection)) {
                throw failure("sonnetdb_open");
            }
            return connection.address();
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_open.", ex);
        }
    }

    @Override
    public void close(long connection) {
        if (connection == 0L) {
            return;
        }
        try {
            CLOSE.invoke(MemorySegment.ofAddress(connection));
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_close.", ex);
        }
    }

    @Override
    public long execute(long connection, String sql) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment sqlAddress = arena.allocateUtf8String(sql);
            MemorySegment result = (MemorySegment) EXECUTE.invoke(MemorySegment.ofAddress(connection), sqlAddress);
            if (isNull(result)) {
                throw failure("sonnetdb_execute");
            }
            return result.address();
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_execute.", ex);
        }
    }

    @Override
    public int bulkExecute(long connection, String payload, String measurement, String onError, String flush) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment payloadAddress = arena.allocateUtf8String(payload);
            MemorySegment bulk = (MemorySegment) BULK_CREATE.invoke(payloadAddress);
            if (isNull(bulk)) {
                throw failure("sonnetdb_bulk_create");
            }

            try {
                setBulkOption(arena, bulk, BULK_SET_MEASUREMENT, measurement, "sonnetdb_bulk_set_measurement");
                setBulkOption(arena, bulk, BULK_SET_ONERROR, onError, "sonnetdb_bulk_set_onerror");
                setBulkOption(arena, bulk, BULK_SET_FLUSH, flush, "sonnetdb_bulk_set_flush");
                MemorySegment result = (MemorySegment) BULK_EXECUTE.invoke(MemorySegment.ofAddress(connection), bulk);
                if (isNull(result)) {
                    throw failure("sonnetdb_bulk_execute");
                }
                try {
                    return recordsAffected(result.address());
                } finally {
                    resultFree(result.address());
                }
            } finally {
                BULK_FREE.invoke(bulk);
            }
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_bulk_execute.", ex);
        }
    }

    @Override
    public void resultFree(long result) {
        if (result == 0L) {
            return;
        }
        try {
            RESULT_FREE.invoke(MemorySegment.ofAddress(result));
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_result_free.", ex);
        }
    }

    @Override
    public int recordsAffected(long result) {
        return invokeInt(RECORDS_AFFECTED, result, "sonnetdb_result_records_affected");
    }

    @Override
    public int columnCount(long result) {
        return invokeInt(COLUMN_COUNT, result, "sonnetdb_result_column_count");
    }

    @Override
    public String columnName(long result, int ordinal) {
        try {
            MemorySegment address = (MemorySegment) COLUMN_NAME.invoke(MemorySegment.ofAddress(result), ordinal);
            if (isNull(address)) {
                throw failure("sonnetdb_result_column_name");
            }
            return readUtf8(address);
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_result_column_name.", ex);
        }
    }

    @Override
    public boolean next(long result) {
        int value = invokeInt(NEXT, result, "sonnetdb_result_next");
        if (value < 0) {
            throw failure("sonnetdb_result_next");
        }
        return value == 1;
    }

    @Override
    public SonnetDbValueType valueType(long result, int ordinal) {
        try {
            int code = (int) VALUE_TYPE.invoke(MemorySegment.ofAddress(result), ordinal);
            if (code < 0) {
                throw failure("sonnetdb_result_value_type");
            }
            return SonnetDbValueType.fromCode(code);
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_result_value_type.", ex);
        }
    }

    @Override
    public long valueInt64(long result, int ordinal) {
        try {
            return (long) VALUE_INT64.invoke(MemorySegment.ofAddress(result), ordinal);
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_result_value_int64: " + lastError(), ex);
        }
    }

    @Override
    public double valueDouble(long result, int ordinal) {
        try {
            return (double) VALUE_DOUBLE.invoke(MemorySegment.ofAddress(result), ordinal);
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_result_value_double: " + lastError(), ex);
        }
    }

    @Override
    public boolean valueBool(long result, int ordinal) {
        try {
            int value = (int) VALUE_BOOL.invoke(MemorySegment.ofAddress(result), ordinal);
            if (value < 0) {
                throw failure("sonnetdb_result_value_bool");
            }
            return value != 0;
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_result_value_bool.", ex);
        }
    }

    @Override
    public String valueText(long result, int ordinal) {
        try {
            MemorySegment address = (MemorySegment) VALUE_TEXT.invoke(MemorySegment.ofAddress(result), ordinal);
            return isNull(address) ? null : readUtf8(address);
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_result_value_text: " + lastError(), ex);
        }
    }

    @Override
    public long kvOpen(long connection, String keyspace, String namespaceName) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment keyspaceAddress = arena.allocateUtf8String(keyspace);
            MemorySegment namespaceAddress = namespaceName == null || namespaceName.isBlank()
                ? MemorySegment.NULL
                : arena.allocateUtf8String(namespaceName);
            MemorySegment kv = (MemorySegment) KV_OPEN.invoke(
                MemorySegment.ofAddress(connection),
                keyspaceAddress,
                namespaceAddress);
            if (isNull(kv)) {
                throw failure("sonnetdb_kv_open");
            }
            return kv.address();
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_kv_open.", ex);
        }
    }

    @Override
    public void kvClose(long kv) {
        if (kv == 0L) {
            return;
        }
        try {
            KV_CLOSE.invoke(MemorySegment.ofAddress(kv));
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_kv_close.", ex);
        }
    }

    @Override
    public long kvGet(long kv, String key) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment keyAddress = arena.allocateUtf8String(key);
            MemorySegment entry = (MemorySegment) KV_GET.invoke(MemorySegment.ofAddress(kv), keyAddress);
            if (isNull(entry)) {
                String message = lastError();
                if (message != null && !message.isBlank()) {
                    throw new SonnetDbException(message);
                }
                return 0L;
            }
            return entry.address();
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_kv_get.", ex);
        }
    }

    @Override
    public long kvSet(long kv, String key, byte[] value, long expiresAtUnixMs) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment keyAddress = arena.allocateUtf8String(key);
            MemorySegment valueAddress = allocateBytes(arena, value);
            long version = (long) KV_SET.invoke(
                MemorySegment.ofAddress(kv),
                keyAddress,
                valueAddress,
                value.length,
                expiresAtUnixMs);
            if (version < 0) {
                throw failure("sonnetdb_kv_set");
            }
            return version;
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_kv_set.", ex);
        }
    }

    @Override
    public boolean kvDelete(long kv, String key) {
        return invokeKvBoolean(KV_DELETE, kv, key, "sonnetdb_kv_delete");
    }

    @Override
    public long kvScanPrefix(long kv, String prefix, int limit) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment prefixAddress = arena.allocateUtf8String(prefix);
            MemorySegment scan = (MemorySegment) KV_SCAN_PREFIX.invoke(
                MemorySegment.ofAddress(kv),
                prefixAddress,
                limit);
            if (isNull(scan)) {
                throw failure("sonnetdb_kv_scan_prefix");
            }
            return scan.address();
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_kv_scan_prefix.", ex);
        }
    }

    @Override
    public long kvTtl(long kv, String key, long[] expiresAtUnixMs) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment keyAddress = arena.allocateUtf8String(key);
            MemorySegment expires = arena.allocate(ValueLayout.JAVA_LONG);
            expires.set(ValueLayout.JAVA_LONG, 0, -1L);
            long milliseconds = (long) KV_TTL.invoke(MemorySegment.ofAddress(kv), keyAddress, expires);
            if (milliseconds < -2) {
                throw failure("sonnetdb_kv_ttl");
            }
            if (expiresAtUnixMs != null && expiresAtUnixMs.length > 0) {
                expiresAtUnixMs[0] = expires.get(ValueLayout.JAVA_LONG, 0);
            }
            return milliseconds;
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_kv_ttl.", ex);
        }
    }

    @Override
    public boolean kvExpireAt(long kv, String key, long expiresAtUnixMs) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment keyAddress = arena.allocateUtf8String(key);
            int code = (int) KV_EXPIRE_AT.invoke(MemorySegment.ofAddress(kv), keyAddress, expiresAtUnixMs);
            if (code < 0) {
                throw failure("sonnetdb_kv_expire_at");
            }
            return code == 1;
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_kv_expire_at.", ex);
        }
    }

    @Override
    public boolean kvPersist(long kv, String key) {
        return invokeKvBoolean(KV_PERSIST, kv, key, "sonnetdb_kv_persist");
    }

    @Override
    public void kvIncr(long kv, String key, long delta, long[] valueAndVersion) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment keyAddress = arena.allocateUtf8String(key);
            MemorySegment value = arena.allocate(ValueLayout.JAVA_LONG);
            MemorySegment version = arena.allocate(ValueLayout.JAVA_LONG);
            int code = (int) KV_INCR.invoke(
                MemorySegment.ofAddress(kv),
                keyAddress,
                delta,
                value,
                version);
            if (code != 0) {
                throw failure("sonnetdb_kv_incr");
            }
            if (valueAndVersion != null && valueAndVersion.length >= 2) {
                valueAndVersion[0] = value.get(ValueLayout.JAVA_LONG, 0);
                valueAndVersion[1] = version.get(ValueLayout.JAVA_LONG, 0);
            }
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_kv_incr.", ex);
        }
    }

    @Override
    public boolean kvCas(
        long kv,
        String key,
        long expectedVersion,
        byte[] value,
        long expiresAtUnixMs,
        long[] currentAndNewVersion) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment keyAddress = arena.allocateUtf8String(key);
            MemorySegment valueAddress = allocateBytes(arena, value);
            MemorySegment current = arena.allocate(ValueLayout.JAVA_LONG);
            MemorySegment next = arena.allocate(ValueLayout.JAVA_LONG);
            int code = (int) KV_CAS.invoke(
                MemorySegment.ofAddress(kv),
                keyAddress,
                expectedVersion,
                valueAddress,
                value.length,
                expiresAtUnixMs,
                current,
                next);
            if (code < 0) {
                throw failure("sonnetdb_kv_cas");
            }
            if (currentAndNewVersion != null && currentAndNewVersion.length >= 2) {
                currentAndNewVersion[0] = current.get(ValueLayout.JAVA_LONG, 0);
                currentAndNewVersion[1] = next.get(ValueLayout.JAVA_LONG, 0);
            }
            return code == 1;
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_kv_cas.", ex);
        }
    }

    @Override
    public void kvEntryFree(long entry) {
        invokeAddressVoid(KV_ENTRY_FREE, entry, "sonnetdb_kv_entry_free");
    }

    @Override
    public String kvEntryKey(long entry) {
        return invokeAddressString(KV_ENTRY_KEY, entry, "sonnetdb_kv_entry_key");
    }

    @Override
    public byte[] kvEntryValue(long entry) {
        return copyBytes(entry, KV_ENTRY_VALUE_LENGTH, KV_ENTRY_COPY_VALUE, "sonnetdb_kv_entry_copy_value");
    }

    @Override
    public long kvEntryVersion(long entry) {
        return invokeAddressLong(KV_ENTRY_VERSION, entry, "sonnetdb_kv_entry_version");
    }

    @Override
    public long kvEntryExpiresAtUnixMs(long entry) {
        return invokeAddressLong(KV_ENTRY_EXPIRES_AT_UNIX_MS, entry, "sonnetdb_kv_entry_expires_at_unix_ms");
    }

    @Override
    public boolean kvScanNext(long scan) {
        int code = invokeAddressInt(KV_SCAN_NEXT, scan, "sonnetdb_kv_scan_next");
        if (code < 0) {
            throw failure("sonnetdb_kv_scan_next");
        }
        return code == 1;
    }

    @Override
    public String kvScanKey(long scan) {
        return invokeAddressString(KV_SCAN_KEY, scan, "sonnetdb_kv_scan_key");
    }

    @Override
    public byte[] kvScanValue(long scan) {
        return copyBytes(scan, KV_SCAN_VALUE_LENGTH, KV_SCAN_COPY_VALUE, "sonnetdb_kv_scan_copy_value");
    }

    @Override
    public long kvScanVersion(long scan) {
        return invokeAddressLong(KV_SCAN_VERSION, scan, "sonnetdb_kv_scan_version");
    }

    @Override
    public long kvScanExpiresAtUnixMs(long scan) {
        return invokeAddressLong(KV_SCAN_EXPIRES_AT_UNIX_MS, scan, "sonnetdb_kv_scan_expires_at_unix_ms");
    }

    @Override
    public void kvScanFree(long scan) {
        invokeAddressVoid(KV_SCAN_FREE, scan, "sonnetdb_kv_scan_free");
    }

    @Override
    public long docOpen(long connection, String collection) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment collectionAddress = arena.allocateUtf8String(collection);
            MemorySegment document = (MemorySegment) DOC_OPEN.invoke(
                MemorySegment.ofAddress(connection),
                collectionAddress);
            if (isNull(document)) {
                throw failure("sonnetdb_doc_open");
            }
            return document.address();
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_doc_open.", ex);
        }
    }

    @Override
    public void docClose(long document) {
        invokeAddressVoid(DOC_CLOSE, document, "sonnetdb_doc_close");
    }

    @Override
    public String docCreateCollection(long document, String optionsJson) {
        return invokeDocumentJson(
            DOC_CREATE_COLLECTION,
            document,
            optionsJson,
            false,
            "sonnetdb_doc_create_collection");
    }

    @Override
    public boolean docDropCollection(long document) {
        int code = invokeAddressInt(DOC_DROP_COLLECTION, document, "sonnetdb_doc_drop_collection");
        if (code < 0) {
            throw failure("sonnetdb_doc_drop_collection");
        }
        return code == 1;
    }

    @Override
    public String docInsert(long document, String payloadJson) {
        return invokeDocumentJson(DOC_INSERT, document, payloadJson, true, "sonnetdb_doc_insert");
    }

    @Override
    public String docUpdate(long document, String payloadJson) {
        return invokeDocumentJson(DOC_UPDATE, document, payloadJson, true, "sonnetdb_doc_update");
    }

    @Override
    public String docDelete(long document, String payloadJson) {
        return invokeDocumentJson(DOC_DELETE, document, payloadJson, true, "sonnetdb_doc_delete");
    }

    @Override
    public String docFindPage(long document, String payloadJson) {
        return invokeDocumentJson(DOC_FIND_PAGE, document, payloadJson, false, "sonnetdb_doc_find_page");
    }

    @Override
    public String docAggregate(long document, String payloadJson) {
        return invokeDocumentJson(DOC_AGGREGATE, document, payloadJson, true, "sonnetdb_doc_aggregate");
    }

    @Override
    public void flush(long connection) {
        try {
            int code = (int) FLUSH.invoke(MemorySegment.ofAddress(connection));
            if (code != 0) {
                throw failure("sonnetdb_flush");
            }
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call sonnetdb_flush.", ex);
        }
    }

    @Override
    public String version() {
        return copyString(VERSION, "sonnetdb_version");
    }

    @Override
    public String lastError() {
        return copyString(LAST_ERROR, "sonnetdb_last_error");
    }

    private static MethodHandle downcall(String symbol, FunctionDescriptor descriptor) {
        MemorySegment address = SymbolLookup.loaderLookup()
            .find(symbol)
            .orElseThrow(() -> new SonnetDbException("Native symbol not found: " + symbol));
        return LINKER.downcallHandle(address, descriptor);
    }

    private static int invokeInt(MethodHandle handle, long argument, String functionName) {
        try {
            int value = (int) handle.invoke(MemorySegment.ofAddress(argument));
            if (value < 0) {
                throw failure(functionName);
            }
            return value;
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call " + functionName + ".", ex);
        }
    }

    private static boolean invokeKvBoolean(MethodHandle handle, long kv, String key, String functionName) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment keyAddress = arena.allocateUtf8String(key);
            int code = (int) handle.invoke(MemorySegment.ofAddress(kv), keyAddress);
            if (code < 0) {
                throw failure(functionName);
            }
            return code == 1;
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call " + functionName + ".", ex);
        }
    }

    private static void setBulkOption(
        Arena arena,
        MemorySegment bulk,
        MethodHandle handle,
        String value,
        String functionName) throws Throwable {
        if (value == null || value.isBlank()) {
            return;
        }

        MemorySegment address = arena.allocateUtf8String(value);
        int code = (int) handle.invoke(bulk, address);
        if (code != 0) {
            throw failure(functionName);
        }
    }

    private static String invokeDocumentJson(
        MethodHandle handle,
        long document,
        String payloadJson,
        boolean required,
        String functionName) {
        if (required && (payloadJson == null || payloadJson.isEmpty())) {
            throw new SonnetDbException("Document JSON payload must not be empty.");
        }

        try (Arena arena = Arena.ofConfined()) {
            MemorySegment payloadAddress = payloadJson == null || payloadJson.isEmpty()
                ? MemorySegment.NULL
                : arena.allocateUtf8String(payloadJson);
            MemorySegment result = (MemorySegment) handle.invoke(MemorySegment.ofAddress(document), payloadAddress);
            if (isNull(result)) {
                throw failure(functionName);
            }
            try {
                return copyDocumentJson(result, functionName);
            } finally {
                DOC_RESULT_FREE.invoke(result);
            }
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call " + functionName + ".", ex);
        }
    }

    private static void invokeAddressVoid(MethodHandle handle, long argument, String functionName) {
        if (argument == 0L) {
            return;
        }
        try {
            handle.invoke(MemorySegment.ofAddress(argument));
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call " + functionName + ".", ex);
        }
    }

    private static int invokeAddressInt(MethodHandle handle, long argument, String functionName) {
        try {
            return (int) handle.invoke(MemorySegment.ofAddress(argument));
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call " + functionName + ".", ex);
        }
    }

    private static long invokeAddressLong(MethodHandle handle, long argument, String functionName) {
        try {
            return (long) handle.invoke(MemorySegment.ofAddress(argument));
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call " + functionName + ".", ex);
        }
    }

    private static String invokeAddressString(MethodHandle handle, long argument, String functionName) {
        try {
            MemorySegment address = (MemorySegment) handle.invoke(MemorySegment.ofAddress(argument));
            if (isNull(address)) {
                throw failure(functionName);
            }
            return readUtf8(address);
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call " + functionName + ".", ex);
        }
    }

    private static String copyDocumentJson(MemorySegment result, String functionName) throws Throwable {
        int required = (int) DOC_RESULT_JSON_LENGTH.invoke(result);
        if (required < 0) {
            throw failure(functionName);
        }

        try (Arena arena = Arena.ofConfined()) {
            MemorySegment buffer = arena.allocate((long) required + 1);
            int copied = (int) DOC_RESULT_COPY_JSON.invoke(result, buffer, required + 1);
            if (copied < 0) {
                throw failure(functionName);
            }
            return buffer.getUtf8String(0);
        }
    }

    private static MemorySegment allocateBytes(Arena arena, byte[] value) {
        if (value.length == 0) {
            return MemorySegment.NULL;
        }

        MemorySegment segment = arena.allocate(value.length);
        segment.asByteBuffer().put(value);
        return segment;
    }

    private static byte[] copyBytes(long handle, MethodHandle lengthHandle, MethodHandle copyHandle, String functionName) {
        try (Arena arena = Arena.ofConfined()) {
            long required = (long) lengthHandle.invoke(MemorySegment.ofAddress(handle));
            if (required < 0 || required > Integer.MAX_VALUE) {
                throw failure(functionName);
            }

            byte[] value = new byte[(int) required];
            if (required == 0) {
                return value;
            }

            MemorySegment buffer = arena.allocate(required);
            int copied = (int) copyHandle.invoke(MemorySegment.ofAddress(handle), buffer, (int) required);
            if (copied < 0) {
                throw failure(functionName);
            }
            buffer.asByteBuffer().get(value);
            return value;
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call " + functionName + ".", ex);
        }
    }

    private static String copyString(MethodHandle handle, String functionName) {
        try (Arena arena = Arena.ofConfined()) {
            MemorySegment first = arena.allocate(4096);
            int required = (int) handle.invoke(first, 4096);
            if (required < 0) {
                throw new SonnetDbException("Failed to call " + functionName + ".");
            }
            if (required < 4096) {
                return first.getUtf8String(0);
            }

            MemorySegment exact = arena.allocate((long) required + 1);
            int second = (int) handle.invoke(exact, required + 1);
            if (second < 0) {
                throw new SonnetDbException("Failed to call " + functionName + ".");
            }
            return exact.getUtf8String(0);
        } catch (SonnetDbException ex) {
            throw ex;
        } catch (Throwable ex) {
            throw new SonnetDbException("Failed to call " + functionName + ".", ex);
        }
    }

    private static boolean isNull(MemorySegment address) {
        return address == null || MemorySegment.NULL.equals(address);
    }

    private static String readUtf8(MemorySegment address) {
        return address.reinterpret(Long.MAX_VALUE).getUtf8String(0);
    }

    private static SonnetDbException failure(String functionName) {
        String message = copyString(LAST_ERROR, "sonnetdb_last_error");
        if (message == null || message.isBlank()) {
            message = functionName + " failed.";
        }
        return new SonnetDbException(message);
    }

    private static void loadNativeLibrary() {
        String path = System.getProperty("sonnetdb.native.path");
        if (path == null || path.isBlank()) {
            path = System.getenv("SONNETDB_NATIVE_LIBRARY");
        }

        if (path != null && !path.isBlank()) {
            System.load(Path.of(path).toAbsolutePath().toString());
            return;
        }

        try {
            System.loadLibrary("SonnetDB.Native");
        } catch (UnsatisfiedLinkError ex) {
            throw new SonnetDbException(
                "Cannot load SonnetDB native library. Set -Dsonnetdb.native.path or SONNETDB_NATIVE_LIBRARY.",
                ex);
        }
    }
}
