## SonnetDB Rust 连接器：手写 FFI 与安全封装

SonnetDB Rust 连接器在手写 C ABI 声明之上提供 `Connection` 和 `ResultSet`，把调用错误、UTF-8 字符串和句柄释放转换成 Rust 的常用 API。当前版本还封装 bulk、KV、Document，不需要调用方直接处理 native 指针。

本文依据当前 Cargo.toml、README 与 lib.rs 整理，正式 4.0.0 与 main 分开核对。`Cargo.toml` 中的 connector 版本和数据库软件版本是不同编号，不能假定该 crate 的所有发布渠道都已经具备 main 功能。

### 完整的小样本程序

```rust
use std::time::{Duration, Instant};

fn main() -> sonnetdb::Result<()> {
    let connection = sonnetdb::Connection::open("./rust-cpu-demo")?;
    connection.execute_non_query(
        "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)")?;
    connection.execute_non_query(
        "INSERT INTO cpu (time, host, usage) VALUES \
        (1710000000000,'edge-1',0.42),(1710000001000,'edge-1',0.73)")?;

    {
        let mut result = connection.execute(
            "SELECT time, host, usage FROM cpu ORDER BY time LIMIT 10")?;
        let started = Instant::now();
        for _ in 0..10 {
            if started.elapsed() > Duration::from_secs(5) {
                return Err(sonnetdb::Error::new("read budget exceeded"));
            }
            if !result.next()? { break; }
            let timestamp = result.get_i64(0)?;
            let host = result.get_text(1)?.unwrap_or_default();
            let usage = result.get_f64(2)?;
            println!("{timestamp}\t{host}\t{usage:.3}");
        }
        result.close()?;
    }
    connection.flush()?;
    connection.close()?;
    Ok(())
}
```

样例在新的目录建表，列序号从 0 开始；真正业务中的 NULL 不能随意统一替换为 0 或空字符串。result 明确先释放，再关闭 connection。Drop 提供兜底释放，但不能替代需要捕获错误的显式 close，也不自动给每个独立 native handle 建立任意借用关系。

墙钟检查发生在同步调用之间，并不能硬中断已进入 Native 的操作。使用不同线程或共享连接时，应按封装的 Send/Sync 与引擎合同处理，不能因为外层 API 是 safe 就推导任意并发都安全。

### 值与字符串

`ValueType` 区分 Null、Int64、Double、Bool、Text，`Value` 提供相应 Rust 值。typed getter 先检查类型；数值转换也有范围和精度边界，例如大整数转 f64 不保证逐位精确。

输入路径和 SQL 经 CString 转为 UTF-8，内嵌 NUL 会被拒绝。返回文本复制为 Rust String，不能把 native 的临时指针带出句柄生命周期。

原生实现通过线程局部错误状态提供错误，调用方应优先使用 wrapper 返回的 `Result<T,Error>`。当前 SQL ABI 接收完整文本，没有 SQL prepare/bind 参数接口，不应使用外部字符串直接拼接查询。

### 已有批量接口

在打开的 connection 上使用：

```rust
let written = connection.execute_bulk(
    "cpu,host=edge-2 usage=0.81 1710000002000",
    Some(&sonnetdb::BulkOptions {
        measurement: Some("cpu".into()),
        on_error: Some("failfast".into()),
        flush: Some("async".into()),
    }),
)?;
```

这纠正历史稿“以后才加入 bulk”的说法。`open_kv`、`open_document_collection` 同样已有封装；它们共享底层引擎，却有不同权限、载荷、事务与恢复边界。

### 手写 FFI 与构建

ffi.rs 与 `sonnetdb.h` 配对维护，内部 Rust 原生调用使用必要的 FFI 边界，公共 wrapper 封装指针；这与 C# Core 的 safe-only 约束是不同语言层次。当前 Cargo manifest 没有额外第三方 crate，并不表示最终共享库没有平台原生依赖。

Windows 需要 Native 的 import library 和 DLL；Linux 链接 `SonnetDB.Native.so`。`SONNETDB_NATIVE_LIB_DIR` 可指定已构建库目录：

```bash
cmake -S connectors/c --preset linux-x64
cmake --build artifacts/connectors/c/linux-x64
cd connectors/rust
native="$(realpath ../../artifacts/connectors/c/linux-x64)"
SONNETDB_NATIVE_LIB_DIR="$native" LD_LIBRARY_PATH="$native:${LD_LIBRARY_PATH:-}" cargo run --example quickstart
```

本次只核查源码，不编译或写入。发布时应固定头文件、FFI 与 Native 版本，做目标平台的打开、写入、读取、错误和关闭 smoke test。

参考：[Rust README](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/rust/README.md)、[安全封装](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/rust/src/lib.rs)、[FFI](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/rust/src/ffi.rs)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
