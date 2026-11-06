# GH-Issue #395 剩余合同与交付登记

日期：2026-11-06（Asia/Shanghai）。基线 `bee8b35ce9937125ec5e6681abf46f6cba68432a`，接续已合入的固定标量与固定 OwnsMany 元素实现。本文件记录完整母 issue，不能用任一源码切片提前关闭。

## 实现前冻结矩阵

| 形状 | 目标合同 | 验证状态 |
| --- | --- | --- |
| OwnsOne / Complex 固定原生标量、根/嵌套 OwnsMany 固定元素标量 | 保留已合入实现；单语句，参数化路径和值 | 既有证据；本轮须回归 |
| Guid / DateTime / DateTimeOffset / DateOnly / TimeOnly / TimeSpan / 二进制 / decimal / enum 与 converter | 常量/闭包值通过模型 JsonValueReaderWriter 编码，包含可空值；不能直接将 CLR 数据库类型当 JSON 表示 | 本轮实现/验证 |
| OwnsOne / Complex 对象、数组属性、OwnsMany 集合及固定元素对象子树 | 基于模型名称、标量编码器和拥有关系手写 Utf8JsonWriter；参数化片段，经服务器 json_set_value(..., TRUE) 原子替换；根容器单独处理 | 本轮实现/验证 |
| 同列多 setter / 覆盖父子路径 / 重复路径 / 普通列混合 | 按声明顺序，RHS 读取更新前行；无 SELECT 回写 | 本轮扩大验证 |
| 缺失、null、数组越界、Unicode/转义、大整数原文 | 可创建缺失末端；缺失父级、错误父级和越界使整条语句失败，不产生半更新；目标外未知内容原文保留 | 本轮扩大验证 |
| sync / async / embedded / REST / 原生 HTTP2 Frame | 真实入口、事务内外可见性/回滚/重开、显式 Token/rowversion 与并发影响行数 | 本轮扩大验证 |
| 动态/负数索引、条件元素目标、任意 LINQ 集合投影 | 写前明确拒绝；不存在具有相同语义的服务器表达式时不得扩大 | 拒绝合同须回归，不宣称支持任意表达式 |
| primitive 元素目标、集合元素 RHS | 既有拒绝；仅在新翻译和原子性证据通过后扩大 | 待新证据，不暗改既有合同 |
| 公开新 Provider + 匹配 Server/依赖 + 独占空缓存消费者 + 真实应用 | 必须有共同已提交源、准确新版本、包/DLL/SQL/服务身份；ProjectReference 和旧公开 5.0.0 均不能替代 | 尚未发行/验收 |

EF Provider 现有运行时模型属于非 NativeAOT 边界；生产 JSON 只允许模型编码器/手写读写器/source generation，无反射 JsonSerializer 回退，无新增动态编译，无 IL/AOT suppression。Core/Server/CLI 的 AOT 门禁独立验证。

## 分工和发行协调

- 本聊天 `01a1af4a-1732-79f3-bba6-c9a78edb07e1` 负责 #395 翻译、JSON 回归、候选及 #392/#395 后续 Provider 发行唯一协调；专属分支 `codex/issue395-complete-20261106`。
- #392 聊天 `01a1af49-10c1-7831-b3c5-75e976049f56` 独立负责 EF Migration 强杀、DDL 恢复和库存实际应用迁移/回退。不会修改 JSON 翻译源，本聊天不修改 Migration 源。
- 双方在 `D:/source/SonnetDB/artifacts/sonnetdb-heavy-20261106.lock` 以 FileShare.None 串行重负载，DOTNET_PROCESSOR_COUNT=2，MSBuild -m:1，禁用持久构建服务。
- 共同源、依赖与候选版本须双方准备后准确冻结；正式发布尚未执行，必须核实当前明确授权。不覆盖 5.0.0，不改 Secrets，不部署生产。

## 执行证据

原始运行、PID/启动/argv/父 PID、TRX 与失败/成功日志保留在本工作树 `artifacts/issue395-complete/`。最终状态随实际验证更新；此登记不等于实现 PASS 或公开发行完成。

### 本轮实现与拒绝合同

