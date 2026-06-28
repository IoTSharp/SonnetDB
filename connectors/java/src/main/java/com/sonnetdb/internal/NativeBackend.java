package com.sonnetdb.internal;

import com.sonnetdb.SonnetDbValueType;

/**
 * SonnetDB native 调用后端。
 */
public interface NativeBackend {
    /**
     * 打开数据库。
     *
     * @param dataSource 数据库根目录。
     * @return native connection 句柄。
     */
    long open(String dataSource);

    /**
     * 关闭数据库连接。
     *
     * @param connection native connection 句柄。
     */
    void close(long connection);

    /**
     * 执行 SQL。
     *
     * @param connection native connection 句柄。
     * @param sql SQL 文本。
     * @return native result 句柄。
     */
    long execute(long connection, String sql);

    int bulkExecute(long connection, String payload, String measurement, String onError, String flush);

    /**
     * 释放结果。
     *
     * @param result native result 句柄。
     */
    void resultFree(long result);

    /**
     * 返回受影响行数。
     *
     * @param result native result 句柄。
     * @return 受影响行数。
     */
    int recordsAffected(long result);

    /**
     * 返回列数。
     *
     * @param result native result 句柄。
     * @return 列数。
     */
    int columnCount(long result);

    /**
     * 返回列名。
     *
     * @param result native result 句柄。
     * @param ordinal 列序号。
     * @return 列名。
     */
    String columnName(long result, int ordinal);

    /**
     * 移动到下一行。
     *
     * @param result native result 句柄。
     * @return 是否存在下一行。
     */
    boolean next(long result);

    /**
     * 返回值类型。
     *
     * @param result native result 句柄。
     * @param ordinal 列序号。
     * @return 值类型。
     */
    SonnetDbValueType valueType(long result, int ordinal);

    /**
     * 读取 int64 值。
     *
     * @param result native result 句柄。
     * @param ordinal 列序号。
     * @return int64 值。
     */
    long valueInt64(long result, int ordinal);

    /**
     * 读取 double 值。
     *
     * @param result native result 句柄。
     * @param ordinal 列序号。
     * @return double 值。
     */
    double valueDouble(long result, int ordinal);

    /**
     * 读取 bool 值。
     *
     * @param result native result 句柄。
     * @param ordinal 列序号。
     * @return bool 值。
     */
    boolean valueBool(long result, int ordinal);

    /**
     * 读取字符串值。
     *
     * @param result native result 句柄。
     * @param ordinal 列序号。
     * @return 字符串值。
     */
    String valueText(long result, int ordinal);

    /**
     * 打开 KV keyspace/namespace 句柄。
     */
    long kvOpen(long connection, String keyspace, String namespaceName);

    /**
     * 关闭 KV 句柄。
     */
    void kvClose(long kv);

    /**
     * 读取 KV entry；缺失时返回 0。
     */
    long kvGet(long kv, String key);

    /**
     * 写入 KV value 并返回写入版本。
     */
    long kvSet(long kv, String key, byte[] value, long expiresAtUnixMs);

    /**
     * 删除 KV key。
     */
    boolean kvDelete(long kv, String key);

    /**
     * 前缀扫描 KV key。
     */
    long kvScanPrefix(long kv, String prefix, int limit);

    /**
     * 查询 KV TTL。
     */
    long kvTtl(long kv, String key, long[] expiresAtUnixMs);

    /**
     * 设置 KV 绝对过期时间。
     */
    boolean kvExpireAt(long kv, String key, long expiresAtUnixMs);

    /**
     * 移除 KV 过期时间。
     */
    boolean kvPersist(long kv, String key);

    /**
     * 原子递增 KV 整数值。
     */
    void kvIncr(long kv, String key, long delta, long[] valueAndVersion);

    /**
     * KV CAS。
     */
    boolean kvCas(
        long kv,
        String key,
        long expectedVersion,
        byte[] value,
        long expiresAtUnixMs,
        long[] currentAndNewVersion);

    /**
     * 释放 KV entry 句柄。
     */
    void kvEntryFree(long entry);

    String kvEntryKey(long entry);

    byte[] kvEntryValue(long entry);

    long kvEntryVersion(long entry);

    long kvEntryExpiresAtUnixMs(long entry);

    boolean kvScanNext(long scan);

    String kvScanKey(long scan);

    byte[] kvScanValue(long scan);

    long kvScanVersion(long scan);

    long kvScanExpiresAtUnixMs(long scan);

    void kvScanFree(long scan);

    long docOpen(long connection, String collection);

    void docClose(long document);

    String docCreateCollection(long document, String optionsJson);

    boolean docDropCollection(long document);

    String docInsert(long document, String payloadJson);

    String docUpdate(long document, String payloadJson);

    String docDelete(long document, String payloadJson);

    String docFindPage(long document, String payloadJson);

    String docAggregate(long document, String payloadJson);

    /**
     * 主动 Flush。
     *
     * @param connection native connection 句柄。
     */
    void flush(long connection);

    /**
     * 返回 native library 版本。
     *
     * @return 版本。
     */
    String version();

    /**
     * 返回最近错误。
     *
     * @return 错误消息。
     */
    String lastError();
}
