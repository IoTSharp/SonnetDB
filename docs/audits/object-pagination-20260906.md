# M36 #323 / OBJECT-001 对象有界分页

记录日期：2026-09-06。基线为已合入 main 的 `e06b7b9da7a0bec47b3574ab99ccbf993eccd58f`，本次验证在 `D:\source\SonnetDB` 的未提交工作区完成。按用户要求拉取 main 后迁入当前改动；验证时尚未提交、推送、创建 PR、dispatch、部署或发布包。验证完成后，用户另行授权提交并推送本次改动。

## 实现与合同

- `SndbObjectStore.ListObjects` 通过原有 `__object_storage` keyspace 的 `object-list-v1:<bucket>:` 派生索引定位 prefix/continuation。索引只包含当前可见对象的 key 和 version ID，正文与权威版本记录仍使用原存储路径；没有第二套 catalog、独立存储引擎或新增 NuGet 运行时依赖。
- 排序键按 UTF-16 code unit 的大端可排序字节编码，对应 `StringComparer.Ordinal`，包括补充字符与 BMP 私用区的相对顺序；既有 Base64/UTF-8 存储 key 不作为原始 key 的排序依据。这是派生索引的逻辑键编码，未修改 FileHeader、BlockHeader 或原有小端文件格式。
- 普通 PUT、multipart 完成、删除标记随版本/latest/审计的同一 WAL 批次更新派生项。生命周期移除 latest 时，同批删除或指向替代版本；只更新旧版本 tags 时继续从权威版本记录读取详情。配额、版本列表、bucket 非空判断保留原语义。
- 原四参数 Core API、SDK 原有重载，以及结果 record 的原构造函数/解构仍保留。新增 delimiter/取消重载和 `Delimiter`、`CommonPrefixes` 属性，复用既有 source-generated JSON context。普通列表继续输出并接受 v1 continuation；分组列表输出绑定 bucket/prefix/delimiter 的 v2 continuation。
- `MaxKeys` 计算对象与 common prefixes 的合计。common prefix 按原始 key 顺序占一个名额，之后直接 seek 到其前缀上界。只在确有下一项时返回 `IsTruncated=true`，最后一页令牌为 null；跨页读取当前状态，不固定跨页快照。
- prefix 保留旧的 `TrimStart('/')` 行为。SDK 修正仅由空格组成的 prefix 被丢弃的问题。Core、REST 与 SDK 均观察取消；SDK 配置 `frame-http2`/`auto` 时列表仍复用既有 HTTP 管理路由，不新增 Object Frame opcode。

## 复杂度与预算

设 P 为请求的 MaxKeys，M 为 KV 可变及冻结覆盖层条目数，D 为磁盘索引大小，C 为本页各层实际检查的物理候选数。

- 对象元数据首次读取时按需启用 `KvOrderedOverlay` 的有序键集合，之后写入/删除增量维护。其他 keyspace 不启用此集合。首次建立和重开后的覆盖层恢复成本为 O(M log M)，此成本与持久化对象列表索引恢复分开；不是每页重复排序。
- 有序覆盖层每个候选通过红黑树 seek，成本 O(C log M)；磁盘使用已有二分定位与顺序范围读取。正常页只读取至多 P+1 个可见索引候选，每次物化至多 256 条索引记录；分组每次读取一个候选并跳过整个前缀。
- 各层 tombstone、覆盖旧磁盘项和 merge lookahead 都计入物理候选预算。每页预算为 `max(1024, 8 * (P + 1))`，第一个超预算候选立即停止，抛出 `object_list_scan_budget_exceeded`。HTTP 映射为 503；不返回半页，不生成推进令牌。等待对象元数据 keyspace 的检查点/压实完成后可重试相同请求。
- 因此大量未压实删除不能把页工作放大到全桶。预算覆盖候选访问；树定位另有对数成本，返回 DTO 内存仍与本页对象元数据字节数有关。不能把“P+1 个可见候选”误称为所有状态下只做 P+1 次物理 I/O。
- bucket gate、KV 锁等待、物理候选、重建页和检查点背压观察取消。单次列表具有两分钟期限；重建清理/填充各最多一百万个 256 项页。已开始的 WAL append/fsync 完成自身提交路径，不能强制中断操作系统同步 I/O。

## 恢复边界

- 旧数据库没有 `object-list-ready-v1:<bucket>` 完成标记时，先分页清理遗留派生项，再按既有 latest 指针分页重建；完成标记最后提交。每页复用现有索引恢复预算、可取消条件批次和检查点，不一次装入全桶元数据。
- 取消、坏权威元数据或写入失败时不发布完成标记。重开/重试重新清理和重建，避免残留或缺项被当作完整索引。一次性迁移的总工作随 bucket 大小增长；它是恢复工作，不计入正常页成本。
- 正常完成的索引由原 WAL/检查点恢复，重开后的列表不重新全桶扫描。普通 PUT/删除标记的 WAL sync 故障注入验证版本、latest 和索引共同重放；这不是磁盘满、断电或真实进程强杀验证。
- ready 标记是派生格式完成标记，不是每次请求的全量完整性校验。禁止直接修改保留 keyspace 的派生行，或让不维护该索引的旧程序写入后继续信任 ready 标记；此类非正常修改需要先使完成标记失效再重建。对任意“标记仍完整但某一派生项被手工删掉”的自动检测不在本切片内。