- 已实现 OwnsOne / Complex 对象、primitive 数组、根/嵌套 OwnsMany 集合与固定元素对象的子树替换，包含整根容器；替换使用模型名称、拥有关系和标量 JSON 编码器，手写 Utf8JsonWriter，并作为参数交给服务器单条 SQL。没有客户端 SELECT 读改写、反射 JSON 序列化、新动态编译或 IL/AOT suppression。
- 非原生类型的常量/闭包赋值通过模型编码器处理。真实矩阵包含 Guid/Guid?、DateTime、DateTimeOffset 的 offset/ticks、DateOnly、TimeOnly、TimeSpan、decimal、byte[]、enum-to-string 与自定义 string converter；nullable 值写为 JSON null。
- 固定 primitive 集合元素目标与 JSON 集合中原生标量元素右值已取得新翻译证据；路径从模型/EF 已标注片段绑定，目标及右值路径均参数化。支持可翻译原生标量运算，RHS 读取更新前行。同列整根/子树/叶子、重复和父子覆盖按声明顺序执行。
- 动态/负数索引、生成条件目标的 ElementAtOrDefault、任意集合 LINQ 投影、非原生类型/converter 的逐行右值保持写前拒绝。非原生 CLR 表示或任意 converter 的应用运算没有通用等价服务器表达式，不能用客户端回退伪装支持。普通 JSON 查询入口的既有边界不因专用 ExecuteUpdate 右值翻译自动扩大。
- 单语句仍限单一关系表、无分组/排序/分页、最多 64 setter、64 个 path 片段和 1024 字符。运行时参数编码有 8 MiB/64 层/节点/协作式时间与取消预算，Core 原有 8 MiB 输入/输出、100000 节点、10 秒与 SQL 取消保持。EF 本身仍是独立非 NativeAOT 边界；Core/Server/CLI 的适用 AOT 门禁须单列。
- 并发需调用者显式携带 Token/应用维护的 RowVersion，并检查实际影响行数。数据库不替应用自动递增该字段。缺失父级、错误父级、数组越界和不适用叶类型使跨行语句整体失败，不能先应用某个 setter 或某一行。

### 已取得的本机证据（未完成公开交付）

