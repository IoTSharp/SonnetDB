package com.sonnetdb;

import com.sonnetdb.internal.NativeBackend;
import com.sonnetdb.internal.NativeBackendLoader;

import java.util.Objects;

/**
 * SonnetDB Java 连接对象。
 *
 * <p>该类型通过 SonnetDB C ABI 打开本地嵌入式数据库目录，并执行 SQL。</p>
 */
public final class SonnetDbConnection implements AutoCloseable {
    private static final NativeBackend Backend = NativeBackendLoader.load();

    private long handle;

    private SonnetDbConnection(long handle) {
        this.handle = handle;
    }

    /**
     * 打开一个 SonnetDB 嵌入式数据库目录。
     *
     * @param dataSource 数据库根目录路径。
     * @return 已打开的连接。
     */
    public static SonnetDbConnection open(String dataSource) {
        Objects.requireNonNull(dataSource, "dataSource");
        return new SonnetDbConnection(Backend.open(dataSource));
    }

    /**
     * 返回底层 SonnetDB native library 版本。
     *
     * @return 版本字符串。
     */
    public static String version() {
        return Backend.version();
    }

    /**
     * 执行一条 SQL 语句。
     *
     * @param sql SQL 文本。
     * @return 查询或非查询结果句柄；调用方必须关闭。
     */
    public SonnetDbResult execute(String sql) {
        Objects.requireNonNull(sql, "sql");
        ensureOpen();
        return new SonnetDbResult(Backend, Backend.execute(handle, sql));
    }

    /**
     * 执行非查询 SQL，并返回受影响行数。
     *
     * @param sql SQL 文本。
     * @return INSERT 返回写入行数；DELETE 返回墓碑数；SELECT 返回 -1。
     */
    public int executeNonQuery(String sql) {
        try (SonnetDbResult result = execute(sql)) {
            return result.recordsAffected();
        }
    }

    /**
     * 打开 KV keyspace 的 root namespace。
     *
     * @param keyspace keyspace 名称。
     * @return KV 句柄；调用方必须关闭。
     */
    public SonnetDbKeyValueStore openKeyValueStore(String keyspace) {
        return openKeyValueStore(keyspace, "");
    }

    /**
     * 打开 KV keyspace/namespace。
     *
     * @param keyspace keyspace 名称。
     * @param namespaceName 逻辑命名空间；空字符串表示 root namespace。
     * @return KV 句柄；调用方必须关闭。
     */
    public SonnetDbKeyValueStore openKeyValueStore(String keyspace, String namespaceName) {
        Objects.requireNonNull(keyspace, "keyspace");
        Objects.requireNonNull(namespaceName, "namespaceName");
        ensureOpen();
        return new SonnetDbKeyValueStore(
            Backend,
            Backend.kvOpen(handle, keyspace, namespaceName));
    }

    /**
     * 主动触发一次 Flush。
     */
    public void flush() {
        ensureOpen();
        Backend.flush(handle);
    }

    /**
     * 关闭连接。
     */
    @Override
    public void close() {
        long current = handle;
        handle = 0L;
        if (current != 0L) {
            Backend.close(current);
        }
    }

    private void ensureOpen() {
        if (handle == 0L) {
            throw new SonnetDbException("SonnetDB connection is closed.");
        }
    }
}