## 本地验证

工具为 PowerShell 7.6.5、.NET SDK 10.0.400、Windows x64，复用已安装的工具。构建使用 `DOTNET_PROCESSOR_COUNT=4`、单 MSBuild worker、禁用 node reuse/shared compiler。有界 runner 记录 PID、创建时间、命令行、父进程及任务派生进程，并按身份核对回收。

最终结果如下；前序运行没有混入最终 PASS 数量：

| 层次 | 结果 | 范围 |
| --- | --- | --- |
| 最终 Core 全量 | 4189/4189 PASS，0 skipped，188 秒 | 包含全部 19 个新增分页/恢复/预算测试实例及现有 KV、对象、表、Graph 等共享引擎回归。 |
| 最终分页定向 | 19/19 PASS，0 skipped | ordinal/Unicode/编码 key、112 组分页参数差分、版本/删除标记、multipart/生命周期、共享门面、重开、重建中断、锁/背压取消、物理预算与压实恢复。 |
| 真实 Kestrel 对象/传输测试 | 40/40 PASS，0 skipped，8 秒 | 含 3 个新增 REST/HTTP2/auto SDK 分页旅程，真实创建/上传/删除/分组/连续页和空格 prefix；列表均走既有 HTTP 管理路由，不声称新增了 Frame list opcode。 |
| Native AOT 本地发布 | Server win-x64 PASS，0 IL/AOT warning | 实际执行 `SonnetDbPublishAot=true`，生成 `native-server/SonnetDB.exe`。本次没有运行原生对象旅程，不能把发布成功当作原生运行验证。 |

最终 TRX：`object-core-budget-final.trx`、`object-budget-final.trx`、`object-server-final.trx`；命令日志：`core-budget-final.*`、`object-budget-final.*`、`server-final.*`、`server-native-aot.*`。前序 Core 4185/4185、4188/4188 和对象/KV 263/263、267/267 均早于最后的物理预算补丁，保留为过程记录。

大 bucket 采用元数据 fixture，测试不伪造 blob 上传吞吐。最终分页定向 TRX 的实测数据如下；耗时为单次本机样本，不是基准分位数：

| 对象数 | 状态 | 分组页可见候选 | 磁盘索引项 | 分配字节 | 耗时 |
| ---: | --- | ---: | ---: | ---: | ---: |
| 67 | 内存覆盖层 | 3 | 0 | 12,056 | 0.562 ms |
| 8,195 | 内存覆盖层 | 3 | 0 | 12,392 | 0.911 ms |
| 8,195 | 检查点 | 3 | 3 | 78,600 | 3.157 ms |

该页把 8,192 个 `a/` 子项折叠为一个 common prefix，再返回 `b`，用 `c` 探测下一页；普通页和深 continuation 另有候选计数断言。旧库重建成本没有混入此表。物理预算测试另构造检查点后 1,100 个真实删除标记，验证在 1,025 个候选处拒绝，并在压实后准确返回剩余对象。

源码测试入口：

```powershell
dotnet test tests/SonnetDB.Core.Tests -c Release --filter 'FullyQualifiedName~ObjectPaginationTests'
dotnet test tests/SonnetDB.Core.Tests -c Release
dotnet test tests/SonnetDB.Tests -c Release -p:BuildAdminUi=false --filter 'FullyQualifiedName~ObjectStorage|FullyQualifiedName~ObjectFrameTransportParity|FullyQualifiedName~KvObjectDocFrame'
dotnet publish src/SonnetDB -c Release -r win-x64 -p:SonnetDbPublishAot=true -p:BuildAdminUi=false
```

本机实际执行由 `artifacts/object-pagination-20260905/Invoke-Bounded.ps1` 包装并设置期限。TRX 位于该目录的 `results/`，日志和进程身份文件保留在同一目录；早期命令日志保留于原接续工作区的 `artifacts/kv-remote-20260905/object-*.log`。这些是本地忽略产物，不是远程 CI artifact。最终身份核对覆盖 58 个已记录 PID，未发现仍运行的匹配进程；新增测试的 `SonnetDB.ObjectPages.*` 独占临时目录无残留。短时 git 版本探测在身份采集前结束，没有持久化 PID 记录，其命令退出状态为 0。未启动独占 MCP 服务或下载/安装工具。

本地产物 `SonnetDB.exe` 的 SHA256 为 `872033DC1774E8D83F4D5897F179A956FD025397AE4862715B4FC12B381C58AB`，仅用于定位此次本地 AOT 产物。

## 未关闭范围

本切片只覆盖 OBJECT-001 的有界对象列表。完整 #323 的 conditional put/get、异步游标和 CLI `cp/sync --dry-run`，#322 Transfer Manager/resume/checksum/并发传输，其他 M36 模型旅程、九模型备份恢复、M20 light/full 与七天 scheduled、M42 固定硬件/ARM64/168 小时性能及生产门禁仍单独验收。没有据本机页面计数宣称完整 S3 兼容或固定硬件容量达标。
