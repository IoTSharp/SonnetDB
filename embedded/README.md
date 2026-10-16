# SonnetDB 嵌入式访问

M49 正在将嵌入式实现从 `connectors/` 拆至此目录。
`.NET` 直接托管访问引擎；C、Go、Rust、Python、Java 的本地绑定共用 `native/SonnetDB.Native` 一套 Native AOT 动态库。
嵌入式不包含 VB6/PureBasic，不新增多进程共享写或 32 位引擎。

当前仅迁移唯一 AOT 导出工程，并修复 SQL result 的 reader/command 所有权与逐行拉取。
旧 C 构建入口和各语言绑定尚待迁移；导出工程目前仍依赖混合 `SonnetDB.Data`，D01 尚未完成。
不能把目录迁移当作远程依赖已解除，也不能把移除 ABI 层结果副本当作 Core 端到端有界。

实施状态和待补 Frame 操作见[实施矩阵](../docs/design/m49-implementation-matrix.md)。
