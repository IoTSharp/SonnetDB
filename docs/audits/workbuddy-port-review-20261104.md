# WorkBuddy 重启端口归属复核（2026-11-04）

PR #390 的隔离标签原先可绕过端口校验：URL 指向 18580、带标签容器发布 18581 时，验证器仍会重启后者并从未重启的 18580 读回，产生错误的恢复 PASS。现在必须匹配 `127.0.0.1` 的 TCP 主机端口；标签不提供豁免。后续重启仍固定 inspect 返回的 64 位容器 ID。

`python -B eng/test-workbuddy-restart.py` 已通过一个测试中的六组子场景：错配端口拒绝、正确端口接受、无标签但正确端口接受、公网绑定拒绝、UDP 拒绝和错误镜像拒绝。拒绝场景断言不会执行 restart 或健康读回。此测试模拟 inspect/restart 结果，不连接 Docker；真实 Docker 持久化及 WorkBuddy 宿主安装仍需独立验证。

健康等待使用单调时钟，最多 60 次、60 秒探测窗口，每次 HTTP 另受请求超时限制。没有改动产品超时或生产实例。

本分支只包含端口归属修复、回归和交接。提交前必须在最终树执行完整 `dotnet restore SonnetDB.slnx` 及 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`；实际进程退出与清理回执保留在本机任务记录。发布协调聊天统一合入，不在本聊天合并 main、移动标签或发布。
