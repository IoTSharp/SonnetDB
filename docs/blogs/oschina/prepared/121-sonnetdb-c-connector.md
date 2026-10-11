## SonnetDB C 连接器：嵌入式时序数据库的原生接入

SonnetDB C 连接器由 C# 实现并通过 .NET Native AOT 输出共享库，对外提供不透明句柄、基础数值和 UTF-8 字符串组成的 C ABI。C/C++ 应用复用同一套引擎，不需要解析 SonnetDB 的文件格式。

旧版本介绍中的“17 个函数”已经不能描述当前 ABI。当前头文件除连接、SQL 与结果外，还提供批量摄入、Document、KV、Object Storage、分块对象读写和 MQ 的独立函数组。本篇先把最小 SQL 生命周期讲清楚。

本文依据当前 `connectors/c` 整理。正式 4.0.0 是独立发布标签；main 中扩展的 ABI 不应冒称所有已发布 Native 库都具备。部署时必须配对同一版本的头文件、共享库和语言封装。

### Native AOT 与 C 的边界

```text
C/C++ 应用
    -> sonnetdb.h 的 C ABI / cdecl
    -> SonnetDB.Native.dll 或 SonnetDB.Native.so
    -> SonnetDB.Data
    -> 本地引擎或远程 SonnetDB
```

Native AOT 共享库不要求目标机另外安装 .NET 托管运行时，但仍依赖目标平台的原生运行库、架构和操作系统合同。一个 x64 DLL 不能由 x86 进程加载。

`sonnetdb_open("./data")` 保留本地目录简写，也支持 ADO.NET 连接字符串，例如 `Data Source=sonnetdb+https://db.example.com/metrics;Token=<token>;Mode=Remote`。远程凭据应由部署环境提供，不能硬编码。

### 完整的小批次 SQL 程序

下面使用一个新的演示目录，检查每一次结果句柄。返回字符串由 result 管理，需要长期保留时在释放结果前复制。

```c
#include <stdio.h>
#include "sonnetdb.h"

static void print_error(const char* operation) {
    char message[2048] = {0};
    sonnetdb_last_error(message, (int32_t)sizeof(message));
    fprintf(stderr, "%s: %s\n", operation, message);
}

int main(void) {
    int status = 1;
    sonnetdb_connection* connection = sonnetdb_open("./c-cpu-demo");
    sonnetdb_result* result = NULL;
    if (!connection) { print_error("open"); return status; }

    result = sonnetdb_execute(connection,
        "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
    if (!result) { print_error("create"); goto cleanup; }
    sonnetdb_result_free(result); result = NULL;

    result = sonnetdb_execute(connection,
        "INSERT INTO cpu (time, host, usage) VALUES "
        "(1710000000000, 'edge-1', 0.42), "
        "(1710000001000, 'edge-1', 0.73)");
    if (!result) { print_error("insert"); goto cleanup; }
    printf("inserted: %d\n", sonnetdb_result_records_affected(result));
    sonnetdb_result_free(result); result = NULL;

    result = sonnetdb_execute(connection,
        "SELECT time, host, usage FROM cpu ORDER BY time LIMIT 10");
    if (!result) { print_error("query"); goto cleanup; }
    for (int row = 0; row < 10; ++row) {
        int32_t next = sonnetdb_result_next(result);
        if (next == 0) break;
        if (next < 0) { print_error("next"); goto cleanup; }
        const char* host = sonnetdb_result_value_text(result, 1);
        printf("%lld\t%s\t%.3f\n",
            (long long)sonnetdb_result_value_int64(result, 0),
            host ? host : "<null>",
            sonnetdb_result_value_double(result, 2));
    }
    if (sonnetdb_flush(connection) < 0) { print_error("flush"); goto cleanup; }
    status = 0;
cleanup:
    if (result) sonnetdb_result_free(result);
    sonnetdb_close(connection);
    return status;
}
```

列序号从 0 开始。NULL 应先用 `sonnetdb_result_value_type` 判断，getter 的默认数值不能替代空值判定。五种 ABI 值是 NULL、INT64、DOUBLE、BOOL、TEXT；复杂类型通过具体文本合同表达，不应假定它们都是完整原生结构体。

错误信息是 native 线程局部状态，应在失败调用后立刻读取。`last_error` / `version` 使用调用者缓冲区并返回所需字节数；固定缓冲区只是演示，生产需按长度分配并限制最大错误长度。

### 批量摄入使用独立句柄

```c
sonnetdb_bulk* bulk = sonnetdb_bulk_create(
    "cpu,host=edge-2 usage=0.81 1710000002000\n"
    "cpu,host=edge-2 usage=0.86 1710000003000");
/* 生产代码逐项检查 bulk、setter 和 execute 的返回值。 */
sonnetdb_bulk_set_measurement(bulk, "cpu");
sonnetdb_bulk_set_flush(bulk, "async");
sonnetdb_result* written = sonnetdb_bulk_execute(connection, bulk);
if (written) sonnetdb_result_free(written);
sonnetdb_bulk_free(bulk);
```

该片段在仍打开的连接上使用，正文也支持 JSON points 和 Bulk VALUES。`async` 仅发出后台 flush 信号。`sonnetdb_flush` 是嵌入式 helper，远程批量写入应通过 bulk 的 flush 选项，不能把它当作远程 durability 命令。

### 构建与发布

源码包含 Windows x64/x86/ARM64 和 Linux x64 的 CMake 配置；存在配置不等于每个平台都已实机验收。Linux 示例为：

```bash
cmake -S connectors/c --preset linux-x64
cmake --build artifacts/connectors/c/linux-x64
```

Windows 预设的 generator 要与实际安装的 Visual Studio 对应，不能直接假定机器必有 VS 2022。构建需要 .NET 10 SDK、Native AOT 所需的原生工具链与平台依赖；本篇不执行构建或安装。

C ABI 的稳定之处是公开边界，行为一致性仍需对应平台和后端 smoke test。数据库目录的备份、升级与多进程写入边界也不会因换成 C 调用而消失。

参考：[C 连接器](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/c/README.md)、[头文件](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/c/include/sonnetdb.h)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
