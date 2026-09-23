# ServerRelay 跨实例活跃续流

M27 #340 的共享日志入口允许客户端将同一运行的订阅连接转到另一 Server 实例。原实例继续执行 provider 和本地工具，其他实例读取相同的事件、sequence 和 cursor。所有者退出后，存活实例将已记录的事件封闭为唯一 `error`/`done`，后续只重放该终态。

## 部署配置

在参与续流的实例上配置同一个绝对文件路径：

```json
{
  "SonnetDBServer": {
    "DataRoot": "D:/sonnetdb/instance-a",
    "Copilot": {
      "ServerRelayJournalPath": "D:/sonnetdb-relay/relay.json"
    }
  }
}
```

环境变量为 `SONNETDB_SONNETDBSERVER__COPILOT__SERVERRELAYJOURNALPATH`。未配置或仅为空白时继续使用 `<DataRoot>/.system/copilot-relay-journal.json`；相对路径在启动配置绑定时拒绝。

各实例必须使用各自的 `DataRoot`，共享的仅是 journal 及其旁边的锁文件。路径所在文件系统必须正确实现跨进程独占文件锁和同目录原子替换；网络文件系统的锁语义需在部署环境验证。所有参与实例应属于同一身份、权限和数据库路由信任域，采用一致的授权配置。每次订阅仍先经过目标实例的身份及数据库访问校验，再校验 owner、database、请求指纹与 cursor。

## 执行与故障合同

- 读取 journal、检查身份并创建运行在同一个文件锁事务内完成；同一身份/runId 只会有一个 `Created` 和一个 provider/工具执行者。
- 每个活跃运行持有独占租约。其他实例可读取历史前缀并以 100 ms 间隔等待追加事件，无须等待运行完成。订阅有事件数、迭代次数和墙钟上限，并响应取消。
- follower 取消只结束自己的订阅；原始执行请求取消仍沿用现有终止语义。没有新增跨实例停止命令。
- 所有者正常关闭或异常退出后，存活实例保留已确认的工具结果，追加稳定的失败终态，不重新调用 provider 或工具。重放不会改变终态 payload 或 sequence。
- 损坏或不可读取的持久日志拒绝新运行，避免把缺失历史误当作首次请求。仍遵循既有 64 个活跃、64 个重放、2048 个身份和单运行 256 个事件/4 MiB 上限。

这项合同不共享 Copilot 会话库、数据库写入状态或 provider 内部状态。`IChatProvider` 当前只有完整调用接口，没有恢复执行协议；因此 owner 故障后的透明继续执行、跨实例取消转移、共享会话高可用仍不在本项完成范围。真实 IdP、双网、公网 CSP/CORS、模型质量和部署长稳使用各自门禁。

## 验收入口

- `CopilotServerRelayMultiInstanceTests`：并发 claim、租约隔离、续流/cursor、故障终态、取消、损坏日志和资源回收。
- `CopilotServerRelayMultiHostTests`：两个独立 Kestrel/DI 宿主通过生产配置共享日志，验证 NDJSON/SSE 与取消后的再次订阅。provider 为确定性测试实现。
- `tests/SonnetDB.Tests/Copilot/scripts/test-m27-server-relay-multi-instance.ps1`：两个真实 Server OS 进程、独立数据目录、loopback provider、活跃工具结果续流、hard-kill 和失败重放。报告记录 provider 次数、二进制/脚本 SHA-256、进程身份与清理结果。

实际执行结果见 [2026-09-23 闭环记录](audits/relay-multi-instance-closure-20260923.md)。本机证据不替代跨机器共享文件系统和真实模型验收。
