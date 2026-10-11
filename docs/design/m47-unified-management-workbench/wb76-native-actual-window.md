# WB76 新 Native 实际窗口

2026-10-09，继续本会话已完成的 WB75（1/5）。用户要求先获取并合并线上代码；实际 fetch `origin/main` 成功，`merge --ff-only origin/main` 返回 `Already up to date`。当前双方提交为 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`、ahead/behind 0/0，`origin/parity-results` 为 `0061d6d78591fb08493f473d3231ca42303faae1`。同步属于本片前置，不另计产品任务；未 push。

新合同与证据根为 `artifacts/wb76-native-actual-20261009`，根截止 04:50Z，最多 14 个 managed 命令、1 次 actual、0 产品构建。Native 保持 900s 总预算、90s 回收保留和 1020s 外层期限。实际窗口执行前必须独审源码／工具，完成微试和语法检查，取得新鲜文件、运行时、资源和四端口准入；准入 180s 内才允许启动。

## 实现与继承

runner 只增加 `wb76` 固定证据入口、对应 `WB-76` 标签和允许列表／错误文案，以及仅用于该入口的 WebView2 `.62` 文件前置说明。六处唯一替换逆投影到 WB75 runner 原始字节完全相等：基线 SHA256 `5358D2F61BD467EA6481CC16BFDF484F294D72F98081D35E0D34CFF5E215EBB2`，新文件 `89612 B`／SHA256 `7F870005A4DD5AB12A264229F7F54DEC46822695B09FA888E0B65E9076DA682A`。独立源码复核通过，语法及实际验收分别记录。

WB73 PS14/Node41 与 WB74 guard38 直接继承，未因会话恢复重跑。原 helper、evidence projector、CIM 调用、cache、20s 整体及单查询预算、100ms 观测记账、Fresh 身份、正常退出与回收权限不变。四阶段／160 槽仅记录现有查询；非法或缺失为 unknown，不能授动作权限。

## 资源与运行时

初次 PerfOS_CPU 查询在 3s 操作期限超时，保存在 `resource-initial-failure.json`。有界 OS/CPU 5s 复核返回 CPU30%、约31GB 可用内存；这只证明准备阶段观察，不能当作实际准入。新鲜明确文件 `C:\Program Files (x86)\Microsoft\EdgeWebView\Application\154.0.4258.62\msedgewebview2.exe` 存在。历史九个 runtime 文件均保持原 hash；23 个明确依赖仅随已验 helper/evidence、新 runner 和 `.62` 路径更新，共32个逐文件 hash 条目。

`.62` 的文件准入不会选择或固定 WebView2。实际 CDP version、被核 owned tuple 的运行时路径／版本以及前置文件 SHA 必须分别观察；不从旧 `.53`／`.62` 差异推根因。复用既有 Studio/Server 产物，无新构建或 source-to-binary 等价声明。

## 验收范围与当前状态

必须通过普通 A→B 控件选择、fresh 双 barrier／PUT ack／DOM／active identity／磁盘语义，正常关闭首桌面并核所有 owned 身份和四端口释放；保留隔离 profile/data/library 后启动第二桌面，先观察普通 bootstrap GET／DOM 恢复 B，再执行普通只读 B SQL并核不同哨兵，最后再次正常关闭与严格回收。不得用 reload/API/localStorage 替代恢复，不放宽身份或 fallback 断言。

当前 actual **NOT_RUN**。新结果、phase 耗时、正常退出和 cleanup 在运行后另记；原 WB70 的失败及根后续回收仍为各自证据。静态／源码测试／构建／实际／安装／Extension Host／AOT／固定硬件／长期／发布分列，七导航、九模型、Graph Beta 和 MQ database identity／instance `.system/mq` 合同保持。

旧会话已经以失败检查点闭合WB76：第一次wrapper-micro在预算`File.Replace`的backup参数报空路径，发生在受管子进程启动前；失败调用1而持久budget0/labels空/actual0。没有micro、语法、准入、Native、restore/format或新commit通过。旧phase1超时、phase2静态PASS/04:18Z到期与原source PASS分别保留，旧根执行权随移交撤销。新根在released之后建立独立[WB77接续窗口](wb77-native-continuation.md)，不写旧工具、预算或审查收据。

## 集成边界

root 串行维护共享文档、最终树完整 restore／原 CI format、精确 owned 暂存和本地提交。并发博客及共享 HANDOFF 外来追加保留且排除本片暂存；以新已提交 HEAD blob 加明确 root 段构造 HANDOFF，不复活旧 private／八段／十五路径 pending 合同，不 whole-add／reset index。

观察到的完整进程身份与 retained handle 退出单列，不声明全生命周期捕获、whole-session orphan freedom 或 processIntegrity。固定 PowerShell7、item+wall/cancel/progress 与精确自有身份 finally 回收；保护其它会话、旧 policy Temp、WB40 runtime、缓存／交付物及 parity。无安装、部署、发布或外部发送。
