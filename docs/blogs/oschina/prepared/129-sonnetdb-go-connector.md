## SonnetDB Go 连接器：cgo 与 database/sql 的双入口

SonnetDB Go 连接器通过 cgo 调用 C ABI，提供直接 `sonnetdb.Open` API 和标准库 `database/sql` driver 两个入口。当前封装还包含 bulk、KV 与 Document，不再只是最早版本的 SQL cursor。

本文依据当前 README、go.mod 和 driver 实现整理。正式 4.0.0 与 main 分开核对；模块路径以仓库 `go.mod` 为准，不能据源码存在假定某个版本已经发布到公共模块代理。

### 完整直接 API 示例

下面使用新目录，明确检查每个 getter 的错误，最多读取 10 行。代码中的 module import 是当前声明路径，实际应用应固定对应源码提交或已验证版本。

```go
package main

import (
    "fmt"
    "log"
    "time"
    sonnetdb "github.com/sonnetdb/sonnetdb/connectors/go"
)

func demo() error {
    connection, err := sonnetdb.Open("./go-cpu-demo")
    if err != nil { return err }
    defer connection.Close()
    if _, err = connection.ExecuteNonQuery(
        "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)"); err != nil {
        return err
    }
    if _, err = connection.ExecuteNonQuery(
        "INSERT INTO cpu (time, host, usage) VALUES " +
        "(1710000000000,'edge-1',0.42),(1710000001000,'edge-1',0.73)"); err != nil {
        return err
    }
    result, err := connection.Execute(
        "SELECT time, host, usage FROM cpu ORDER BY time LIMIT 10")
    if err != nil { return err }
    defer result.Close()
    deadline := time.Now().Add(5 * time.Second)
    for row := 0; row < 10; row++ {
        if time.Now().After(deadline) { return fmt.Errorf("read budget exceeded") }
        ok, err := result.Next()
        if err != nil { return err }
        if !ok { break }
        ts, err := result.Int64(0)
        if err != nil { return err }
        host, isNull, err := result.Text(1)
        if err != nil { return err }
        usage, err := result.Double(2)
        if err != nil { return err }
        fmt.Println(ts, host, isNull, usage)
    }
    return nil
}

func main() {
    if err := demo(); err != nil { log.Fatal(err) }
}
```

getter 应按实际列类型与 NULL 合同使用。`defer` 让 result 先于 connection 释放；生产代码还可显式检查关闭错误，finalizer 不能代替确定的生命周期管理。

这里的墙钟判断发生在调用之间，不能硬中断已经进入 Native 的同步调用。需要严格操作截止时间时，应按当前远程超时或隔离运行方式设计，不应仅凭 Go deadline 宣称引擎计算被取消。

### database/sql 入口

包初始化会注册名为 `sonnetdb` 的 driver：

```go
db, err := sql.Open("sonnetdb", "./existing-go-data")
if err != nil { return err }
defer db.Close()
db.SetMaxOpenConns(1)
db.SetMaxIdleConns(1)
```

这个片段需要导入 `database/sql` 和 connector，位于返回 error 的调用函数内。示例限制为一个本地连接，避免未经验证地并发打开同一个目录。`sql.Open` 的成功也不代表 SQL 或权限已经验证。

当前 driver 明确拒绝 SQL 参数数组和事务。`Prepare` 保存 SQL 字符串，并不表示底层具有 prepare/bind/step ABI。不要直接套用 `db.Query("... WHERE host=?",host)` 或 `BeginTx`；对外部输入应采用白名单、可靠的字面量处理或支持参数绑定的其他入口，不能任意拼接。

### Bulk、KV 与 Document

在仍打开的直接连接上可以提交有界 LP 批次：

```go
written, err := connection.ExecuteBulk(
    "cpu,host=edge-2 usage=0.81 1710000002000\n" +
    "cpu,host=edge-2 usage=0.86 1710000003000",
    sonnetdb.BulkOptions{Measurement: "cpu", OnError: "failfast", Flush: "async"})
if err != nil { return err }
fmt.Println(written)
```

`OpenKV` 与 `OpenDocumentCollection` 有各自句柄和 JSON/二进制合同。各自 `Close`，不把 SQL driver 的能力推断为完整跨模型事务。native 错误是线程局部状态，应使用当前方法返回的 error，不跨 goroutine 延后查询 LastError 拼接诊断。

### cgo 部署前提

当前代码按 build tag 面向 cgo 的 Windows/Linux。需要 Go 1.22+、适配 cgo 的 C 工具链和匹配架构的 Native 库。不能默认 `CGO_ENABLED=0` 仍可运行，也不能用“机器有 MSVC”自动证明 Windows cgo 链接完成。

Linux 的源码路径示例：

```bash
cmake -S connectors/c --preset linux-x64
cmake --build artifacts/connectors/c/linux-x64
cd connectors/go
native="$(realpath ../../artifacts/connectors/c/linux-x64)"
CGO_ENABLED=1 CGO_LDFLAGS="-L$native" LD_LIBRARY_PATH="$native:${LD_LIBRARY_PATH:-}" go run ./examples/quickstart
```

这是读者的复现入口，本次不执行构建或写入。共享引擎避免重复实现存储格式，平台行为与错误边界仍需单独 smoke test。

参考：[Go 连接器](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/go/README.md)、[driver](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/go/driver.go)、[模块声明](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/go/go.mod)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
