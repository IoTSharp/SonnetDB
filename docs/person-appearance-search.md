# 人员外观、步态、姿态与动作查询（M35 #308）

本切片提供 `PersonAppearanceSearch` 的**预计算候选查询 SDK**。它不运行模型、不登记身份、不持久化模板，也没有跨摄像头识别服务。ReID 的 mAP/CMC、步态及动作的 precision/recall、真实模型许可证和固定硬件证据仍待完成；合同 fixture 不计作识别质量。

每个实例只配置一个 `PersonAppearanceTask` 和完整 `PersonAppearanceProfile`，独立能力标识分别为 `reid`、`gait`、`pose`、`action`。默认 `Enabled=false`。查询必须同时通过实例的允许用途集合和宿主注入的 `IBiometricAuthorizer`，每个候选的 `Purpose` 必须与查询用途一致。普通数据库权限不隐含上述能力权限。

`EmbeddingProfile` 的 ID、provider、model、revision、维度、metric、normalization、输入模态及外发策略都必须相等；专业用途、输入布局版本和时序窗口也必须相等。步态和动作要求视频模态以及 1..600000 毫秒的窗口。不同任务、同 ID 下不同权重或预处理版本，以及“只有维度相同”的向量均被拒绝。该入口接受已经生成的向量，不调用任何本地或外部 provider。

候选复用 #306 `VisualDerivedTarget` 和 `VisualDetectorProfile`，包含原对象版本、区域以及视频 timecode。查询先冻结有限候选、完整 profile 和向量，再调用宿主提供的 `isCurrent`。宿主必须在这个可信回调中核对对象/裁剪的当前版本及对象可见性；SDK 自身不读取对象。过期、非 Ready 或来源无效的候选不会返回。返回值是模型空间中的距离，不是身份结论、匹配概率或自动决策。

宿主必须提供同步、持久、失败即抛错的审计接收器。禁用/拒绝产生 `denied`；允许的操作在检查候选前记录 `started`，完成或失败记录终态，所有记录共享 `OperationId`。终态审计失败会阻止结果交付。记录只含主体、用途、能力、计数和时间，不含原向量、候选身份或对象键。进程退出时未写终态的 started 应由宿主按其审计合同处理；此 SDK 不伪造持久审计保障。

默认最多 1000 个候选、100 万个向量标量、10 秒协作期限；配置上限为 10000 个候选、1000 万个标量和一分钟。每个向量至多 4096 维，必须有限、绝对值至多 100 万；Cosine 拒绝零向量，声明 L2 归一化时检查单位范数。取消或预算失败不返回部分结果。距离使用现有 `VectorDistance`，同距离按局部目标 ID、复合候选 ID 序数排序；默认 TopK 为 10，上限 1000。`CandidateId` 是固定字段顺序 JSON 的 SHA-256，包含 bucket、key、versionId、eTag、detectorProfileId、targetId；不同来源的同名局部目标不会误判重复。此入口不能自行解析部分对象引用，宿主应统一使用固定且完整的 versionId/ETag。

生产 JSON 使用公开的 `PersonAppearanceJsonContext`。本轮 #308 的合同与嵌入式候选查询入口已完成，最终定向测试 20 项通过（与 #309 合计 41/41，见 `artifacts/m35-20260918/m35-308-309.trx`）。持久摄取、模板删除/保留清理、服务端路由及真实模型评测未包含在本轮已完成范围内。