- 修复前新增四项真实用例 **4 FAIL**；第一次实现 **1 PASS/3 FAIL**，修复 EF 的 Complex 元数据绑定后 **4/4 PASS**。原始 before/after-v1/after-v2 回执和 TRX 保留。
- 扩大首轮 **60 PASS/9 FAIL**，失败集中于集合包装/元素右值；修复 MaterializeCollectionNavigationExpression 与 Queryable 元素绑定，并为 RHS 建立参数化 JSON 路径后，失败组 **9/9 PASS**。编译期 nullable 诊断与缺失 SQL 类型映射的失败均保留，未压制警告。
- ExecuteUpdate 扩大 **90/90 PASS、0 FAIL、0 SKIP**（`all-update-v1.trx`）。覆盖三协议、同步/异步、参数/单 SQL、父子/整根顺序、缓存参数刷新、模型名称/转义、原行右值、显式 RowVersion 的真实并发 0/1 竞争、事务内外可见性/回滚、跨行拒绝与重开。
- 完整 EF 首轮 **370 PASS/57 FAIL、0 SKIP**（`full-ef-v1.trx`）：51 项为新增 DateTimeOffset 暴露未修改字符串的 `+` 被重新编码为 `\u002B`，6 项为旧原始 INSERT 缺新增显式 RowVersion 初值。保留全部严格原文断言；Core 改为有界复制已验证字符串 token，保留 surrogate 块边界，并补 3 项 Core 词法回归；测试模型只给 RowVersion 初始默认 0，仍由应用维护版本。
- Core JSON 扩大 **101/101 PASS、0 FAIL、0 SKIP**（`json-core-v1.trx`），包括既有精确 UTF-8/输出/编码 scratch、节点/时间/取消和整语句原子失败合同。完整 EF 修复后重验、最终 restore/规定 Format、包消费和精确远端 CI 仍需各自取得结果。
- 独立产品补货应用位于 `eng/probes/issue395-json-app`，仅 PackageReference；分进程 seed/update/verify，包含 SKU+RowVersion、策略对象、标签数组、供应商元素、Guid、带 offset/ticks 的时间、未知 JSON 原文、回滚和重开；额外 verify-rejected 用于旧包负控。当前写入源码不等于该应用已构建或新包已消费。
- 后续完整 EF `full-ef-v2.trx` **424/427 PASS**，仅新增 OwnsMany Guid/converter 标量目标未带 IProperty 的 3 项失败；使用目标实际 JsonValueReaderWriter 补齐编码，并对只含嵌套对象的容器做有界模型探针，新增 **7/7 PASS**（`boundary-v1.trx`），包括 64 个 setter 的真实顺序及第 65 个的写前拒绝。
- 扩大完整 EF `full-ef-final.trx` **434/438 PASS**：3 项新增 root=null 单独赋值被 SQL 生成器拒绝 EF 优化后的 NULL 常量，已修白名单；另一项旧 DatabaseMigrate 用例在清理 rowstore 目录出现 IOException，保留原始失败，不推定根因。最后短回归 `last-failures-v1.trx` **10/10 PASS**，包含 root=null/父null后子写入整条拒绝、primitive/RHS负数拒绝和该旧迁移用例。438 完整零失败仍待本轮最终复验，不能用短回归代替。
- 一次短回归调度曾在共享锁的 300 次/10 分钟上限退出，未创建 dotnet 测试进程或 TRX，因此是 **NOT_RUN**；只读确认持锁者为 #230 Core 全套，未终止他方进程。之后 #392 独占缓存消费完成并释放后才运行上述 10 项，避免叠加负载。
- NuGet.org V3 index 匿名实时回读仍以 **5.0.0** 为最新 Provider，5.1.0 尚不存在。现有发行政策明确七天 scheduled Parity 是 non-blocking observation，实际门禁要求相同 source SHA 的当前候选 workflows/原始 Parity/发行物；未修改或绕过门禁。共同 5.1.0 仅暂定，尚未冻结或发布。
- 最终产品实现冻结后，完整 EF **438/438 PASS、0 FAIL、0 SKIP**（`full-ef-final-v2.trx`），Core JSON **102/102 PASS、0 FAIL、0 SKIP**（`json-core-final.trx`）。新增并发用双独立工作线程、10秒Barrier起跑、显式RowVersion谓词/递增与0/1影响行数，覆盖embedded/REST/原生Frame。旧迁移清理IOException保留为原失败；最终同一用例与完整套通过，不推定其唯一根因已查明。
- 本轮相对基线新增50个EF测试用例、4个Core词法/Unicode回归，另将原明确拒绝的固定primitive元素和类型常量改为经过实际翻译/原子性验证的正向用例；动态/负数/条件/非原生逐行RHS等仍有零SQL拒绝回归。最终restore/规定Format、独立包应用构建与公开发行按后续实际回执记录。
- 产品补货应用已在仓库外独占 NuGet.org-only 全新 packages/http-cache 下 restore/build/seed 成功；旧公开 **5.0.0 的业务 ExecuteUpdate 真实 FAIL**（LINQ JSON 子树翻译拒绝），随后独立 `verify-rejected` 进程 **PASS**，RowVersion/库存/策略/标签/供应商均未被写入。不是新 Provider 正向验收。
- `consumer-control-identity.json` 核四包 `SonnetDB`（ADO DLL 为 SonnetDB.Data）、Core、Protocol、EntityFrameworkCore 均为5.0.0，source均 `https://api.nuget.org/v3/index.json`，repository commit均 `84433f9edcceb84605520ebecc4b2879e3c4aa79`；唯一 packageFolders 是独占目录，四个 cache/app DLL SHA逐项一致。身份核对器第一次只因目录尾斜线表示差异拒绝，改为解析后的绝对路径比较后通过，没有放宽包/来源/版本/DLL合同。
- 最终完整solution restore退出0（`restore-final-v1.result.json`）；规定Format、当前源候选/新公共缓存、精确PR CI和匹配Server尚需按后续回执核实。原始消费者目录与日志保留，不将负控升级为新包成功。
- 首个源提交/PR为 `7e9fb974ad4df52e8b1d8ac7a07764fb5eaa9955` / [#412](https://github.com/IoTSharp/SonnetDB/pull/412)，提交前完整restore和规定Format均0。LOCAL_ONLY `0.0.0-issue395.local.20261106.1` 四包以该提交构建，独占candidate feed+新缓存的补货应用restore/build/seed/update/verify五步骤全部PASS，4包nuspec源提交/cache-app DLL SHA一致。该结果仅绑定7e9单片，不等于共同5.1或公开包。
- #411已合入main `859c69618940cc50e1aff0941736876981103f4a`。同步到#412的共同工作树仅CHANGELOG冲突，保留双方条目；HANDOFF为本项6060字节前置+原始896596字节+main的4417字节追加，原始区间SHA仍为3d55ec7c93c7575aa9c6016e925faa60863a468780505b8998f8ad5946faa520，没有清洗历史UTF8或丢失对方记录。
- 共同工作树完整EF **438/438**、Core JSON **102/102**再次PASS、0失败/跳过（`joint-ef.trx`、`joint-core-json.trx`），完整restore0；规定Format及新merge head远端CI继续逐门核实。共同候选/发行迁移说明见 `docs/releases/5.1.0-provider-candidate-20261106.md`，明确旧5.0部分提交不能靠升级Provider自动回滚，须停写备份/核schema数据历史、恢复一致备份回补或经审查修复。
- 共同树规定全solution Format已退出0（`joint-format.result.json`，287.8秒），无实现变更后提交main同步merge。新精确head的CI/候选仍独立验收，不能借原7e9或#411单片CI关闭母issue。
