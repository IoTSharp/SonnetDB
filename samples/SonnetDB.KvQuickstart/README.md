# KV Quickstart

同一程序使用嵌入式、REST 或 HTTP/2 Frame，所有 JSON 使用 source-generated context。
固定 30 秒运行期限，单条操作使用连接的 `Timeout`；不重试写入。

```powershell
$env:SONNETDB_CONNECTION = 'Data Source=./kv-quickstart-data;Timeout=10'
dotnet run --project samples/SonnetDB.KvQuickstart -c Release
dotnet run --project samples/SonnetDB.KvQuickstart -c Release -- --verify-reopen
```

远程连接改为 `Data Source=sonnetdb+http://localhost:5000/demo;Token=<token>;Timeout=10;Protocol=rest`。
`Protocol=frame-http2` 必须指向配置为 HTTP/2 的监听器，并使用 `--existing-database` 跳过 HTTP/1.1 管理面建库。
建库需要 admin；生产环境应先建库并使用 `--existing-database`，
再给应用授予目标库读写权限，并从环境或凭据服务提供连接，不把 token 写入源码。

程序保留 `device_cache/site_east:device_001`，以 CAS 更新 JSON 状态及应用操作 ID。
NX 未应用和 CAS 冲突都是确定结果；网络断开、取消和存储 I/O 失败可能留下未知提交结果。
应用操作 ID 便于核对当前状态，但不是服务端幂等键、租约、分布式锁或持久操作历史。
原子取删之后若响应丢失，不能通过再次取删恢复旧值；需要可靠消费时应使用消息队列合同。

`--verify-reopen` 用于程序退出或 Server 正常重启后核对持久值、TTL 和删除记录。
它不代表断电耐久、数据库备份、七天稳定性或生产验收。

Native AOT 可达验证：

```powershell
dotnet publish samples/SonnetDB.KvQuickstart -c Release -r win-x64 -p:KvSamplePublishAot=true
```
