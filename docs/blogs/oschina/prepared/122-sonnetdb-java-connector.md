## SonnetDB Java 连接器：一套 API，双后端驱动

SonnetDB Java 连接器在公共 API 之下提供 JNI 与 FFM 两条原生调用路径。它们共同调用 C ABI，Java 应用使用 `SonnetDbConnection` 与 `SonnetDbResult` 管理 SQL 和结果，也可使用当前封装的 bulk、KV、Document 接口。

本文依据当前 Java README、后端加载器和 CMake 配置整理。正式 4.0.0 与 main 分开核对，部署时应配对同一版本的 JAR、JNI bridge 和 SonnetDB.Native。本连接器是原生 API 包装，不应直接当作实现完整 `java.sql.Driver` 的 JDBC 驱动。

### 默认 JNI，FFM 需要显式选择

| 选项 | 行为 |
| --- | --- |
| `jni` | 默认后端，Java 8 兼容的 API 与桥接库 |
| `ffm` | 通过多版本 JAR 中的 FFM 实现直接调用 Native |
| `auto` | JDK 特性版本至少 21 时尝试 FFM，加载失败再回 JNI |

系统属性为 `sonnetdb.java.backend`，环境变量回退为 `SONNETDB_JAVA_BACKEND`。默认值是 `jni`，不是“任何 JDK 上自动选择最快后端”。后端在进程初始化时加载，不能假定每个连接可以动态切换。

JNI 路径需要 SonnetDB.Native 与 SonnetDB.Java.Native 两个原生库；FFM 路径不需要 JNI bridge。减少一个库不直接证明端到端查询更快。

### 完整 SQL 示例

下面使用新的空目录，并展示资源释放与固定行数查询。

```java
import com.sonnetdb.SonnetDbConnection;
import com.sonnetdb.SonnetDbResult;

public final class Quickstart {
    public static void main(String[] args) {
        try (SonnetDbConnection connection = SonnetDbConnection.open("./java-cpu-demo")) {
            connection.executeNonQuery(
                "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
            int inserted = connection.executeNonQuery(
                "INSERT INTO cpu (time, host, usage) VALUES " +
                "(1710000000000, 'edge-1', 0.42)," +
                "(1710000001000, 'edge-1', 0.73)");
            System.out.println("inserted: " + inserted);
            try (SonnetDbResult result = connection.execute(
                    "SELECT time, host, usage FROM cpu ORDER BY time LIMIT 10")) {
                for (int row = 0; row < 10 && result.next(); row++) {
                    System.out.printf("%d\t%s\t%.3f%n",
                        result.getLong(0), result.getString(1), result.getDouble(2));
                }
            }
            connection.flush();
        }
    }
}
```

列序号从 0 开始，`executeNonQuery` 会处理并释放非查询结果。生产数据有 NULL 时应按结果类型检查，应用捕获 `SonnetDbException` 后记录操作、版本与安全错误信息，不输出凭据。

### 多版本 JAR 与 preview 边界

基础公共类按 Java 8 编译，FFM 实现在 `META-INF/versions/21` 等配置版本目录中，manifest 声明 `Multi-Release: true`。低版本 JVM 使用基础类；高版本 JVM 选择版本化类，但这不代表 preview 字节码可跨任意新 JDK 运行。

默认双后端配置针对 JDK 21 FFM preview，构建和运行需要匹配的 JDK 21，并传 `--enable-preview --enable-native-access=ALL-UNNAMED`。JDK 22 之后 API 正式化，不应直接把 JDK 21 preview JAR 当作“21+ 全版本无缝运行”；更换 FFM release 或关掉 preview 需要按对应 JDK 重新构建和验证。

Java 8-only 环境可以设置 `SONNETDB_JAVA_BUILD_FFM=OFF`。它仍需要当前平台的 Native 库与 JNI bridge。

### 启动命令按操作系统选择分隔符

Windows 的 classpath 分隔符是分号。下面假设三个文件都放在当前部署目录，业务类位于 `app.jar`：

```powershell
java -Dsonnetdb.java.backend=jni -Dsonnetdb.native.path=./SonnetDB.Native.dll -Dsonnetdb.jni.path=./SonnetDB.Java.Native.dll -cp "sonnetdb-java.jar;app.jar" Quickstart
```

JDK 21 FFM：

```powershell
java --enable-preview --enable-native-access=ALL-UNNAMED -Dsonnetdb.java.backend=ffm -Dsonnetdb.native.path=./SonnetDB.Native.dll -cp "sonnetdb-java.jar;app.jar" Quickstart
```

Linux 使用 `SonnetDB.Native.so`、对应 JNI `.so` 和冒号 classpath。库路径也可通过 `SONNETDB_NATIVE_LIBRARY`、`SONNETDB_JNI_LIBRARY` 提供，不应依赖不清楚来源的全局 PATH。

### 源码构建与 API 扩展

先构建 C 连接器，再构建 Java bridge/JAR，README 提供 Windows、Linux 和 JNI-only 预设。预设 generator、JDK 与 ABI 架构必须匹配；本篇不安装依赖或实际编译，支持范围需要目标环境验证。

当前 API 还提供 `executeBulk`、`openKeyValueStore` 与 `openDocumentCollection` 等独立模型入口。它们扩展了历史版本只有 SQL 的范围，却不会自动带来 prepared/bind、任意事务或跨模型原子性。使用前按对应方法和 JSON 合同验证小批次。

参考：[Java 连接器](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/java/README.md)、[后端加载器](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/java/src/main/java/com/sonnetdb/internal/NativeBackendLoader.java)、[构建配置](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/java/CMakeLists.txt)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
