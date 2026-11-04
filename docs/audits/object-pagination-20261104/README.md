# 对象前缀首请求修复与隔离验证

2026-11-04（Asia/Shanghai）。本批在 `codex/object-pagination-20261104` 独立工作树实现，基于已推送的 `b6f8d60c5992cfc30870c30b239dd2cfe0e2d57d`，整合 `origin/main@5742bbe536e60dd321843c409b0f837722016c0e`。共享 `D:/source/SonnetDB` 的发布、博客和路线图改动保留。

**本批范围通过：真实大桶元数据的 ARM64 隔离副本首请求 2.339 秒，前三页九个对象与权威 latest/版本独立对拍一致；Core 566 项和真实 Server/SDK 43 项通过。** 当前生产仍运行 R1，本批没有生产部署、重启、数据库业务写入或发布包。整个 M36/M41/M42 保持未完成。

## 确认原因与实现

现场通过只读 KV get 确认 `vehicle-recognition` 的 `object-list-ready-v1` 标记不存在。旧列表因此在返回第一页之前，为整个桶清理并回填派生索引。原有两次八秒首请求失败继续保存在[原现场报告](../mulei-field-gates-20261104/README.md)及其 `objects-verified.json`，没有改写为成功。

`EnsureObjectListIndex` 现在从权威 latest 的 Base64 前缀粗定位，再按原始名称做 ordinal 精确过滤，只补齐请求范围。完成标记最后提交并持久化，续页和重开直接复用；标记绑定桶条目版本，避免同名桶重建后误用旧状态。对象写入、删除和生命周期继续与 latest 原子维护派生行，回填无需先删除已完成范围。新建空桶与完整索引标记一起原子创建。

冷打开继续构造既有稀疏目录，改为复用 `KvIndexPageReader` 的固定 64KiB 缓冲和逻辑偏移，跳过 value；正常点读仍为 4KiB。读取字节计数改为 Int64，以支持打开超过 2GiB 的元数据文件。没有改变文件布局、版本、CRC、十六键重启块或公开 API。现场冷打开仍约 237 秒，正式冷启动性能门禁继续后置。

## 分层证据

| 层级 | 结果与边界 |
| --- | --- |
| 本地 Core / 存储 | **566 PASS / 0 FAIL / 0 SKIP**，覆盖 KV、对象、旧格式/分页、备份、实例 MQ snapshot 和对象 Frame codec。新增前缀、Unicode、取消、重开和桶重建回归。 |
| 真实 Kestrel / SDK | **43 PASS / 0 FAIL / 0 SKIP**，包括旧桶首查、续页、页间删除/新增、REST/Frame、运行中 SQL 取消、对象下载取消/主动断连、分片失败后换客户端续传。 |
| 隔离恢复旅程 | 属于 Core 566：原实例目录整体移到离线路径后，单库恢复可读当前和历史对象正文并核对 SHA；MQ 单独恢复保留消息头和消费位置，继续发布及再次重开通过。单库备份仍不包含实例 `.system/mq`。 |
| ARM64 真实语料隔离副本 | **PASS**；复制来源只读，XFS reflink 修改隔离性小输入验证通过。真实元数据逻辑大小 23,814,699,967 字节，捕获 sequence `24527236`。只在副本中写派生索引和审计。 |
| 提交前门禁 | 完整 `dotnet restore SonnetDB.slnx` 及规定的 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 退出 0；Format 的 workspace 加载警告原样记录。实际提交前最终回执见 `evidence.json`。 |
| 生产 / 发布 | **NOT_RUN**：新候选未部署。原 R1 同容器、同启动时间、healthy、restart=0、OOMKilled=false；验证容器和副本均已回收。 |

## ARM64 三页结果

请求桶为 `vehicle-recognition`，前缀 `flows/2026/11/04/`，`maxKeys=3`。容器限制四个 CPU、2GiB 内存、network=none；使用现场已存在镜像中的托管 runtime 执行候选 Core。窗口开始前先完成冷打开，**每个列表请求仍为原定八秒**。

