/* Each task is a separate review surface. Fixtures never issue requests or write data. */
(function () {
  'use strict';
  const catalog = window.M47_CATALOG;
  if (!catalog || catalog.sections.length !== 7 || catalog.models.length !== 9) throw new Error('Load the M47 catalog before task details.');
  const pages = catalog.sections.flatMap(section => section.pages).concat(catalog.models);
  if (pages.length !== 39) throw new Error('M47 task details expect exactly 39 pages.');
  const byId = new Map(pages.map(page => [page.id, page]));
  const f = (label, value, help = '', kind = 'input') => ({ label, value, help, kind });
  const i = (label, value) => ({ label, value });
  const task = (title, view, description, fields = [], columns = [], rows = [], action = '查看设计说明', status = 'existing', inspector = []) => ({
    title, view, description, status, fields, columns, rows, action,
    inspector: [i('连接', 'Factory / Local'), i('数据来源', '静态设计示例，原型不执行请求'), ...inspector]
  });
  const planned = (title, description, fields = [], inspector = []) => task(title, 'notice', description + ' 此能力仍在规划；当前原型拒绝调用后端或执行操作。', fields, [], [], '查看规划边界', 'planned', inspector);
  const base = (id, view = 'table', status) => {
    const page = byId.get(id);
    return task(page.tabs[0], view, page.intro, page.fields || [], page.columns, page.rows, page.primary, status || page.status, page.inspector);
  };
  const auditColumns = ['时间', '对象', '动作', '状态', '来源'];
  const mappingColumns = ['源字段', '目标字段', '类型', '校验'];

  const details = {
    measurement: [
      base('measurement', 'table', 'existing'),
      task('SQL', 'sql', '在 Telemetry 上下文编写 SQL，保留 TAG/FIELD 原始拼写。', [f('SQL', 'SELECT DeviceID, AVG(Temperature) FROM Telemetry GROUP BY DeviceID;', '只展示编辑输入，不运行请求', 'textarea')], ['DeviceID', 'AVG(Temperature)'], [['Pump-01', '67.6 · 示例'], ['Pump-02', '66.0 · 示例']], '打开 SQL 输入', 'extension', [i('数据库', 'factory'), i('名称', 'DeviceID 原始拼写')]),
      task('图表', 'chart', '按时间与设备比较温度；图线来自本页固定样例，不是实时聚合证据。', [f('时间窗', '2026-10-04 09:00–10:00'), f('FIELD', 'Temperature'), f('分组 TAG', 'DeviceID')], ['time', 'Pump-01', 'Pump-02'], [['09:00', '67.4', '65.8'], ['09:05', '68.1', '66.2'], ['09:10', '67.8', '66.0']], '预览图表配置', 'extension', [i('数据库', 'factory'), i('空值', 'NULL 不补零')]),
      task('轨迹', 'notice', 'Telemetry 示例未提供坐标字段。轨迹结果只能在真实查询返回坐标/时间列后启用；当前不能绘制设备轨迹。', [f('经度列', '未选择'), f('纬度列', '未选择'), f('时间列', 'time'), f('点数预算', '1,000')], [], [], '查看坐标映射要求', 'extension', [i('数据库', 'factory'), i('可用原因', '缺少适用坐标列')]),
      task('Schema', 'table', '区分 TAG 与 FIELD，查看保存的名称、类型和稀疏性。', [], ['名称', '角色', '类型', '示例值'], [['DeviceID', 'TAG', 'STRING', 'Pump-01'], ['Line', 'TAG', 'STRING', 'Line-A'], ['Temperature', 'FIELD', 'DOUBLE', '67.4'], ['Pressure', 'FIELD', 'DOUBLE', 'NULL 可缺失']], '预览 Schema 变更', 'existing', [i('数据库', 'factory'), i('写入合同', '名称映射到既有 schema 拼写')]),
      task('文件导入', 'import', '先检查列映射、时区与重复策略，再进入审批。', [f('文件', 'Telemetry_1004.csv'), f('时间单位', '毫秒'), f('时区', 'Asia/Shanghai'), f('重复策略', '按真实接口支持项选择')], mappingColumns, [['timestamp', 'time', 'Int64', '需检查时间范围'], ['device', 'DeviceID', 'TAG STRING', '保留既有拼写'], ['temperature', 'Temperature', 'FIELD DOUBLE', '空值不造数']], '预览导入映射', 'existing', [i('目标', 'factory / Telemetry'), i('恢复', '仅实际持久任务支持时启用')]),
      planned('保留策略', '展示 retention 定义与预计影响，尚需确认现有维护接口可提供的范围。', [f('对象', 'factory / Telemetry'), f('保留时长', '待加载真实策略'), f('影响点数', '尚未计算')], [i('删除边界', '预览与批准后才允许执行')])
    ],
    table: [
      base('table', 'table', 'existing'),
      task('设计器', 'table', '检查列定义，编辑生成待评审 DDL，不直接改变表结构。', [], ['列名', '类型', '允许 NULL', '默认值', '约束'], [['AssetID', 'STRING', '否', '—', '主键 · 示例'], ['DeviceID', 'STRING', '否', '—', '—'], ['Name', 'STRING', '否', '—', '—'], ['CommissionedAt', 'TIMESTAMP', '是', '—', '—']], '预览 DDL 草稿', 'existing', [i('目标', 'factory / Assets'), i('兼容性', '以实际 ALTER 支持矩阵为准')]),
      task('索引', 'table', '查看索引定义与维护入口；样例不宣称真实索引已构建。', [], ['名称', '列', '种类', '唯一', '状态'], [['PK_Assets · 示例', 'AssetID', '主键', '是', '未读取'], ['IX_Assets_DeviceID · 示例', 'DeviceID', '按服务能力', '否', '未读取']], '预览索引操作', 'existing', [i('对象', 'factory / Assets'), i('权限', '数据库管理权限')]),
      task('ER 图', 'graph', '演示表与约束的检视布局。样例连接关系必须有实际外键合同后才显示为真实 ER。', [], ['表', '列', '关系', '证据'], [['Assets', 'DeviceID', '→ Telemetry.DeviceID', '设计样例，非外键证据'], ['Assets', 'AssetID', '→ Maintenance.AssetID', '设计样例，非外键证据']], '查看关系定义', 'existing', [i('图源', '实际约束与 schema'), i('禁止推断', '同名列不自动成为外键')]),
      task('DDL', 'json', '查看并复制保留原始大小写的定义示例；最终 SQL 以服务器返回为准。', [f('定义', 'CREATE TABLE Assets (AssetID STRING, DeviceID STRING, Name STRING);', '设计示例，需按真实 DDL 语法生成', 'textarea')], ['来源', '定义'], [['示例 DDL', 'CREATE TABLE Assets (…)']], '复制定义示例', 'existing', [i('对象', 'factory / Assets'), i('标识符', '保留创建拼写并正确引用')]),
      task('导入 / 导出', 'import', '按列校验 CSV 输入，区分覆盖、追加与只导出选中数据。', [f('文件', 'Assets.csv'), f('方向', '导入'), f('重复策略', '需明确选择'), f('导出范围', '当前筛选结果')], mappingColumns, [['asset_id', 'AssetID', 'STRING', '主键需校验'], ['device_id', 'DeviceID', 'STRING', 'NOT NULL'], ['name', 'Name', 'STRING', 'NOT NULL']], '预览文件与影响', 'existing', [i('目标', 'factory / Assets'), i('敏感列', '导出前明确范围')]),
      task('Explain', 'sql', '手动解释当前对象查询，展示扫描、预算与物化边界。', [f('SQL', "SELECT AssetID, Name FROM Assets WHERE DeviceID = 'Pump-01';", '', 'textarea')], ['节点', '输入', '边界'], [['Scan · 示例', 'Assets', '按真实 planner 输出'], ['Filter · 示例', 'DeviceID', '不代表已经执行']], '打开 Explain 输入', 'extension', [i('数据库', 'factory'), i('执行', '手动解释，不自动重跑')])
    ],
    document: [
      base('document', 'search', 'existing'),
      task('文档', 'json', '查看选中文档的 JSON / Tree / Raw。长 Payload 默认折叠，属性键保持文档语义。', [f('文档 ID', 'manual-pump-01'), f('Payload', '{"_id":"manual-pump-01","title":"冷却泵维护手册","DeviceType":"Pump","revision":2}', '', 'textarea')], [], [], '预览局部修改', 'existing', [i('集合', 'factory / Manuals'), i('属性键', '不套用 SQL 名称合同')]),
      task('Aggregate', 'form', '编辑已支持的聚合阶段，校验通过才可转入执行；未知阶段明确拒绝。', [f('Filter', '{"DeviceType":"Pump"}', '', 'textarea'), f('Group key', 'DeviceType'), f('聚合', 'count'), f('最大结果', '100')], ['DeviceType', 'count'], [['Pump', '2 · 示例'], ['Valve', '1 · 示例']], '预览聚合输入', 'extension', [i('集合', 'factory / Manuals'), i('兼容边界', '不是完整 MongoDB Pipeline')]),
      task('索引', 'table', '检查 JSON 路径和全文索引，维护走差异预览。', [], ['名称', '路径 / 字段', '种类', '状态'], [['ByDeviceType · 示例', '$.DeviceType', 'JSON 路径', '未读取'], ['SearchIndex · 示例', 'title, body', '全文', '未读取']], '预览索引变更', 'existing', [i('集合', 'factory / Manuals')]),
      task('Validator', 'form', '编辑验证规则并检查候选文档；规则变更先展示差异与错误路径。', [f('规则', '{"required":["title","DeviceType"]}', '语法以真实 validator 为准', 'textarea'), f('候选文档', '{"title":"冷却泵维护手册","DeviceType":"Pump"}', '', 'textarea')], ['路径', '结果'], [['$.title', '示例校验字段'], ['$.DeviceType', '示例校验字段']], '预览校验输入', 'existing', [i('对象', 'factory / Manuals'), i('权限', '变更需数据库管理权限')]),
      task('Change Feed', 'table', '按真实 continuation 读取文档变更，区分返回位点与客户端接收时间。', [f('Continuation', '尚未请求'), f('本页上限', '50')], ['时间', '文档 ID', '动作', '位点'], [['09:40:00 · 示例', 'manual-pump-01', 'replace · 示例', '真实 token 未提供'], ['09:41:00 · 示例', 'note-line-a-1004', 'insert · 示例', '真实 token 未提供']], '预览订阅范围', 'existing', [i('对象', 'factory / Manuals'), i('恢复', '断线不代表持久订阅已恢复')]),
      task('导入 / 导出', 'import', '校验 NDJSON 与 ID 路径；replace 可能覆盖现有文档，必须明确预览。', [f('文件', 'Manuals.ndjson'), f('ID 路径', '_id'), f('写入模式', 'Insert'), f('最大预览', '20 文档')], ['行', 'ID', '校验'], [['1', 'manual-pump-01', '待校验'], ['2', 'manual-valve-01', '待校验']], '预览文档导入', 'existing', [i('集合', 'factory / Manuals'), i('重复 ID', '按选择模式处理，不自动切换')])
    ],
    kv: [
      base('kv', 'table', 'existing'),
      task('值检查器', 'json', '检查一个 key 的真实字节表示，二进制不强制转换为 UTF-8。', [f('Key', 'device:Pump-01:state'), f('显示格式', 'JSON'), f('Value', '{"state":"running","temperature":67.4}', '', 'textarea')], [], [], '预览值修改', 'extension', [i('Keyspace', 'factory / DeviceState'), i('可选格式', 'Text / JSON / Hex / Base64')]),
      task('TTL', 'form', '将 TTL 剩余时长、绝对到期与无过期状态分开；修改先预览。', [f('Key', 'device:Pump-01:state'), f('当前 TTL', '59 秒 · 静态示例'), f('到期时间', '真实响应后显示'), f('目标 TTL', '120 秒', '以服务器 TTL 单位合同为准')], [], [], '预览 TTL 修改', 'extension', [i('范围', 'factory / DeviceState'), i('过期', '已过期与读取失败分开')]),
      task('批量操作', 'form', '匹配范围与实际加载页分开标记，批量删除先确认 key 清单和数量。', [f('Prefix', 'device:'), f('动作', '删除已选 Key'), f('选中数量', '2 · 示例'), f('范围', '当前已加载列表')], ['Key', '操作'], [['device:Pump-01:state', '删除预览'], ['device:Pump-02:state', '删除预览']], '预览批量影响', 'existing', [i('Keyspace', 'factory / DeviceState'), i('禁止误导', '页内筛选不是全量扫描')]),
      task('统计', 'table', '区分当前加载页统计与服务器全量统计，不将未知数量填零。', [], ['指标', '范围', '值'], [['Key 数', '当前示例页', '4'], ['JSON Key', '当前示例页', '2'], ['有 TTL', '当前示例页', '1'], ['全量 Key 数', '服务器', '尚未提供']], '查看统计口径', 'existing', [i('对象', 'factory / DeviceState')]),
      task('操作历史', 'table', '恢复当前 keyspace 的输入，只读历史不自动重放修改/CAS。', [], auditColumns, [['09:42 · 示例', 'device:Pump-01:state', 'GET', '示例', '本地 History'], ['09:40 · 示例', 'device:Pump-02:state', 'TTL 预览', '草稿', '本地 History']], '恢复选中输入', 'extension', [i('对象', 'factory / DeviceState'), i('CAS', '仅服务实际支持时启用')])
    ],
    mq: [
      task('概览', 'table', 'SonnetMQ Topic 逻辑属于 factory 数据库，以数据库权限和命名空间进入九模型统一资源树；物理 Store 共享实例 .system/mq。', [], ['Topic', '消息 / Offset', '消费者组', 'Retention'], [['DeviceEvents', '10242 · 示例', '2 · 示例', '以真实配置为准']], '查看 Topic 运行时', 'existing', [i('逻辑数据库', 'factory / DeviceEvents'), i('物理存储', '实例共享 .system/mq Store'), i('单库备份', '尚未覆盖 MQ Store')]),
      task('消息', 'table', '在 factory / DeviceEvents 按 offset/time seek 浏览有界消息，读取按数据库 Read 权限；浏览不会自动 ack。', [f('数据库', 'factory'), f('Seek 模式', 'Offset'), f('起始 offset', '10240'), f('最大条数', '100')], byId.get('mq').columns, byId.get('mq').rows, '预览浏览范围', 'existing', [i('逻辑数据库', 'factory / DeviceEvents'), i('物理存储', '实例共享 .system/mq Store'), i('Payload', 'JSON / Text / Hex / Base64'), i('单库备份', '尚未覆盖 MQ Store')]),
      task('消费者组', 'table', '检视当前数据库 Topic 的 consumer group、位点和 lag；ack/reset 等修改按数据库 Write 权限与真实能力预览。', [], ['消费者组', '位点', 'Lag', '状态'], [['factory-monitor · 示例', '10240 · 示例', '2 · 示例', '未读取运行时'], ['audit-export · 示例', '10238 · 示例', '4 · 示例', '未读取运行时']], '查看组位点', 'existing', [i('逻辑数据库', 'factory / DeviceEvents'), i('物理存储', '实例共享 .system/mq Store'), i('Ack', '不因浏览改变位点'), i('单库备份', '尚未覆盖 MQ Store')]),
      task('配置', 'form', 'Topic 配置按数据库逻辑上下文查看；可能影响实例共享 Store 的全局配置须单独提示影响范围和核验权限，不能当作仅当前数据库变更。', [f('数据库', 'factory'), f('Topic', 'DeviceEvents'), f('Retention', '以真实运行配置为准'), f('持久目录', '实例共享 .system/mq'), f('容量 / 全局参数', '先核对真实 API 作用范围')], [], [], '预览 Topic 配置', 'existing', [i('逻辑数据库', 'factory / DeviceEvents'), i('物理存储', '实例共享 .system/mq Store'), i('全局影响', '跨数据库 Store 影响需明确检查'), i('单库备份', '尚未覆盖 MQ Store'), i('兼容', '不宣称 Kafka/RabbitMQ 完整兼容')]),
      task('恢复边界', 'notice', 'Topic 逻辑属于数据库，物理数据与消费位点保存在实例共享 .system/mq。当前单数据库备份未覆盖该 Store；恢复需检查跨数据库 Topic 影响，不能声称数据库与 MQ 已统一备份。当前只展示边界，不执行恢复。', [f('逻辑数据库', 'factory / DeviceEvents'), f('物理范围', '实例共享 .system/mq Store'), f('单库备份', '尚未覆盖 MQ Store'), f('恢复影响', '可能影响实例内其它数据库 Topic'), f('恢复证据', '需独立核验')], [], [], '查看 MQ 持久化边界', 'extension', [i('逻辑数据库', 'factory'), i('物理存储', '实例共享 .system/mq Store'), i('恢复操作', '需独立合同、权限与影响预览')]),
      planned('DLQ · 能力依赖', '当前统一 DLQ/重放页面合同尚未确认。必须保留数据库 Topic 命名空间与权限，不能虚构死信列表或重新投递动作。', [f('数据库', 'factory'), f('Topic', 'DeviceEvents'), f('能力', '待服务 capabilities 确认')], [i('逻辑数据库', 'factory / DeviceEvents'), i('物理存储', '实例共享 .system/mq Store'), i('单库备份', '尚未覆盖 MQ Store')]),
      task('审计', 'table', '按 factory 数据库 Topic 浏览实际可取得的操作/批准记录，清楚标记本地与服务端来源。', [], auditColumns, [['09:35 · 示例', 'factory / DeviceEvents', 'Publish 预览', '草稿', '本地 History'], ['09:32 · 示例', 'factory / DeviceEvents', 'Browse', '示例', '本地 History']], '查看选中记录', 'extension', [i('逻辑数据库', 'factory / DeviceEvents'), i('物理存储', '实例共享 .system/mq Store'), i('单库备份', '尚未覆盖 MQ Store'), i('限制', 'Topic offset 不是数据库备份位点')])
    ],
    vector: [
      base('vector', 'search', 'existing'),
      task('数据 / 导入', 'import', '校验 ID、维度、向量与 metadata；长向量使用有限预览。', [f('文件', 'embeddings.ndjson'), f('ID 路径', 'id'), f('向量列', 'Embedding'), f('维度', '从真实 profile 加载')], ['ID', '向量预览', '维度', '校验'], [['manual-pump-01#12', '[0.12, -0.34, …]', '以真实数据为准', '未执行']], '预览向量导入', 'existing', [i('对象', 'factory / ManualEmbeddings.Embedding')]),
      task('索引参数', 'form', '只提供当前服务已支持的索引参数，不以设计输入增加引擎能力。', [f('Kind', '真实索引定义'), f('Dimensions', '未读取'), f('Metric', '真实索引定义'), f('Top-K 默认', '10')], [], [], '预览索引参数', 'existing', [i('对象', 'factory / ManualEmbeddings.Embedding')]),
      task('Profile', 'form', '显式展示 Provider、模型、revision、维度与 normalization，缺 profile 时保留 raw 路径。', [f('Profile ID', '服务器已配置项'), f('Provider / Model', '未读取'), f('Revision', '未读取'), f('Dimensions', '未读取'), f('Normalization', '未读取')], [], [], '查看 Profile 合同', 'extension', [i('范围', 'factory'), i('Fallback', '不悄悄使用 hash fallback')]),
      task('命中详情', 'json', '对选中 hit 展示源数据、metadata 与 metric 解释。', [f('Hit ID', 'manual-pump-01#12'), f('Metadata', '{"source":"Manuals","DeviceType":"Pump"}', '', 'textarea'), f('摘要', '冷却泵轴承温度异常检查')], ['指标', '值'], [['Distance', '0.084 · 示例'], ['排名', '1 · 示例']], '打开源对象', 'extension', [i('数据库', 'factory'), i('分数方向', '按真实 metric 解释')]),
      planned('质量证据', '需真实模型、数据集、Recall、质量/成本与硬件报告；示例排名及 tiny fixture 不构成语义质量证据。', [f('模型报告', '未就绪'), f('Recall 报告', '未就绪'), f('固定硬件', '未就绪')])
    ],
    fulltext: [
      base('fulltext', 'search', 'existing'),
      task('Analyzer', 'form', '检查 tokenizer 与 token/position；下表仅解释输出布局。', [f('输入文本', '冷却泵温度告警'), f('Tokenizer', '从索引定义加载')], ['Token', 'Position', 'Offset'], [['冷却泵 · 示例', '0', '0–3'], ['温度 · 示例', '1', '3–5'], ['告警 · 示例', '2', '5–7']], '预览分析输入', 'existing', [i('对象', 'factory / Manuals.SearchIndex'), i('证据', '不是 analyzer 实测')]),
      task('索引', 'table', '检视索引字段与 tokenizer，配置修改先比较差异。', [], ['属性', '值'], [['Collection', 'Manuals'], ['Index', 'SearchIndex'], ['Fields', 'title, body · 示例'], ['Tokenizer', '以真实定义为准']], '预览索引变更', 'existing', [i('数据库', 'factory')]),
      task('重建', 'form', '通过既有维护入口预览影响；没有真实任务 ID 就不提供 resume 或伪百分比。', [f('索引', 'Manuals.SearchIndex'), f('范围', '当前索引'), f('任务 ID', '尚未执行'), f('恢复能力', '按真实响应')], [], [], '预览重建影响', 'extension', [i('数据库', 'factory'), i('进度', '按真实服务器终态展示')]),
      task('数据导入', 'import', '选择 ID 路径与 insert/replace，再检查文档和重复记录。', [f('文件', 'Manuals.ndjson'), f('ID 路径', '_id'), f('模式', 'Insert')], ['行', '文档 ID', '状态'], [['1', 'manual-pump-01', '待校验'], ['2', 'manual-valve-01', '待校验']], '预览全文数据导入', 'existing', [i('目标', 'factory / Manuals.SearchIndex')]),
      task('统计', 'table', '整理可用索引统计；未知字段明确未提供。', [], ['指标', '值', '来源'], [['索引字段', '2 · 示例', '设计定义'], ['文档数', '未读取', '服务器 stats'], ['词项数', '未提供', '按能力']], '查看统计口径', 'extension', [i('数据库', 'factory')])
    ],
    bucket: [
      base('bucket', 'table', 'existing'),
      task('预览', 'form', '用有界 Range 读取选中对象，先检查类型与大小，不自动下载全文件。', [f('Key', '2026-10-04/pump-01.jpg'), f('Range 起点', '0'), f('最大字节', '65,536'), f('格式', '按 Content-Type / Hex')], ['元数据', '值'], [['Content-Type', 'image/jpeg · 示例'], ['Size', '2.4 MB · 示例'], ['Version', 'v-demo-12']], '预览 Range 请求', 'existing', [i('Bucket', 'factory / Evidence')]),
      task('上传 / 下载', 'import', '选择文件与目标 key，明确覆盖、新版本、metadata 和校验。', [f('方向', '上传'), f('文件', 'pump-01.jpg'), f('目标 Key', '2026-10-04/pump-01.jpg'), f('Content-Type', 'image/jpeg'), f('Metadata', 'device=Pump-01; line=Line-A')], ['阶段', '状态'], [['本机文件', '示例，未选择实际文件'], ['目标版本', '需确认'], ['服务器提交', '尚未执行']], '预览对象上传', 'existing', [i('Bucket', 'factory / Evidence'), i('宿主', 'Native bridge / Web fallback')]),
      task('Multipart', 'table', '检查会话、分片确认和有效期，complete/abort 走独立预览。', [f('对象', 'archive/line-a-1004.zip'), f('Session', 'demo-session-01')], ['Part', '大小', 'Checksum', '服务器状态'], [['1', '16 MB · 示例', '真实 checksum 未读取', '示例已确认'], ['2', '16 MB · 示例', '真实 checksum 未读取', '示例已确认'], ['3', '16 MB · 示例', '真实 checksum 未读取', '示例已确认']], '预览完成分片', 'existing', [i('范围', 'factory / Evidence'), i('进度', '浏览器进度不等于服务器提交')]),
      task('版本与治理', 'form', '当前对象与非当前版本的 retention/lifecycle 分开；legal hold、quota 与 policy 明确作用范围。', [f('Lifecycle', '从实际策略加载'), f('Retention', '从实际策略加载'), f('Quota', '从实际策略加载'), f('Legal hold', '当前对象版本'), f('Policy', '以服务器定义为准')], ['版本', '当前', '保护'], [['v-demo-12', '是 · 示例', '需读取真实策略'], ['v-demo-08', '否 · 示例', '需读取真实策略']], '预览治理变更', 'existing', [i('Bucket', 'factory / Evidence'), i('删除', '对象删除与版本删除分开')]),
      task('图片语义', 'search', '使用真实配置的语义 profile；处理状态、metadata filter 与命中 Inspector 共同解释结果。', [f('查询', 'Line-A 冷却泵温度异常'), f('Top-K', '10'), f('Prefix', '2026-10-04/'), f('Profile', '显式配置项，尚未读取')], ['Key', 'Score', '处理状态'], [['2026-10-04/pump-01.jpg', '0.82 · 示例', '静态样例']], '预览语义查询', 'existing', [i('Bucket', 'factory / Evidence'), i('证据', 'hash fallback/合成模型不算真实语义证据')]),
      task('审计', 'table', '按 prefix 和上限查看对象动作，不扩大 S3/SigV4 兼容声明。', [f('Prefix', '2026-10-04/'), f('最大记录', '50')], auditColumns, [['09:40 · 示例', 'pump-01.jpg', 'PUT 预览', '草稿', '本地示例'], ['09:42 · 示例', 'pump-01.jpg', 'Range preview', '未执行', '原型']], '查看选中审计', 'existing', [i('Bucket', 'factory / Evidence')])
    ],
    graph: [
      base('graph', 'graph', 'existing'),
      task('Schema / 索引', 'table', '检视真实 label、property/index 与诊断能力，始终保留 Graph Beta。', [], ['Label', '属性', '索引', '状态'], [['Device · 示例', 'DeviceID, type, line', '以真实定义为准', '未读取'], ['Station · 示例', 'name', '以真实定义为准', '未读取'], ['CONNECTED_TO · 示例', 'flow', '以真实定义为准', '未读取']], '查看图定义', 'existing', [i('对象', 'factory / FactoryTopology'), i('模型状态', 'Graph Beta')]),
      task('SQL / PGQ', 'sql', '进入共用 SQL 编辑器；图查询必须符合真实 SQL/PGQ 支持矩阵和有界预算。', [f('SQL / PGQ', '在 FactoryTopology 上下文编写受支持的图查询', '语法提示来自真实合同，不把示意文本当可执行 SQL', 'textarea'), f('最大节点 / 边', '200 / 400 · 设计预算')], [], [], '打开图查询输入', 'extension', [i('模型', 'Graph Beta'), i('数据库', 'factory')]),
      task('受限编辑', 'form', '节点/边只编辑草稿；Stage 成功才获得有效审批，批准后才改变数据。', [f('元素', 'Vertex / Pump-01'), f('Label', 'Device'), f('Properties', '{"line":"Line-A","type":"Pump"}', '', 'textarea'), f('审批', '尚未暂存')], ['字段', '之前', '草稿'], [['line', 'Line-A · 示例', 'Line-A'], ['type', 'Pump · 示例', 'Pump']], '预览节点差异', 'existing', [i('对象', 'factory / FactoryTopology'), i('模型', 'Graph Beta')]),
      task('导入 / 导出', 'import', '检查 vertices/edges 身份、关系和预算；导入需 staged preview 与批准。', [f('文件', 'topology.json'), f('Payload', '{"vertices":[],"edges":[]}', '', 'textarea'), f('节点 / 边预算', '200 / 400')], ['内容', '数量', '校验'], [['Vertices', '尚未读取', '需验证身份'], ['Edges', '尚未读取', '需验证端点']], '预览图导入', 'existing', [i('对象', 'factory / FactoryTopology'), i('模型', 'Graph Beta')]),
      task('维护', 'form', '选择 repair/rebuild、checkpoint 或 compact；当前只预览，不生成服务器审批。', [f('操作', 'Checkpoint'), f('Graph', 'FactoryTopology'), f('影响范围', '以真实维护预览为准'), f('审批有效期', '服务器返回后显示')], [], [], '预览图维护', 'existing', [i('数据库', 'factory'), i('模型', 'Graph Beta'), i('过期处理', '重新暂存，禁止重放过期审批')]),
      task('审计', 'table', '拆出既有维护审计任务，区分草稿、服务器暂存与执行终态。', [], auditColumns, [['09:30 · 示例', 'FactoryTopology', 'Checkpoint preview', '未执行', '原型'], ['09:25 · 示例', 'Pump-01', 'Stage edit', '草稿示例', '本地示例']], '查看图审计详情', 'extension', [i('对象', 'factory / FactoryTopology'), i('模型', 'Graph Beta')])
    ],

    summary: [
      base('summary'),
      task('资源分布', 'table', '按数据库逻辑范围查看九模型分布；MQ Topic 归入当前数据库，其物理 Store 另注实例共享。', [], ['模型', '范围', '对象示例', '数量口径'], [['时序', 'factory', 'Telemetry', '1 · 示例'], ['关系', 'factory', 'Assets', '1 · 示例'], ['文档 / 搜索', 'factory', 'Manuals', '按真实资源分类'], ['MQ', 'factory', 'DeviceEvents', '1 · 示例；物理 .system/mq 共享实例']], '打开资源树', 'extension', [i('统计', '不可重复计数派生索引')]),
      task('待处理事项', 'table', '从草稿、审批与任务进入对应原生工作流。', [], ['事项', '目标', '状态', '下一步'], [['Assets 变更', 'factory / Assets', '2 行草稿 · 示例', '预览差异'], ['RAG 换代', 'factory / copilot-docs', '待确认 · 示例', '查看 RAG'], ['Graph 维护', 'factory / FactoryTopology', 'Beta · 示例', '检查审批']], '打开选中事项', 'extension')
    ],
    'database-catalog': [
      base('database-catalog'),
      task('最近访问', 'table', '按当前连接显示最近进入的数据库，访问时间不等于健康检查时间。', [], ['数据库', '最近访问', '最后任务', '权限'], [['factory', '今天 09:42 · 示例', 'Telemetry 查询', '管理 · 示例'], ['analytics', '昨天 16:20 · 示例', '报表查询', '查询 · 示例']], '打开选中数据库', 'extension')
    ],
    connections: [
      base('connections'),
      task('连接测试', 'notice', '当前为未测试状态。真实流程分别检查 URL、服务健康、认证、数据库/对象权限和 capabilities；原型不联网，不能返回测试成功。', [f('URL', 'http://127.0.0.1:5080'), f('测试预算', '10 秒'), f('服务健康', '未执行'), f('认证', '未执行'), f('数据库/对象权限', '未执行'), f('能力', '未执行'), f('测试时间', '未执行')], [], [], '查看测试步骤', 'extension'),
      task('凭据说明', 'notice', 'Web 使用当前会话；Studio 使用 Windows 安全存储；VS Code 使用 SecretStorage。共享 profile 与工作区不包含 Token 明文。', [f('Web', '当前会话'), f('Studio', 'Windows 凭据管理器'), f('VS Code', 'SecretStorage')], [], [], '查看凭据边界', 'extension')
    ],
    recent: [
      planned('最近', 'Workspace Registry 将保存页签、输入、过滤器和快照，恢复不会自动执行。', [f('数据库', 'factory'), f('工作区', '产线温度分析')]),
      planned('已固定', '固定常用工作区以便跨会话找回，尚无统一 Registry 存储合同。', [f('拟固定工作区', '设备异常排查'), f('宿主范围', '当前宿主')]),
      planned('离线快照', '离线结果必须显示采集时间、预算和过期状态，联网后只恢复上下文。', [f('快照时间', '示例，不是已保存资产'), f('重放写操作', '禁止')])
    ],
    sql: [
      base('sql', 'sql'),
      task('参数', 'form', '参数声明与值分开，执行前检查类型/NULL；支持范围遵循当前 SQL 客户端合同。', [f('DeviceID', 'Pump-01', 'STRING 参数，示例'), f('from', '2026-10-04 09:00:00', 'TIMESTAMP 参数，Asia/Shanghai'), f('to', '2026-10-04 10:00:00', 'exclusive upper bound'), f('结果上限', '1,000 行'), f('超时预算', '30 秒')], [], [], '预览参数输入', 'extension', [i('数据库', 'factory'), i('绑定', '参数值不拼接为 SQL 标识符')]),
      task('查询说明', 'notice', 'SQL 标识符保留创建拼写；未引用名称 OrdinalIgnoreCase，双引号名称 Ordinal。结果预算、物化与支持矩阵以真实能力为准。', [f('名称示例', 'DeviceID / "DeviceID"'), f('结果视图', 'Table / Raw / Chart / Explain'), f('写入路径', '暂存预览 → 审批')], [], [], '查看查询合同', 'existing')
    ],
    notebook: [
      planned('单元', '说明与 SQL 单元组织排查旅程；当前 Notebook 无生产入口，不能执行单元。', [f('单元 01', '说明：检查 Pump-01'), f('单元 02', 'SQL：查询 Telemetry')]),
      planned('大纲', '按标题与单元定位，不关闭当前 SQL 输入或丢失草稿。', [f('章节', '设备上下文 / 温度窗口 / 维护记录 / 结论')]),
      planned('版本与导出', 'Notebook 作为可提交资产，导出须排除连接凭据并显式选择结果快照。', [f('格式', '待确认 Notebook 合同'), f('结果快照', '默认不包含'), f('凭据', '始终排除')])
    ],
    history: [
      base('history'),
      task('模型操作', 'table', '查看同一连接下的模型操作输入与状态，恢复仅恢复草稿；MQ 按数据库 Topic 过滤，物理 Store 为实例共享。', [], auditColumns, [['09:31 · 示例', 'factory / Manuals', 'Find', '示例', '本地 History'], ['09:28 · 示例', 'factory / DeviceEvents', 'Browse', '示例', '本地 History']], '恢复模型输入', 'extension'),
      task('审批记录', 'table', '从本地 History 跳转对应审批详情，不把本地记录充当服务器审计。', [], ['时间', '对象', '动作', '审批来源', '状态'], [['09:37 · 示例', 'factory / Assets', '修改 2 行', '本地草稿', '未执行'], ['09:25 · 示例', 'factory / Graph Beta', '维护', '实际审批待读取', '示例']], '查看审批来源', 'extension')
    ],
    metrics: [
      base('metrics', 'chart'),
      task('内存与存储', 'table', '按数据库呈现 MemTable 与 Segment，指标缺失时显示未提供。', [], ['数据库', 'MemTable bytes', 'MemTable points', 'Segments'], [['factory', '24.8 MB · 示例', '82,430 · 示例', '128 · 示例'], ['analytics', '8.2 MB · 示例', '24,100 · 示例', '64 · 示例']], '查看采样口径', 'existing', [i('端点', '/metrics'), i('窗口', '最近 10 分钟 · 示例')]),
      task('AI 用量', 'chart', '区分真实 usage 与估算值；本页示例不能用于真实成本结论。', [], ['时间窗口', 'Requests', 'Input tokens', 'Output tokens'], [['09:00–09:10', '12 · 示例', '2,400 · 示例', '860 · 示例'], ['09:10–09:20', '8 · 示例', '1,600 · 示例', '520 · 示例']], '查看估算说明', 'existing', [i('成本', '真实 Provider 报告独立验收')])
    ],
    events: [
      base('events'),
      task('数据库事件', 'table', '单独查看 CREATE/DROP 事件，避免与查询流混合。', [], ['接收时间', '数据库', '操作', '来源'], [['09:40 · 示例', 'sandbox', 'created · 示例', 'SSE'], ['昨天 · 示例', 'analytics', 'loaded · 示例', '设计样例']], '查看事件详情', 'existing'),
      task('连接状态', 'notice', '展示 SSE 连接、最后帧时间与重新连接原因。原型未订阅真实事件流。', [f('传输', 'SSE'), f('最后帧', '未订阅'), f('重连', '未执行'), f('保留数据', '断线保留本地已接收列表')], [], [], '查看事件连接合同', 'existing')
    ],
    'slow-queries': [
      base('slow-queries'),
      task('选中 SQL', 'sql', '仅恢复慢查询文本到当前编辑器，保留来源与接收时间，不自动重跑。', [f('SQL', 'SELECT DeviceID, AVG(Temperature) FROM Telemetry GROUP BY DeviceID;', '静态示例', 'textarea'), f('耗时', '824 ms · 示例')], [], [], '恢复查询输入', 'extension', [i('数据库', 'factory')]),
      task('解释入口', 'notice', 'Explain 需要显式运行并显示真实 planner 输出。当前慢查询样例不附带虚构的执行计划。', [f('SQL 来源', '选中慢查询'), f('自动重跑', '关闭'), f('计划状态', '未执行')], [], [], '打开 Explain 输入', 'extension')
    ],
    alerts: [
      planned('规则', '阈值与评估窗口需要统一告警服务合同。', [f('名称', '查询延迟'), f('指标', 'query P95'), f('窗口', '5 分钟'), f('阈值', '> 500 ms')]),
      planned('评估记录', '当前没有实际告警评估或触发记录，不能把样例显示成真实触发。', [f('最近评估', '未执行'), f('触发数', '未提供')]),
      planned('通知路由', '通知通道与接收范围尚未配置，原型不会发送邮件或消息。', [f('路由', '未配置'), f('通道', '待合同确认'), f('去抖', '评估窗口决定')])
    ],
    runtime: [
      planned('运行时', '通用诊断聚合 DTO 待规划；MQ/Graph 已有诊断可以转入其原生工作台。MQ 运行时按数据库 Topic 展示，物理 Store 为实例共享。', [f('组件', 'WAL / Flush / SonnetMQ / Graph Beta'), f('MQ 逻辑范围', 'factory / DeviceEvents'), f('MQ 物理范围', '实例共享 .system/mq Store')]),
      planned('诊断采集', '必须有预算、取消、脱敏与实际服务器支持才可采集。', [f('时间预算', '30 秒'), f('字节预算', '5 MB'), f('敏感字段', '采集前脱敏')]),
      planned('预算与取消', '区分请求取消与服务器终态，不能因为界面结束便宣布后台任务已结束。', [f('请求状态', '未执行'), f('服务器终态', '未取得')])
    ],
    modbus: [
      base('modbus'),
      task('待审批写入', 'table', '检视现场端点写请求、寄存器范围和绑定目标，批准/拒绝沿用原合同。', [], ['请求', '端点', '寄存器', '表绑定', '状态'], [['demo-write-01', 'LocalSlave', '40001–40004', 'factory / RegisterMap', '待审批示例']], '查看现场写入影响', 'existing', [i('权限', '管理员 / 实际批准权限')]),
      task('审计', 'table', '查看已取得的批准、拒绝与执行事件，包含请求关联标识。', [], ['请求 ID', '时间', '事件', '端点', '结果'], [['demo-write-01', '09:30 · 示例', '预览', 'LocalSlave', '原型未执行']], '查看写请求审计', 'existing')
    ],
    imports: [
      base('imports'),
      task('字段映射', 'import', '模型与目标对象决定字段角色和类型，映射后再检查数据。', [f('目标', 'factory / Telemetry'), f('源文件', 'Telemetry_1004.csv'), f('时间单位', '毫秒')], mappingColumns, [['timestamp', 'time', 'TIMESTAMP', '需验证单位'], ['device', 'DeviceID', 'TAG STRING', '保持既有 schema'], ['temperature', 'Temperature', 'FIELD DOUBLE', '需验证范围']], '预览字段映射', 'extension'),
      task('校验与错误', 'table', '错误明确到行/字段，失败策略不默默跳过记录。', [], ['位置', '字段', '问题', '处理'], [['示例行 12', 'timestamp', '时间单位不明确', '修正映射'], ['示例行 19', 'Temperature', '值不是 DOUBLE', '修正数据']], '返回修正映射', 'extension', [i('持久恢复', '按目标模型能力开放')])
    ],
    transfers: [
      base('transfers'),
      task('Multipart 会话', 'table', '从真实对象会话查看已确认分片、有效期与继续上传能力。', [], ['会话', '对象', '分片', '状态'], [['demo-session-01', 'Evidence / archive/line-a-1004.zip', '3 / 8 · 示例', '示例会话，未联网']], '查看会话详情', 'extension', [i('范围', 'factory / Evidence')]),
      task('校验与错误', 'table', '比较服务器与客户端校验状态；不把上传字节完成当作提交完成。', [], ['对象', '检查', '结果', '下一步'], [['pump-01.jpg', 'Checksum', '尚未读取', '等待服务器响应'], ['line-a-1004.zip', 'Part checksum', '示例，未校验', '查看分片详情']], '查看校验合同', 'extension')
    ],
    jobs: [
      planned('任务索引', '统一跨模型任务索引尚无通用 DTO，任务恢复保持模型原生合同。MQ 消费任务归属数据库 Topic；共享 Store 恢复可能影响其它数据库。', [f('模型', 'RAG / 对象语义 / SonnetMQ'), f('MQ 逻辑范围', 'factory / DeviceEvents'), f('MQ 物理范围', '实例共享 .system/mq Store')]),
      planned('位点', 'generation、revision、object version 和 MQ Topic offset 是不同语义，不能混合为通用恢复数字。MQ 位点归属数据库 Topic，物理持久化由实例共享 Store 承载。', [f('RAG', 'expected revision / generation'), f('对象', 'object version'), f('SonnetMQ', 'factory / DeviceEvents offset；物理 .system/mq')]),
      planned('重试与恢复', '通用按钮不能把客户端重试冒称持久 resume；可转入 RAG 等真实恢复入口。', [f('前置条件', '权限 / profile / 版本 / 持久任务身份'), f('写入重放', '禁止自动执行')])
    ],
    'ai-connect': [
      planned('连接向导', '选择客户端、传输与配置范围；HTTP MCP 已有，向导与 stdio bridge 尚待 M47-U08。', [f('客户端', 'WorkBuddy'), f('传输', 'Streamable HTTP'), f('数据库', 'factory'), f('配置范围', '用户')]),
      planned('配置预览', '配置引用环境变量/宿主凭据，当前不能直接写用户或项目配置。', [f('Endpoint', 'http://127.0.0.1:5080/mcp/factory'), f('Credential', '环境变量引用，不含明文'), f('stdio', 'bridge 尚未实现')]),
      planned('工具列表', '真实 tools/list 未接通；只能说明 schema/SQL/model browse 的只读边界，不能伪造工具成功响应。', [f('tools/list', '未执行'), f('默认权限', '只读'), f('写入口', '工作台审批')]),
      planned('自检', '真实工具未接通。HTTP、认证、tools/list、预算、取消各项都是未执行；不能返回自检 PASS。', [f('HTTP', '未执行'), f('认证', '未执行'), f('tools/list', '未执行'), f('预算 / 取消', '未执行')]),
      planned('数据外发', '工具返回内容是否出域由显式宿主策略、用户权限和调用范围共同决定。', [f('允许内容', '用户明确选择的查询 / metadata'), f('凭据', '永不作为工具结果外发'), f('宿主责任', '自托管与官方 Connector 分开')])
    ],
    'copilot-settings': [
      base('copilot-settings', 'form'),
      task('账号绑定', 'notice', '复用现有 sonnetdb.com 设备码/授权绑定流程；本原型不发起授权或轮询。', [f('绑定状态', '未读取'), f('设备码', '未请求'), f('Token 到期', '服务器返回后显示')], [], [], '查看账号绑定流程', 'existing'),
      task('模型目录', 'table', '真实 Provider 返回目录后才可选择模型；不预设不存在的模型 ID。', [], ['用途', '模型来源', '状态'], [['Chat', '平台 / Provider 真实目录', '尚未加载'], ['Embedding', '显式 Profile', '尚未加载']], '查看目录来源', 'existing'),
      task('用量', 'table', '输入/输出 Token 与请求数区分真实和估算，成本门禁另有证据。', [], ['指标', '当前值', '口径'], [['Requests', '未读取', '最近 1 小时'], ['Input tokens', '未读取', '实际 / 估算分开'], ['Output tokens', '未读取', '实际 / 估算分开']], '查看用量口径', 'existing'),
      task('测试', 'notice', '连接测试与真实模型质量/成本分别记录。本原型没有调用 Provider，测试状态为未执行。', [f('连接测试', '未执行'), f('模型质量', '独立门禁'), f('成本', '独立门禁')], [], [], '查看测试边界', 'existing')
    ],
    rag: [
      base('rag'),
      task('持久任务', 'table', '检查 generation、profile 与 expected revision，续跑必须匹配真实服务器任务。', [], ['Generation', 'Profile', 'Expected revision', '内容 / 分块', '状态'], [['demo-07', 'factory-text-v2 · 示例', '7 · 示例', '128 / 864 · 示例', '未读取真实任务']], '预览任务续跑', 'existing', [i('对象', 'factory / copilot-docs'), i('丢弃', '仅删除未发布派生资源')]),
      task('派生重建', 'form', '同 profile 重建或新 profile 换代；新版本完整发布前继续使用当前快照。', [f('Stream', 'copilot-docs'), f('Active revision', '7 · 示例'), f('目标 Profile', '选择已配置项'), f('操作', '重建 / 换代')], [], [], '预览 RAG 重建', 'existing', [i('数据库', 'factory'), i('安全整数', '超出范围禁写')]),
      task('退役清理', 'form', '按发布时间截止点与最大版本数预览；使用中的版本延期处理，原始主数据保留。', [f('截止时间', '明确选择'), f('最多检查版本', '20'), f('原始主数据', '保留'), f('使用中版本', '延期清理')], [], [], '预览退役清理', 'existing', [i('数据库', 'factory')]),
      task('审计', 'table', '数据库 RAG 审计按真实 continuation 分页，每页上限遵循接口。', [f('本页上限', '50'), f('Continuation', '未请求')], auditColumns, [['09:20 · 示例', 'copilot-docs hash', 'Rebuild preview', '未执行', '设计示例']], '查看 RAG 审计范围', 'existing', [i('数据库', 'factory')])
    ],
    'tool-permissions': [
      planned('工具权限', '有效权限为用户授权、只读工具边界与宿主策略的交集；尚无统一权限页面合同。', [f('数据库', 'factory'), f('写入工具', 'MCP 不提供'), f('用户身份', '按实际登录权限')]),
      planned('结果预算', '必须展示 maxRows、字节、时间与截断，前端偏好不能扩大服务器限制。', [f('maxRows', '100 · 设计值'), f('Bytes', '1 MB · 设计值'), f('Timeout', '30 秒 · 设计值')]),
      planned('数据外发', '将允许返回的数据范围、宿主、Provider 与敏感字段排除规则一起预览。', [f('Schema', '按权限'), f('Result rows', '按显式允许范围'), f('Secrets', '排除')]),
      planned('调用记录', '需实际工具调用记录来源与留存权限，目前没有真实调用，不能显示工具执行成功。', [f('调用来源', '未接通'), f('记录', '未取得')])
    ],
    users: [
      base('users'),
      task('选中用户授权', 'table', '查看选中用户的数据库授权，实例权限与数据库权限分开。', [f('用户', 'analyst')], ['数据库', '权限', '来源'], [['factory', '查询 · 示例', '直接授权'], ['analytics', '查询 · 示例', '直接授权']], '打开授权预览', 'existing'),
      task('凭据管理', 'form', '修改密码与 Token 管理分开，密码不出现在表格或历史。', [f('用户', 'analyst'), f('新密码', '••••••••', '仅表单临时值', 'secret'), f('确认新密码', '••••••••', '', 'secret')], [], [], '预览凭据操作', 'existing', [i('权限', '超级用户管理')])
    ],
    grants: [
      base('grants'),
      task('授权预览', 'form', '检查用户、数据库、权限与撤销影响，MQ Topic Read/Write 同样按数据库授权；实例共享 Store 恢复/全局配置另行核验。', [f('用户', 'analyst'), f('数据库', 'factory'), f('目标权限', '查询'), f('动作', '授予 / 撤销需明确选择')], ['范围', '影响'], [['factory', '选中用户的数据库访问，包含数据库 MQ 命名空间'], ['实例共享 MQ Store', '恢复/全局配置需独立权限与跨数据库影响核验']], '预览授权影响', 'existing'),
      task('有效权限', 'table', '按真实数据库授权合同展示 MQ Topic 访问；界面控制不是服务器授权，物理 Store 全局管理不能由 Topic 访问推断。', [], ['范围', '能力', '依据'], [['factory', 'Query · 示例', '数据库 grant'], ['factory / DeviceEvents', 'Read/Write 按真实授权', '数据库命名空间与 grant'], ['实例共享 MQ Store', '恢复/全局配置另行核验', '物理实例范围'], ['敏感管理', '以服务器校验为准', '控制平面']], '查看权限依据', 'extension')
    ],
    tokens: [
      base('tokens'),
      task('创建', 'form', '为已存在用户创建 Token，成功时一次性显示；原型没有生成实际凭据。', [f('所属用户', 'analyst'), f('说明', 'AI client readonly access'), f('有效期', '仅真实合同支持时选择')], [], [], '预览 Token 发放', 'existing', [i('显示', '明文仅创建时一次'), i('撤销', '按标识与用户确认')]),
      task('安全说明', 'notice', 'Token 明文不进入列表、历史、工作区或配置导出；复制失败须显式提示，关闭后不恢复明文。', [f('凭据存储', '宿主安全存储'), f('撤销影响', '关联客户端需重新认证')], [], [], '查看凭据边界', 'existing')
    ],
    approvals: [
      base('approvals'),
      task('影响预览', 'table', '明确对象、动作、差异、数量与风险；未知影响不能允许确认。', [f('目标', 'factory / Assets'), f('动作', '更新记录'), f('影响', '2 行 · 示例'), f('来源', '本地草稿')], ['字段', '之前', '草稿'], [['State / A-003', '维护 · 示例', '运行 · 草稿'], ['Name / A-002', '冷却泵 02 · 示例', '冷却泵 02A · 草稿']], '返回草稿修改', 'extension'),
      task('执行记录', 'table', '服务器执行终态与客户端取消分开；错误不自动重试写入。', [], ['操作', '范围', '请求状态', '服务器终态'], [['draft-001', 'factory / Assets', '未执行', '未取得'], ['demo-graph-002', 'factory / Graph Beta', '未执行', '未取得']], '查看执行状态解释', 'extension'),
      task('审计', 'table', '按来源检视审批/执行记录，不能把本地草稿计作服务端审计。', [], auditColumns, [['09:37 · 示例', 'factory / Assets', 'Draft edit', '本地草稿', 'History'], ['09:30 · 示例', '实例 / LocalSlave', 'Write preview', '未执行', '原型']], '查看审计来源', 'extension')
    ],
    backup: [
      base('backup'),
      task('备份预览', 'form', '核对数据库目录、目标路径与实际覆盖范围后才进入备份执行。MQ Topic 虽逻辑属于数据库，但实例共享 Store 尚未纳入单库备份。', [f('数据库', 'factory'), f('目标位置', '由服务器或 Studio 选择'), f('范围', '单数据库'), f('MQ 物理 Store', '实例 .system/mq，尚未覆盖')], [], [], '预览数据库备份', 'extension'),
      task('验证', 'notice', '实际验证会读取版本/manifest/校验；原型未选择备份、不执行验证，不能返回通过。', [f('备份文件', '未选择'), f('Version', '未读取'), f('Checksum', '未验证')], [], [], '查看验证步骤', 'extension'),
      task('恢复预览', 'form', '恢复前检查版本、目标目录与覆盖对象；确认精确目标，禁止默认为当前数据库覆盖。', [f('源备份', '未选择'), f('目标数据库', '新数据库，需明确名称'), f('目标目录', '服务器受控路径'), f('覆盖确认', '尚未确认')], [], [], '预览恢复范围', 'extension'),
      task('范围说明', 'notice', '当前备份以数据库目录为范围。SonnetMQ Topic 按数据库命名空间与权限管理，但 .system/mq 是实例共享 Store；尚未被单库备份覆盖。恢复该 Store 可能影响其它数据库 Topic，需独立合同与验证。', [f('Database', 'factory'), f('SonnetMQ 逻辑范围', 'factory / DeviceEvents'), f('MQ 物理范围', '实例共享 .system/mq'), f('灾备证据', '原型不代替恢复门禁')], [], [], '查看持久化边界', 'extension')
    ],
    preferences: [
      planned('外观', '统一偏好存储待实现，当前仅预览 Fluent 浅色与标准密度。', [f('主题', 'Fluent 浅色'), f('行高', '36 px'), f('字号', '13 px')]),
      planned('编辑器', '统一三面编辑器偏好需宿主适配，不能改写真实编辑器配置。', [f('字号', '13 px'), f('Tab size', '2'), f('Word wrap', '按偏好')]),
      planned('结果', '默认预算属于客户端偏好，永远不能扩大服务器最大值。', [f('默认行数', '1,000'), f('复制格式', 'TSV / JSON'), f('自动导出', '关闭')]),
      planned('快捷键', '共用命名与宿主冲突需核对，危险确认不绑定直接执行键。', [f('命令中心', 'Ctrl+K · 规划'), f('执行', 'Ctrl+Enter'), f('恢复历史', '按现有快捷键适配')]),
      planned('工作区', '布局宽度与草稿恢复在本宿主保存，Registry 尚待实现。', [f('Explorer', '260 px'), f('Inspector', '300 px'), f('恢复动作', '仅恢复输入')])
    ],
    'server-settings': [
      planned('服务配置', '未有统一在线配置 API；摘要只读，敏感字段脱敏。', [f('来源', 'appsettings.json / capabilities'), f('在线保存', '未提供')]),
      planned('预算', '真实请求预算从服务 capabilities 读取，当前不提供扩大预算开关。', [f('SQL', '待读取'), f('MCP', '待读取'), f('Object Range', '待读取')]),
      planned('可观测性', 'Prometheus 配置继续依赖真实服务文件与重启说明，不伪装已有在线开关。', [f('Prometheus', '待读取'), f('修改方式', '配置文件 / 服务重启')]),
      planned('重启影响', '需要实例宿主生命周期与待处理请求合同后才可执行重启。', [f('影响对象', '连接 / 任务 / MQ runtime'), f('归属', '只管理授权实例'), f('未完成写入', '先核对终态')])
    ],
    'studio-host': [
      base('studio-host'),
      task('Managed Local', 'notice', '当前为 Web 原型。托管 Server 的 start/stop、data root 与健康状态只在真实 Studio bridge 可用；这里不启动进程。', [f('宿主', 'Web Admin'), f('Native bridge', '当前未连接'), f('Server', '未读取')], [], [], '查看 Managed Local 边界', 'extension'),
      task('文件与凭据', 'table', '按宿主选择原生文件 API/安全存储，Bridge 不可用给出可见 fallback。', [], ['能力', 'Web', 'Studio', 'VS Code'], [['文件选择', '浏览器', 'Native dialog', '扩展 API'], ['凭据', '当前会话', 'Windows 安全存储', 'SecretStorage']], '查看宿主适配', 'extension'),
      task('安装与升级', 'notice', '已有安装包/宿主合同不等于干净 Windows、升级、卸载保留、WebView2 门禁通过。原型不能展示安装 PASS。', [f('干净 Windows', '待验证'), f('升级 / 卸载', '待验证'), f('WebView2', '待验证')], [], [], '查看实机验收项', 'extension'),
      task('进程生命周期', 'notice', '托管 Server 只操作宿主拥有的进程。启动/停止需身份、归属、端口与回收验证；Web 原型无进程操作。', [f('PID / 启动时间', '宿主返回后显示'), f('归属', 'Managed Local'), f('Stop', '仅归属明确的 Server')], [], [], '查看生命周期合同', 'extension')
    ],
    'capability-matrix': [
      planned('宿主能力', 'Web 完整治理、Studio 同一 Web 工作台加 native bridge、VS Code Remote-first 开发者入口；统一 manifest 待建。', [f('Web', '完整治理'), f('Studio', 'Web + Native bridge'), f('VS Code', '开发者只读子集 / 深链接')]),
      planned('契约版本', 'API/MCP/UI 契约版本需要真实发行 manifest，原型不填固定已发布版本。', [f('API contract', '待统一'), f('MCP contract', '待统一'), f('uiContractVersion', '待统一')]),
      planned('兼容性', '服务 capabilities 与宿主版本协商后才判断功能可用；未知能力明确拒绝。', [f('Server', '待读取真实版本'), f('Host', '当前原型'), f('Graph', 'Beta')]),
      planned('发布证据', 'Web、干净 Windows Studio 与 Extension Host 分别验证，局部 PASS 不能成为三面发布完成。', [f('Web', '未就绪'), f('Studio', '未就绪'), f('VSIX', '未就绪')])
    ],
    about: [
      base('about'),
      task('指南', 'table', '按连接、查询、九模型与治理任务组织帮助，不用工程模块名替代用户任务。', [], ['指南', '入口', '范围'], [['开始连接', '连接管理', '三个宿主'], ['分析数据', 'SQL / 对象页签', '数据库'], ['消息 / MQ', 'SonnetMQ', '数据库 Topic；物理 .system/mq 共享实例'], ['安全操作', '审批 / 凭据 / 外发', '身份与宿主']], '查看指南目录', 'existing'),
      task('快捷键', 'table', '显示宿主可用快捷键与规划入口，危险操作只可进入预览。', [], ['任务', '快捷键', '状态'], [['执行输入', 'Ctrl+Enter', '既有，需统一映射'], ['命令中心', 'Ctrl+K', '规划'], ['收起 Explorer', '按钮 / 宿主适配', '布局入口']], '查看快捷键说明', 'extension'),
      task('许可证', 'notice', '许可证与第三方声明以真实发行物为准。本页不假设未核查依赖的授权文本。', [f('SonnetDB', '仓库许可证'), f('第三方声明', '发行物随附清单'), f('版本', '真实发行 manifest')], [], [], '查看许可证来源', 'existing')
    ]
  };

  // WB-02 through WB-02F keeps one page-level state contract across the base task and every
  // task tab. These are review fixtures only; app.js still owns the interactive
  // global mode switch and never treats this metadata as a server capability.
  const statefulPages = ['sql', 'summary', 'database-catalog', 'connections', 'recent', 'notebook', 'history', 'metrics', 'events', 'slow-queries', 'alerts', 'runtime', 'measurement', 'table', 'document', 'kv', 'mq', 'vector', 'fulltext', 'bucket', 'graph', 'modbus', 'imports', 'transfers', 'jobs', 'ai-connect', 'copilot-settings', 'rag', 'tool-permissions'];
  statefulPages.forEach((id) => {
    const page = byId.get(id);
    if (!page) return;
    (details[id] || []).forEach((entry) => {
      entry.stateMatrix = page.stateMatrix;
      entry.capabilities = page.capabilities;
    });
  });
  window.M47_TASK_DETAILS = details;
})();
