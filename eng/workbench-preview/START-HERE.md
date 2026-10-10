# SonnetDB Workbench Preview 1 构建候选

本包只包含 Windows x64 NativeAOT Server 和同源 Web。产品状态 NOT_READY；首次部署、实际用户旅程及发布门禁须由 P05～P08 单独验收。Graph 仍为 Beta；document、KV、fulltext、vector、object、MQ、Graph 的专用读取按 R1 延期。

先通过独立 manifest 和 SHA256SUMS 核 ZIP。解压到空目录（例如 `C:\SonnetDB\preview\package`），用 PowerShell 7 执行 `./Start-Preview.ps1 -VerifyOnly`；执行 `./Start-Preview.ps1` 后访问 `http://127.0.0.1:5080/admin/`，按首次设置页建立自己的管理员。HTTP 5080 与 Frame HTTP/2 5081 仅监听环回；可选协议与外部连接默认关闭。目录权限、端口占用、首次设置、停止重开和错配拒绝的实物验收属于 P05。不要外网暴露或混入旧文件。

数据目录是包目录的相邻 `../data`，与二进制分开。此包不预置数据或凭据；Server 的完整 API 与现有授权仍存在，Preview profile 不构成多租户沙箱。数据库备份不覆盖实例 `.system/mq`。

只开放有界 SQL、关系表、measurement 与一次授权批准的关系表单行插入；取消不等于回滚，未知写结果不自动重放。共同预算 30 秒、1000 行（关系 200、measurement 500）、响应/保留/导出各 4 MiB、输入 64 KiB；估算预算不代表物理堆硬上限。

这是 P04 包内启动说明，P06 的升级/回退、已知限制、反馈与公开发行说明尚未完成。不要让旧 Server 打开新数据目录；不要把本包视为正式版、安装器或全套发行。许可证原文与依赖版本见 `licenses/`、`dependency-licenses.json`、`THIRD_PARTY_NOTICES.md`。