| 项目 | 实测 |
| --- | --- |
| 冷打开 | 237,351.471 ms；单独计时，未当作列表延迟或冷启动达标。 |
| 第一页 | 2,338.9011 ms，三个对象，truncated=true。 |
| 第二页 | 1.9418 ms，三个对象，游标推进。 |
| 第三页 | 1.4502 ms，三个对象，游标推进。 |
| 回填候选 | 4,443；三页实际列表候选合计 12。 |
| 独立 oracle | 扫描完整桶的 3,175,639 条 latest，仅保留最小 128 个目标键，再读取版本/删除标记，验证前三页的九个键与版本一致且无重复。未复用产品的前缀定位算法。 |
| 正常关闭 | 退出 0，有 `phase=dispose_end`；无 OOM，容器与目录删除后再次独立回读为空。 |

候选 Core SHA256 为 `e341e5d67577f456aeeacc5f3a6e71d28422d34f40bfff10c0eadd2a4c90f88b`。探针和完整依赖闭包的 SHA 在 `probe-bundle-r6.json`；同一闭包用于 r7。这里是实际语料、实际 ARM64 上的嵌入式 Core 验证，真实 Server/SDK 是单独的本地门禁，不能合并表述为新 ARM64 Server 已部署。

## 失败保留与可重跑入口

修复前新增三个回归均失败，原 TRX 保留。Server 夹具最初误用了不可见的内部批次及不存在的 SDK 会话列表方法，已改为公开 KV 删除和实际服务端 store 观测。下载夹具还需在客户端确实报告 download progress 后取消，并恢复后续真实写入，让 HTTP/1.1 半关闭可被观测；最终同时验证 RequestAborted、服务端退出、旧文件保留、临时文件清理及后续完整下载。

现场尝试 r2 因原定 2GiB 复制预算提前退出；确认 23.6GB 段和 XFS 后才采用通过修改隔离验证的 reflink。r3 缺运行时依赖，r4/r5 在冷打开预算内未进入列表，r6 冷打开完成后只剩两秒进程预算；这些均保留原失败和资源清理记录。r7 只为独立验证进程留足冷打开、请求、对拍和关闭时间，八秒请求窗口没有延长。原 R1 冷打开未取得完整同副本对照，保持 `DEFERRED`，不计算新旧加速倍数。

发送远端脚本前增加 ASCII guard；业务和程序集通过 Base64 传递，防止本机代码页破坏 SSH stdin。首次脚本解析失败发生在远端 Python 执行之前，没有创建副本或容器。

原生 SSH 的 stdout 直接继承了执行器句柄，最初 `Out-String` 产生空本地文件；这些文件未被计为有效证据。r2～r7、只读检查和最终清理原文均从本会话工具结果的 stdout 恢复，并记录工具 callId、日志行摘要和原文字节 SHA，见 `stdout-provenance.json`。新增进程边界捕获包装器并以原生 Cordonix stdout 验证，避免再次将空捕获当作 JSON 回执。

可重跑工具见 [验证工具说明](../../../eng/validation/object-prefix-probe/README.md)及本目录 `compare-field.ps1`。应先复制脚本到新建证据目录，构建工具，将输出目录全部运行时 DLL、deps/runtimeconfig JSON 打为该目录的 `probe.zip`，再以 PowerShell 7 运行脚本；它只走 `mulei-direct-target:22022` 和批准的 stdin wrapper，副本路径固定为本任务独占 validation 子目录。原始 TRX 以 gzip 保存，其未压缩 SHA、机器 JSON、源码 SHA 和全部尝试清单见 `evidence.json`。本机完整未压缩日志保留在 `artifacts/object-pagination-20261104/`。

## 继续保留的边界

完整高变更率/全桶分页、multipart 跨进程及断电未知结果、独立对象卷和实例 MQ 的现场恢复/RPO/RTO、真实九模型旅程、目标 NativeAOT、固定硬件容量/冷启动性能、真实语料质量及完整新候选 168 小时继续保持 `NOT_RUN` / `DEFERRED`。本轮本地恢复和换客户端续传不能替代这些门禁；生成的单元测试数据只用于合同回归。
