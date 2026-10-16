# 整合后构建接线修复（2026-10-17）

人工整合提交 `d65e7884597d37ef18690b4eb451717349438a2c` 已进入线上 main，M49 与在线演示远端分支全部是 main 祖先。本次处理新 SHA 实际工作流发现的两个遗漏；不恢复已暂停的 Issue 定时任务。

Docker Publish `38940523381` 在发布 Server 时找不到 `SpanReader`/`SpanWriter`，日志明确显示跳过 `/repo/src/SonnetDB.Protocol/SonnetDB.Protocol.csproj`。Dockerfile 现在在 restore 层与源码层分别拷贝新 Protocol 项目，同时补齐 restore 阶段已存在的 MQTT 源生成项目引用。镜像工作流增加 Protocol 路径触发。修复前后按真实 ProjectReference 递归核七项目闭包，现均有 restore 输入和源码父目录 COPY；原缺失和校验脚本首次未处理父目录 COPY 的误报分别保留。

Connectors Release `38940523472` 的 Windows x64 在 Rust 链接时 `LNK1181` 找不到 `SonnetDB.Native.lib`，Linux x64 与 Windows x86 作业没有该失败。CMake 已迁到 embedded/native，但发布工具仍首先搜索旧 connectors/c/native；随后选中仅复制 DLL 的示例目录，导致链接搜索目录缺配套导入库。发布工具改为新 canonical Native 目录，Rust 独立默认路径同步迁移，CMake 的源码/项目依赖增加 Protocol，避免后续改协议却复用旧 AOT 产物。其余构建入口已按同类路径搜索复核。

本机实际验证：

- VS2026 Professional 18.5、MSVC14.50、bundled CMake4.2.3；CMake配置和完整Release构建实际exit0，发布新的Windows x64 NativeAOT库并链接两份C探针。
- C quickstart实际运行exit0，result-lifetime探针实际运行exit0，覆盖Int64/UTF-8/NULL/提前释放/父连接关闭/EOF/重开。数据库与TEMP均在D盘独占短目录。
- connectors-release工具Release构建零警告错误。调用编译后工具真实的GetNativeLibraryPath、CreateNativeEnvironment和CopyNativeRuntime，选择embedded/native下同一目录的DLL/LIB，打包后逐项SHA256匹配。
- 本机Java、Go、Rust未装；未把C链接或路径探针当这些消费者已通过。修复后的完整Docker构建已启动，但基础镜像下载较慢，当前不声明本机镜像成功。新提交的精确线上工作流分别回读。
- 本次只修改构建接线，无生产运行时语义变更；既有Core7606、Server1430、EF295、Identity18、Client195、Native10及前端469全通过的组合验证仍按其原SHA/运行证据记录，不追加或重算为本次新执行。

原失败完整日志、退出码、Docker闭包、Native产物SHA及构建命令在本机 `artifacts/integration-20261017/`。最终待提交树须完整实际通过规定restore/Format后才commit；新CI与远端回读继续写入该目录，避免为状态回读产生无限提交。旧树、stash和parity-results引用全部保留。#248在最终精确CI成功后再逐项回复关闭，#247/#245仍为未实现准备材料。
