# 车辆外观与车牌精确检索（M35 #309）

`VehicleObservationStore` 是 Core 的可用嵌入式 SDK，接受外部工具已经生成的车辆目标、外观向量和车牌 OCR 结果。它复用普通 Document collection、path index、KV/WAL 和对象桶，支持导入/替换、车牌精确查询、外观查询、删除及数据库关闭重开。它不下载模型、不解码图片、不执行 OCR，也不声称提供真实 OCR 准确率或车辆重识别质量。

```csharp
var store = new VehicleObservationStore(database, "traffic_vehicles");
var imported = store.Import(observation); // 外部 detector/OCR/embedding 输出
var plates = store.FindPlate("CN", "京 A-12345");
var similar = store.SearchAppearance(vehicleProfile, queryVector,
    new VehicleSearchOptions { Limit = 20, MaxCandidates = 5000 });
store.Delete(imported.Target);
```

`VehicleObservation` 复用 #306 的原对象版本、内容 hash、原图区域、timecode 和 detector profile。至少提供外观向量或车牌读取之一。`Target.Id` 继续是原对象版本和 detector profile 范围内的局部标识，不能直接当集合主键。导入先解析并补齐真实 versionId 与 ETag，随后以 bucket、key、versionId、eTag、detectorProfileId、targetId 固定字段顺序 JSON 的 SHA-256 作为 `ObservationId`，任何字段变化都产生不同观察身份。两个原图里的 `target-1` 不会互相覆盖。`GetObservationId(imported.Target)` 与查询返回的 `ObservationId` 一致；删除可用该 ID 或导入/查询返回的完整目标，源已删除也无需重新读取对象。尚未补齐 versionId/ETag 的外部原始目标不能直接用于计算 ID或删除。导入同一复合身份会完整替换原观察，包括移除旧车牌及旧外观 profile 的索引条目。原媒体与裁剪继续仅保存在对象桶。

新集合建立四个稀疏 path index：标准化车牌精确键、车辆外观 profile ID、OCR profile ID、detector profile ID。已有集合缺少这些固定索引或索引定义不兼容时拒绝打开，避免接管普通业务集合。导入以共享 Document store 的有界锁串行检查在用 profile：相同 ID 不能改变完整 appearance、OCR 或 detector 合同；模型换代应使用新 ID。直接通过普通 Document API 写入属于宿主可信管理边界，读取时仍验证文档 ID、派生键和完整合同，损坏时抛错。该 SDK 不新建模型目录或专用 WAL。

车牌键采用版本 `plate-ascii-cjk-v1`，组成是**规范化版本 + 显式签发地区 + 规范化号码**。规则仅将全角 ASCII 转半角、ASCII 字母转大写，并移除空格、全角空格、连字符和间隔点；保留 ASCII 字母数字与基本中日韩汉字，其他字符拒绝。原始输入最多 128 字符，结果最多 32 字符。不会将 O 改为 0、I 改为 1、猜测签发地区或接受编辑距离命中。不同签发地区不共用号码键，OCR confidence 只保留为来源信息，向量不参与号码相等判断。新规范化规则必须使用新版本并显式重建。

`FindPlate` 通过真实 path index 按 ID 分页，返回新鲜目标，`HasMore` 表示还有至少一个新鲜精确命中。`SearchAppearance` 先按车辆专属 profile 索引筛选，再复用现有向量距离计算有界精确 TopK；仅保留 Limit 个结果，不保留全部候选观察。它不是 ANN 路径，返回距离不是车牌号码或车辆实体身份。两种查询均返回实际读取数与跳过陈旧条目数，候选预算超限抛错，不返回看似完整的部分结果。

导入在写入前检查当前原对象与裁剪的版本/ETag、原对象 hash 及 MIME；查询再次验证来源。**Object 和 Document 之间没有跨模型原子快照**：对象可能在检查后发生变化，调用方需要结果版本合同，并在读取媒体时使用返回的版本/ETag；本 SDK 不保证查询与并发对象替换隔离。源对象删除/替换后旧派生观察不会作为新鲜结果交付，但仍占用空间，需由调用方执行 `Delete`。单目标删除由原 Document 写入路径清理全部派生索引，不删除原媒体。

默认查询期限为 10 秒，最多一分钟；默认最多 10000 个候选、100 万向量标量、100 个结果，配置上限为 10000 个候选、1000 万标量、1000 个结果。数据库内部同步锁和磁盘操作遵循原 Document/Object 合同，协作取消在有界分页和计算间检查，不是硬实时中断。单文档最多 256 Ki UTF-16 字符，向量最多 4096 维。导入期限为 30 秒。

集合属于普通 Document 资源，宿主必须配置其权限、用途、访问/导出审计、保留期限和删除策略；本 SDK 不安装 HTTP 端点或隐含授权。生产 JSON 使用 `VehicleAppearanceJsonContext`，持久包使用内部 source-generated context。本轮 #309 的合同与嵌入式入口已完成，最终定向测试 21 项通过（与 #308 合计 41/41，见 `artifacts/m35-20260918/m35-308-309.trx`），覆盖存储、精确语义、模型漂移拒绝、恢复、来源失效和预算。真实 OCR/车辆模型质量、服务端治理和自动源删除联动未包含在本轮已完成范围内。
