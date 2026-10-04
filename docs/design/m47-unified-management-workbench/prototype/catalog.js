/* M47 review prototype. All displayed records are illustrative design fixtures. */
window.M47_CATALOG = {
  sections: [
    {
      id: 'overview', label: '概览', icon: 'layout', pages: [
        {
          id: 'summary', title: '实例概览', tabLabel: '实例概览', type: 'overview', scope: 'instance', status: 'extension', existingRoute: '/admin/app/dashboard',
          intro: '从连接健康、数据库和待处理事项进入今天的工作。数值为设计示例。', primary: '打开工作台',
          columns: ['数据库', '模型', 'Segment', '最近访问', '状态'],
          rows: [['factory', '9 种', '128', '今天 09:42', '在线 · 示例'], ['analytics', '4 种', '64', '昨天 16:20', '在线 · 示例'], ['sandbox', '3 种', '12', '10 月 02 日', '只读 · 示例']],
          fields: [{ label: '数据库', value: '3' }, { label: '活动工作区', value: '4' }, { label: '待审批', value: '2' }, { label: '需关注', value: '1' }],
          tabs: ['总览', '资源分布', '待处理事项'], inspector: [{ label: '连接', value: 'Factory / Local' }, { label: '服务地址', value: '127.0.0.1:5080' }, { label: '数据来源', value: '设计示例，非健康证据' }],
          empty: { title: '开始连接 SonnetDB', body: '添加一个连接后，可查看数据库、资源和最近工作区。', action: '添加连接' }
        },
        {
          id: 'database-catalog', title: '数据库目录', tabLabel: '数据库', type: 'table', scope: 'instance', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '查看可访问数据库，进入九模型统一资源上下文。SonnetMQ Topic 使用数据库逻辑命名空间；物理 Store 共享实例 .system/mq。', primary: '新建数据库',
          columns: ['名称', '访问权限', '主要资源', 'Segment', '操作'], rows: [['factory', '管理', 'Telemetry · Assets · Manuals · DeviceEvents', '128', '打开'], ['analytics', '查询', 'DailySummary · FeatureVectors', '64', '打开'], ['sandbox', '查询', 'Examples · Sessions', '12', '打开']],
          fields: [{ label: '名称筛选', value: '', kind: 'input' }, { label: '权限', value: '全部', kind: 'select' }], tabs: ['可访问数据库', '最近访问'],
          inspector: [{ label: '选中数据库', value: 'factory' }, { label: '名称解析', value: '保留原始拼写' }, { label: '危险操作', value: '删除前预览资源与影响范围' }],
          empty: { title: '暂无可访问数据库', body: '可请求授权，管理员可新建数据库。', action: '查看当前权限' }
        },
        {
          id: 'connections', title: '连接管理', tabLabel: '连接', type: 'connection', scope: 'host', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '复用现有远程连接对话框，整理连接配置、凭据位置和测试结果。测试只读取服务健康与能力。', primary: '添加连接',
          columns: ['连接名称', '地址', '宿主', '凭据', '状态'], rows: [['Factory / Local', 'http://127.0.0.1:5080', 'Web / Studio', '当前会话', '已选择 · 示例'], ['Factory Readonly · 示例', 'https://db.example.internal', 'Web / VS Code', '宿主安全存储', '未测试'], ['Managed Local', 'http://127.0.0.1:5080', 'Studio', 'Windows 凭据管理器', '仅 Studio 可用']],
          fields: [{ label: '名称', value: 'Factory / Local', kind: 'input' }, { label: '服务地址', value: 'http://127.0.0.1:5080', kind: 'input' }, { label: '默认数据库', value: 'factory', kind: 'select' }, { label: '认证方式', value: 'Token', kind: 'select' }],
          tabs: ['连接库', '连接测试', '凭据说明'], inspector: [{ label: '测试状态', value: '原型未执行网络请求' }, { label: '切换连接', value: '先保留当前草稿与工作区' }, { label: '密钥', value: '不进入共享工作区文件' }],
          empty: { title: '还没有保存的连接', body: '输入服务地址并选择凭据来源，然后测试连接。', action: '添加连接' }
        },
        {
          id: 'recent', title: '最近工作区', tabLabel: '最近工作区', type: 'table', scope: 'host', status: 'planned',
          intro: '恢复对象、输入和过滤器。恢复工作区不会重放写操作或重新执行查询。', primary: '恢复选中工作区',
          columns: ['工作区', '连接 / 数据库', '类型', '保存时间', '草稿'], rows: [['产线温度分析', 'Factory / Local / factory', 'SQL', '今天 09:42', '已保存'], ['Assets', 'Factory / Local / factory', '关系表', '今天 09:30', '2 处未提交'], ['设备异常排查', 'Factory Readonly · 示例 / factory', 'Notebook · 规划', '昨天 16:20', '只读']],
          fields: [{ label: '搜索工作区', value: '', kind: 'input' }], tabs: ['最近', '已固定', '离线快照'],
          inspector: [{ label: '恢复内容', value: '页签 / 输入 / 过滤器 / 结果快照' }, { label: '连接检查', value: '恢复后重新核对权限' }, { label: 'M47 状态', value: 'Workspace Registry 规划' }],
          empty: { title: '还没有保存的工作区', body: '查询或对象浏览时可保存当前上下文。', action: '打开 SQL 工作区' }
        }
      ]
    },
    {
      id: 'workbench', label: '工作台', icon: 'code', pages: [
        {
          id: 'sql', title: 'SQL 工作区', tabLabel: 'SQL 01', type: 'sql', scope: 'database', status: 'existing', existingRoute: '/admin/app/sql',
          intro: '编写、选中执行和解释 SQL；结果与对象 Inspector 在当前数据库上下文内连续。', primary: '执行查询',
          columns: ['time', 'DeviceID', 'AvgTemperature', 'PointCount'], rows: [['2026-10-04 09:00:00', 'Pump-01', '67.4', '120'], ['2026-10-04 09:00:00', 'Pump-02', '65.8', '118'], ['2026-10-04 09:05:00', 'Pump-01', '68.1', '120'], ['2026-10-04 09:05:00', 'Pump-02', '66.2', '119']],
          fields: [{ label: '数据库', value: 'factory', kind: 'select' }, { label: '结果上限', value: '1,000 行', kind: 'select' }, { label: '超时预算', value: '30 秒', kind: 'select' }],
          tabs: ['编辑器', '参数', '查询说明'], inspector: [{ label: '选中行', value: 'Pump-01 · 09:00' }, { label: '名称合同', value: 'DeviceID 保留原始拼写' }, { label: '结果来源', value: '静态设计数据' }, { label: '写操作', value: '转入暂存预览与审批' }],
          empty: { title: '执行查询后查看结果', body: '支持 Table、Raw、Chart 和 Explain；分页与截断按服务端能力显示。', action: '插入 SELECT 示例' },
          capabilities: [
            { id: 'sql-read', status: 'existing', label: 'SELECT / EXPLAIN 与结果视图', note: '沿用现有 SQL 工作区；原型只展示静态结果。' },
            { id: 'sql-write-preview', status: 'extension', label: '写语句暂存预览与审批', note: '能力由服务端预检、权限和影响数量决定。' },
            { id: 'sql-continuation', status: 'planned', label: '通用 SQL NDJSON continuation', note: '未有统一合同时不显示可恢复游标。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'existing', summary: '显示已加载结果、耗时和当前预算。', primary: '执行查询', fields: ['数据库', '结果上限', '超时预算'] },
            empty: { label: '空结果', status: 'existing', summary: '查询成功但返回 0 行，保留 SQL、参数和过滤器。', primary: '调整筛选', preserve: ['SQL 输入', '数据库上下文'] },
            error: { label: '读取错误', status: 'existing', summary: '只替换结果面板，保留编辑器与其它页签。', primary: '检查并重试', preserve: ['SQL 输入', '参数'] },
            permission: { label: '无权限', status: 'existing', summary: '隐藏结果载荷，显示所需数据库/对象权限。', primary: '查看所需权限', preserve: ['目标数据库', 'SQL 输入'] },
            readonly: { label: '只读', status: 'existing', summary: '查询、Explain、导出可用；写语句转为不可提交的预览。', primary: '导出当前结果', blocked: ['确认执行', '删除'] },
            longContent: { label: '长内容', status: 'extension', summary: '编辑器与 Result Plane 保持可滚动，结果按行/字节预算截断。', primary: '查看截断说明', limits: ['100 行 / 1 MiB（示例预算）', '不虚构 continuation'] }
          }
        },
        {
          id: 'notebook', title: 'SQL Notebook', tabLabel: '异常排查', type: 'notebook', scope: 'database', status: 'planned',
          intro: '把说明、SQL 单元和结果快照组织成可复核的排查文档。Notebook 为 M47 规划。', primary: '运行当前单元',
          columns: ['单元', '类型', '内容', '状态'], rows: [['01', '说明', '核对 Pump-01 的温度与维护记录', '已保存 · 示例'], ['02', 'SQL', 'SELECT … FROM Telemetry …', '未执行'], ['03', 'SQL', 'SELECT … FROM Assets …', '未执行'], ['04', '说明', '记录结论与证据边界', '草稿']],
          fields: [{ label: 'Notebook 名称', value: '设备异常排查', kind: 'input' }, { label: '数据库', value: 'factory', kind: 'select' }], tabs: ['单元', '大纲', '版本与导出'],
          inspector: [{ label: '当前单元', value: '02 · SQL' }, { label: '执行范围', value: '单元内 SQL，仅手动执行' }, { label: '可提交资产', value: '不含密钥与生产数据快照' }],
          empty: { title: '从一段说明开始', body: '添加说明或 SQL 单元，保存排查过程。', action: '添加 SQL 单元' }
        },
        {
          id: 'history', title: '执行历史', tabLabel: '执行历史', type: 'table', scope: 'host', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '复用现有 History Drawer，按连接、数据库和模型找回查询上下文。', primary: '恢复输入',
          columns: ['时间', '数据库 / 对象', '动作', '状态', '耗时', '结果'], rows: [['09:42:16', 'factory / Telemetry', 'SELECT', '成功 · 示例', '24 ms', '4 行'], ['09:37:08', 'factory / Assets', '写入预览', '待确认 · 示例', '—', '2 行草稿'], ['09:31:54', 'factory / Manuals', '全文查询', '截断 · 示例', '18 ms', '前 25 条']],
          fields: [{ label: '动作', value: '全部', kind: 'select' }, { label: '状态', value: '全部', kind: 'select' }, { label: '时间范围', value: '今天', kind: 'select' }], tabs: ['查询', '模型操作', '审批记录'],
          inspector: [{ label: '恢复方式', value: '仅恢复输入，不自动执行' }, { label: '历史来源', value: '本地工作台历史' }, { label: '审计区别', value: '服务端审计请到治理' }],
          empty: { title: '还没有执行历史', body: '手动执行查询后，这里保存输入、状态与耗时。', action: '打开 SQL 工作区' }
        }
      ]
    },
    {
      id: 'observe', label: '观测', icon: 'activity', pages: [
        {
          id: 'metrics', title: '性能指标', tabLabel: '性能指标', type: 'overview', scope: 'instance', status: 'existing', existingRoute: '/admin/app/monitoring',
          intro: '复用 Prometheus 指标与采样窗口；未启用指标时展示配置原因。原型数值为示例。', primary: '刷新指标',
          columns: ['数据库', 'MemTable', '数据点', 'Segment', '待 Flush'], rows: [['factory', '24.8 MB', '82,430', '128', '1'], ['analytics', '8.2 MB', '24,100', '64', '0'], ['sandbox', '1.1 MB', '2,050', '12', '0']],
          fields: [{ label: '写入速率', value: '2.4k 点/秒 · 示例' }, { label: '查询 P95', value: '24 ms · 示例' }, { label: 'WAL fsync P95', value: '3.2 ms · 示例' }, { label: '窗口', value: '最近 10 分钟' }],
          tabs: ['吞吐与延迟', '内存与存储', 'AI 用量'], inspector: [{ label: '采样来源', value: '/metrics' }, { label: '采样时间', value: '原型未采样' }, { label: '能力降级', value: '最小指标集可单独展示' }],
          empty: { title: '等待指标采样', body: '若完整指标未启用，仍可查看最小指标集及启用说明。', action: '查看采样说明' }
        },
        {
          id: 'events', title: '实时事件', tabLabel: '实时事件', type: 'table', scope: 'instance', status: 'existing', existingRoute: '/admin/app/events',
          intro: '查看数据库与运行时事件；暂停只暂停当前视图接收。', primary: '暂停视图',
          columns: ['时间', '级别', '来源', '数据库', '事件'], rows: [['09:42:18', '信息', 'SQL', 'factory', '查询结束 · 示例'], ['09:40:04', '信息', 'Catalog', 'sandbox', '数据库已加载 · 示例'], ['09:35:50', '警告', 'SSE', '实例', '事件连接重连 · 示例']],
          fields: [{ label: '级别', value: '全部', kind: 'select' }, { label: '来源', value: '全部', kind: 'select' }], tabs: ['事件流', '数据库事件', '连接状态'],
          inspector: [{ label: '传输', value: 'SSE' }, { label: '连接中断', value: '保留已接收事件并显示时间' }, { label: '事件留存', value: '本地显示不等同持久审计' }],
          empty: { title: '尚未收到事件', body: '连接建立后等待下一条运行时或数据库事件。', action: '检查事件连接' }
        },
        {
          id: 'slow-queries', title: '慢查询', tabLabel: '慢查询', type: 'table', scope: 'instance', status: 'extension', existingRoute: '/admin/app/events',
          intro: '将已有事件页与 SlowQueryDrawer 的慢查询入口整理为同一任务。', primary: '打开查询输入',
          columns: ['时间', '数据库', '耗时', '行数', '状态', 'SQL 摘要'], rows: [['09:22:16', 'factory', '824 ms', '10,000', '成功 · 示例', 'SELECT … FROM Telemetry'], ['09:15:08', 'analytics', '612 ms', '0', '失败 · 示例', 'SELECT … JOIN …']],
          fields: [{ label: '数据库', value: '全部', kind: 'select' }, { label: '最短耗时', value: '500 ms', kind: 'input' }, { label: '时间范围', value: '最近 1 小时', kind: 'select' }], tabs: ['慢查询列表', '选中 SQL', '解释入口'],
          inspector: [{ label: '阈值', value: '以服务端配置为准' }, { label: 'SQL 内容', value: '按权限展示并支持脱敏' }, { label: 'EXPLAIN', value: '手动打开，避免自动重跑' }],
          empty: { title: '窗口内没有慢查询', body: '可扩大时间窗口，或查看当前慢查询阈值。', action: '查看阈值说明' }
        },
        {
          id: 'alerts', title: '告警规则', tabLabel: '告警规则', type: 'form', scope: 'instance', status: 'planned',
          intro: '规划阈值、窗口与通知路由。当前管理路由未提供统一告警配置。', primary: '预览规则',
          columns: ['规则', '指标', '窗口', '阈值', '启用'], rows: [['查询延迟', 'query P95', '5 分钟', '> 500 ms', '规划草稿'], ['磁盘容量', 'free bytes', '10 分钟', '< 10 GB', '规划草稿']],
          fields: [{ label: '规则名称', value: '查询延迟', kind: 'input' }, { label: '指标', value: 'query P95', kind: 'select' }, { label: '评估窗口', value: '5 分钟', kind: 'select' }, { label: '阈值', value: '500 ms', kind: 'input' }, { label: '通知路由', value: '尚未配置', kind: 'select' }], tabs: ['规则', '评估记录', '通知路由'],
          inspector: [{ label: '后端状态', value: '统一告警接口待规划' }, { label: '通知动作', value: '本原型不会发送消息' }, { label: '权限', value: '实例管理权限' }],
          empty: { title: '尚未配置告警', body: '先确认指标来源和通知路由，再创建规则草稿。', action: '创建规则草稿' }
        },
        {
          id: 'runtime', title: '运行时诊断', tabLabel: '运行时', type: 'table', scope: 'instance', status: 'planned',
          intro: '规划引擎任务、请求预算和诊断采集的统一入口；不把缺失的运行时 DTO 表示为已有。', primary: '预览诊断采集',
          columns: ['组件', '范围', '可见信息', '设计状态'], rows: [['WAL / Flush', '数据库', '积压、最近错误、时间戳', '能力适配待确认'], ['SonnetMQ', 'factory · Store 为实例共享', 'topic / consumer runtime', '复用已有 MQ 运行时'], ['Graph Beta', '数据库', '遍历诊断、预算、审计', '复用已有 Graph 能力']],
          fields: [{ label: '组件', value: '全部', kind: 'select' }, { label: '诊断预算', value: '30 秒 / 5 MB', kind: 'input' }], tabs: ['运行时', '诊断采集', '预算与取消'],
          inspector: [{ label: '采集边界', value: '有界、可取消、按权限脱敏' }, { label: '实现状态', value: '聚合视图规划' }, { label: '证据', value: '原型不生成运行诊断报告' }],
          empty: { title: '当前能力未提供诊断信息', body: '可查看已暴露指标，或转入对应模型运行时。', action: '查看能力矩阵' }
        }
      ]
    },
    {
      id: 'flows', label: '数据流', icon: 'flow', pages: [
        {
          id: 'modbus', title: 'Modbus TCP', tabLabel: 'Modbus', type: 'table', scope: 'instance', status: 'existing', existingRoute: '/admin/app/modbus',
          intro: '复用现有 Runtime、Pending、Audit；源、端点和表绑定保持各自权限。', primary: '刷新状态',
          columns: ['源 / 端点', '模式', '地址', '数据库 / 表', '状态'], rows: [['PumpLine-A', 'Master', '10.20.0.15:502', 'factory / Telemetry', '示例配置'], ['LocalSlave', 'Slave', '0.0.0.0:1502', 'factory / RegisterMap', '示例配置']],
          fields: [{ label: '数据库', value: 'factory', kind: 'select' }, { label: 'Runtime', value: '以服务端返回为准' }], tabs: ['运行时', '待审批写入', '审计'],
          inspector: [{ label: '选中源', value: 'PumpLine-A' }, { label: '绑定', value: 'Telemetry / RegisterMap' }, { label: '现场写入', value: '批准与拒绝复用现有流程' }],
          empty: { title: '尚无 Modbus 源或端点', body: '由管理员配置运行时，之后可查看绑定与待审批写入。', action: '查看配置说明' }
        },
        {
          id: 'imports', title: '导入任务', tabLabel: '导入任务', type: 'table', scope: 'database', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '统一呈现已有模型导入入口；持久 resume 仅在真实后端能力存在时开放。', primary: '选择导入目标',
          columns: ['任务', '模型 / 目标', '格式', '进度', '状态'], rows: [['Telemetry_1004.csv', '时序 / Telemetry', 'CSV', '预览 100 行', '待确认 · 示例'], ['Assets.csv', '关系 / Assets', 'CSV', '映射 8 列', '草稿 · 示例'], ['Manuals.ndjson', '文档 / Manuals', 'NDJSON', '校验 20 文档', '草稿 · 示例']],
          fields: [{ label: '模型', value: '全部', kind: 'select' }, { label: '任务状态', value: '全部', kind: 'select' }], tabs: ['任务列表', '字段映射', '校验与错误'],
          inspector: [{ label: '文件来源', value: '本机选择，尚未上传' }, { label: '恢复能力', value: '按模型能力显示' }, { label: '权限', value: '目标数据库写入 / 导入权限' }],
          empty: { title: '还没有导入任务', body: '先选择模型与目标对象，检查字段映射及重复记录策略。', action: '选择导入目标' }
        },
        {
          id: 'transfers', title: '对象传输', tabLabel: '对象传输', type: 'table', scope: 'database', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '整理现有对象上传下载和 Multipart；明确字节、分片、校验与取消状态。', primary: '新建上传',
          columns: ['对象', '方向', '大小', '分片', '状态'], rows: [['evidence/2026-10-04/pump-01.jpg', '上传', '2.4 MB', '单文件', '待确认 · 示例'], ['archive/line-a-1004.zip', '上传', '128 MB', '3 / 8', '可恢复会话 · 示例'], ['manuals/pump-guide.pdf', '下载', '4.8 MB', 'Range', '未开始 · 示例']],
          fields: [{ label: 'Bucket', value: 'Evidence', kind: 'select' }, { label: '方向', value: '全部', kind: 'select' }], tabs: ['传输', 'Multipart 会话', '校验与错误'],
          inspector: [{ label: '目标 Bucket', value: 'Evidence' }, { label: '校验', value: '以服务器 checksum 为准' }, { label: '大文件', value: '按能力显示 Range 与分片' }],
          empty: { title: '还没有对象传输', body: '选择 Bucket 与文件，检查大小、版本和校验方式。', action: '新建上传' }
        },
        {
          id: 'jobs', title: '任务与位点', tabLabel: '任务与位点', type: 'table', scope: 'instance', status: 'planned',
          intro: '规划任务、位点、重试和派生发布的统一索引；实际 resume 继续由各模型合同决定。', primary: '查看选中任务',
          columns: ['任务', '范围', '阶段', '位点 / 版本', '可用动作'], rows: [['RAG Manuals', 'factory / copilot-docs', '待续跑 · 示例', 'generation demo-07', '转至 RAG'], ['对象语义处理', 'factory / Evidence', '待处理 · 示例', 'object version demo-12', '转至对象'], ['MQ 消费', 'factory / DeviceEvents · 实例共享 Store', '浏览示例', 'offset 10240', '转至 SonnetMQ']],
          fields: [{ label: '范围', value: '实例与数据库', kind: 'select' }, { label: '阶段', value: '全部', kind: 'select' }], tabs: ['任务索引', '位点', '重试与恢复'],
          inspector: [{ label: '统一 DTO', value: '规划，尚无通用任务接口' }, { label: '恢复边界', value: '不可把客户端重试当持久 resume' }, { label: '取消', value: '区分取消请求与服务器终态' }],
          empty: { title: '当前没有可展示的任务', body: '按模型能力查看任务或恢复入口。', action: '查看任务能力' }
        }
      ]
    },
    {
      id: 'ai', label: 'AI 与 MCP', icon: 'spark', pages: [
        {
          id: 'ai-connect', title: 'AI Connect', tabLabel: 'AI Connect', type: 'ai-connect', scope: 'database', status: 'planned',
          intro: '规划 WorkBuddy、Claude、Cursor、Codex 接入；复用 typed HTTP MCP，stdio bridge 尚未实现。', primary: '生成配置预览',
          columns: ['客户端', '传输', '配置范围', '当前设计状态'], rows: [['WorkBuddy', 'HTTP / stdio', '用户 / 项目', '配置向导规划'], ['Claude', 'HTTP / stdio', '用户 / 项目', '配置向导规划'], ['Cursor', 'HTTP / stdio', '项目', '配置向导规划'], ['Codex', 'HTTP / stdio', '用户 / 项目', '配置向导规划']],
          fields: [{ label: '客户端', value: 'WorkBuddy', kind: 'select' }, { label: '传输方式', value: 'Streamable HTTP', kind: 'select' }, { label: '数据库', value: 'factory', kind: 'select' }, { label: '配置范围', value: '用户', kind: 'select' }, { label: '结果预算', value: '最多 100 行 / 1 MB / 30 秒', kind: 'input' }],
          tabs: ['连接向导', '配置预览', '工具列表', '自检', '数据外发'], inspector: [{ label: 'HTTP Endpoint', value: '/mcp/factory' }, { label: '工具边界', value: '默认只读' }, { label: 'stdio bridge', value: 'M47-U08 规划' }, { label: '凭据', value: '配置示例使用环境变量引用' }],
          empty: { title: '连接一个 AI 客户端', body: '先选择客户端与数据库，检查工具范围和配置落点。', action: '开始接入向导' }
        },
        {
          id: 'copilot-settings', title: 'Copilot 与 Provider', tabLabel: 'Provider', type: 'form', scope: 'instance', status: 'existing', existingRoute: '/admin/app/ai-settings',
          intro: '复用已有账号绑定、Provider、模型目录、测试和用量；真实模型质量与成本单独验收。', primary: '预览配置变更',
          columns: ['用途', 'Provider / 模型', '配置来源', '状态'], rows: [['Chat', '平台模型 / 由服务端返回', 'sonnetdb.com 账号', '未读取 · 原型'], ['Embedding', '显式 profile', '服务端配置', '未读取 · 原型']],
          fields: [{ label: 'Provider', value: '以当前服务器配置为准', kind: 'select' }, { label: 'Chat 模型', value: '尚未加载模型目录', kind: 'select' }, { label: '外发范围', value: '仅显式允许内容', kind: 'select' }, { label: '凭据', value: '••••••••', kind: 'secret' }],
          tabs: ['Provider', '账号绑定', '模型目录', '用量', '测试'], inspector: [{ label: '管理权限', value: '实例管理员' }, { label: '测试边界', value: '连接成功不等同质量门禁' }, { label: '估算用量', value: '缺 usage 时单独标注估算' }],
          empty: { title: 'Provider 尚未配置', body: '管理员可绑定账号或配置受支持 Provider。', action: '查看配置步骤' }
        },
        {
          id: 'rag', title: 'RAG 管理', tabLabel: 'RAG', type: 'table', scope: 'database', status: 'existing', existingRoute: '/admin/app/rag',
          intro: '复用已发布快照、持久任务续跑、派生重建与清理；不将已有能力重新列为空壳。', primary: '预览重建 / 换代',
          columns: ['Stream', 'Active revision', 'Profile', '内容 / 分块', '待续跑任务'], rows: [['copilot-docs', '7 · 示例', 'factory-text-v2 · 示例', '128 / 864 · 示例', '待处理 · 示例'], ['maintenance-notes', '3 · 示例', 'factory-text-v2 · 示例', '42 / 210 · 示例', '无 · 示例']],
          fields: [{ label: '数据库', value: 'factory', kind: 'select' }, { label: 'Stream', value: 'copilot-docs', kind: 'input' }, { label: '目标 profile', value: '选择服务器已配置 profile', kind: 'select' }], tabs: ['已发布快照', '持久任务', '派生重建', '退役清理', '审计'],
          inspector: [{ label: '模型身份', value: 'Provider / model / revision / dimensions' }, { label: '发布边界', value: '新版本完整发布前保留当前版本' }, { label: '写入', value: '服务器校验数据库管理权限' }],
          empty: { title: '尚无已发布快照', body: '先通过实际摄取入口创建首个版本，再在这里重建与维护。', action: '查看摄取说明' }
        },
        {
          id: 'tool-permissions', title: '工具权限与外发', tabLabel: '工具权限', type: 'table', scope: 'database', status: 'planned',
          intro: '规划可见的 MCP 工具清单、预算和外发范围；默认只读，写入走工作台审批。', primary: '预览权限范围',
          columns: ['工具类别', '数据范围', '权限', '预算', '外发'], rows: [['Schema / describe', 'factory schema', '只读', '有界 metadata', '按宿主配置'], ['SQL select / explain', 'factory', '只读', '100 行 / 1 MB / 30 秒', '按显式允许范围'], ['模型 browse / search', '已授权对象', '只读', '按模型预算', '按宿主配置'], ['写入 / 删除', '工作台审批', 'MCP 不提供', '—', '—']],
          fields: [{ label: '客户端', value: '全部', kind: 'select' }, { label: '工具类别', value: '全部', kind: 'select' }], tabs: ['工具权限', '结果预算', '数据外发', '调用记录'],
          inspector: [{ label: '有效权限', value: '用户权限 ∩ 工具权限 ∩ 宿主策略' }, { label: '权限状态', value: '设计示例，不是实际授权' }, { label: '服务端校验', value: '任何客户端开关不能扩大权限' }],
          empty: { title: '尚未加载工具清单', body: 'AI Connect 自检成功后，按当前身份显示实际 tools/list。', action: '打开 AI Connect' }
        }
      ]
    },
    {
      id: 'govern', label: '治理', icon: 'shield', pages: [
        {
          id: 'users', title: '用户', tabLabel: '用户', type: 'table', scope: 'instance', status: 'existing', existingRoute: '/admin/app/users',
          intro: '复用 CREATE / ALTER / DROP USER 管理；凭据不出现在表格、历史或配置导出中。', primary: '新建用户',
          columns: ['用户名', '超级用户', '创建时间', 'Token 数', '操作'], rows: [['admin', '是', '2026-09-01 · 示例', '2', '改密'], ['analyst', '否', '2026-09-12 · 示例', '1', '授权 / 改密'], ['collector', '否', '2026-09-20 · 示例', '1', '授权 / 改密']],
          fields: [{ label: '名称筛选', value: '', kind: 'input' }], tabs: ['用户列表', '选中用户授权', '凭据管理'],
          inspector: [{ label: '选中用户', value: 'analyst' }, { label: '权限', value: '仅超级用户管理' }, { label: '删除用户', value: '确认名称与关联凭据影响' }],
          empty: { title: '没有匹配的用户', body: '调整名称过滤条件。仅有权限的管理员可创建用户。', action: '清除筛选' }
        },
        {
          id: 'grants', title: '数据库授权', tabLabel: '数据库授权', type: 'table', scope: 'instance', status: 'existing', existingRoute: '/admin/app/grants',
          intro: '复用数据库授权，明确用户、数据库、权限与撤销影响。', primary: '预览授权',
          columns: ['用户', '数据库', '权限', '来源', '操作'], rows: [['analyst', 'factory', '查询', '直接授权 · 示例', '查看 / 撤销'], ['collector', 'factory', '写入', '直接授权 · 示例', '查看 / 撤销'], ['analyst', 'analytics', '查询', '直接授权 · 示例', '查看 / 撤销']],
          fields: [{ label: '用户', value: 'analyst', kind: 'select' }, { label: '数据库', value: 'factory', kind: 'select' }, { label: '权限', value: '查询', kind: 'select' }], tabs: ['授权列表', '授权预览', '有效权限'],
          inspector: [{ label: '权限范围', value: 'MQ Topic Read/Write 按数据库授权；实例 Store 恢复/全局配置另行核验' }, { label: '撤销', value: '预览受影响身份与工作区' }, { label: '前端', value: '禁用动作并展示原因' }],
          empty: { title: '没有匹配的授权', body: '选择用户和数据库查看授权，管理员可添加授权草稿。', action: '清除筛选' }
        },
        {
          id: 'tokens', title: '访问 Token', tabLabel: 'Token', type: 'table', scope: 'instance', status: 'existing', existingRoute: '/admin/app/tokens',
          intro: '复用 Token 发放和撤销；新凭据仅在创建时展示一次。', primary: '创建 Token',
          columns: ['标识', '所属用户', '创建时间', '有效期', '状态'], rows: [['tk_…a71c', 'analyst', '2026-10-01 · 示例', '30 天 · 示例', '有效 · 示例'], ['tk_…b209', 'collector', '2026-09-20 · 示例', '以服务端合同为准', '有效 · 示例']],
          fields: [{ label: '用户', value: '全部', kind: 'select' }, { label: '状态', value: '全部', kind: 'select' }], tabs: ['Token 列表', '创建', '安全说明'],
          inspector: [{ label: 'Token 内容', value: '不可在列表中恢复显示' }, { label: '复制', value: '创建成功后的一次性提示' }, { label: '撤销', value: '确认标识与所属用户' }],
          empty: { title: '尚无可显示的 Token', body: '管理员可为已存在用户创建凭据。', action: '选择用户' }
        },
        {
          id: 'approvals', title: '审批与审计', tabLabel: '审批与审计', type: 'table', scope: 'instance', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '统一索引已有 staged 操作与模型审计；各审批的有效期、作用域和权限以服务端合同为准。', primary: '查看影响预览',
          columns: ['审批', '范围 / 对象', '动作', '影响', '状态'], rows: [['draft-001', 'factory / Assets', '更新记录', '2 行 · 示例', '本地草稿'], ['demo-graph-002', 'factory / Graph Beta', '维护', '预算预览 · 示例', '暂存示例'], ['demo-modbus-003', '实例 / LocalSlave', '端点写入', '4 个寄存器 · 示例', '待审批示例']],
          fields: [{ label: '范围', value: '全部', kind: 'select' }, { label: '状态', value: '全部', kind: 'select' }], tabs: ['待处理', '影响预览', '执行记录', '审计'],
          inspector: [{ label: '统一列表', value: '延伸设计，未有通用审批 DTO' }, { label: '确认信息', value: '连接 / 范围 / 对象 / 数量 / 风险' }, { label: '审计', value: '本地草稿与服务器记录分开' }],
          empty: { title: '没有待处理审批', body: '危险操作先形成草稿并预览影响，确认后才执行。', action: '查看审计' }
        },
        {
          id: 'backup', title: '数据库备份与恢复', tabLabel: '数据库备份', type: 'table', scope: 'database', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '复用现有数据库备份状态与验证入口。MQ Topic 逻辑属于数据库，但单库备份尚未覆盖实例共享 .system/mq Store。', primary: '预览备份',
          columns: ['备份', '数据库', '范围', '验证', '设计状态'], rows: [['factory_snapshot_demo', 'factory', '单数据库', '未执行', '备份示例'], ['factory_restore_demo', 'factory', '数据库恢复', '未执行', '恢复草稿']],
          fields: [{ label: '数据库', value: 'factory', kind: 'select' }, { label: '备份位置', value: '由服务端或 Studio 文件对话框选择', kind: 'input' }, { label: '恢复目标', value: '新数据库 / 明确选择', kind: 'select' }], tabs: ['备份状态', '备份预览', '验证', '恢复预览', '范围说明'],
          inspector: [{ label: '持久化边界', value: '当前单库备份为数据库目录' }, { label: 'SonnetMQ', value: '数据库逻辑命名空间；物理 .system/mq 共享实例 Store，未纳入单库备份' }, { label: '恢复风险', value: '必须检查版本、目录和覆盖目标；MQ Store 恢复另行核验' }],
          empty: { title: '尚未选择备份', body: '先检查数据库备份能力、路径和覆盖范围。', action: '查看备份能力' }
        }
      ]
    },
    {
      id: 'settings', label: '设置', icon: 'settings', pages: [
        {
          id: 'preferences', title: '工作台偏好', tabLabel: '偏好', type: 'form', scope: 'host', status: 'planned',
          intro: '规划三面一致的密度、编辑器、结果与布局偏好；以当前 Fluent 浅色工作台为默认。', primary: '保存偏好草稿',
          columns: [], rows: [], fields: [{ label: '主题', value: 'Fluent 浅色', kind: 'select' }, { label: '密度', value: '标准 · 42px 行高', kind: 'select' }, { label: '编辑器字号', value: '13 px', kind: 'input' }, { label: '默认结果上限', value: '1,000 行', kind: 'input' }, { label: 'Explorer', value: '304 px · 可调整', kind: 'input' }, { label: 'Inspector', value: '344 px · 可调整', kind: 'input' }],
          tabs: ['外观', '编辑器', '结果', '快捷键', '工作区'], inspector: [{ label: '保存位置', value: '当前宿主本地偏好' }, { label: '不含内容', value: '凭据、服务器权限与预算上限' }, { label: '重置', value: '仅重置界面偏好' }],
          empty: { title: '使用默认工作台偏好', body: '可调整布局、密度和编辑器，设置不扩大服务端预算。', action: '恢复默认偏好' }
        },
        {
          id: 'server-settings', title: '实例配置', tabLabel: '实例配置', type: 'form', scope: 'instance', status: 'planned',
          intro: '规划可读配置与能力来源，禁止把未有 API 的服务配置做成可在线保存。', primary: '查看配置说明',
          columns: ['配置领域', '当前来源', '修改方式', '设计状态'], rows: [['可观测性', 'appsettings.json', '配置文件 / 重启', '只读说明'], ['请求预算', '服务端能力', '按真实管理接口', '待适配'], ['MQ 持久目录', '实例运行配置', '实例维护流程', '只读说明']],
          fields: [{ label: 'Prometheus', value: '以服务端状态为准' }, { label: 'SQL 预算', value: '以能力响应为准' }, { label: 'MQ 持久目录', value: '实例共享 .system/mq；Topic 逻辑按数据库划分' }], tabs: ['服务配置', '预算', '可观测性', '重启影响'],
          inspector: [{ label: '管理员', value: '只读配置摘要与变更说明' }, { label: '在线保存', value: '仅真实接口支持后启用' }, { label: '凭据', value: '摘要不展示敏感字段' }],
          empty: { title: '实例未提供配置摘要', body: '查看配置说明，或请求管理员提供允许读取的配置。', action: '查看服务能力' }
        },
        {
          id: 'studio-host', title: 'Studio 宿主', tabLabel: 'Studio 宿主', type: 'connection', scope: 'host', status: 'extension',
          intro: '复用既有 Studio bridge 与托管 Server 合同；实机安装与卸载证据仍待独立验证。', primary: '检查宿主能力',
          columns: ['能力', 'Web Admin', 'Studio', 'VS Code'], rows: [['原生文件对话框', '浏览器选择', 'Native bridge', '扩展文件 API'], ['Managed Local', '不适用', '已有宿主合同', 'Remote-first'], ['凭据存储', '当前会话', 'Windows 安全存储', 'SecretStorage']],
          fields: [{ label: '宿主', value: 'Web Admin · 原型', kind: 'select' }, { label: 'Managed Local', value: '仅 Studio 显示' }, { label: 'Data root', value: '宿主返回后显示' }, { label: '进程状态', value: '未读取 · 原型' }], tabs: ['宿主状态', 'Managed Local', '文件与凭据', '安装与升级', '进程生命周期'],
          inspector: [{ label: 'Web 降级', value: 'Native bridge 不可用时显示原因' }, { label: 'Start / Stop', value: '只管理宿主归属 Server' }, { label: '验收', value: '浏览器原型不替代干净 Windows 证据' }],
          empty: { title: '当前为浏览器宿主', body: '原生文件、托管本地服务和安装信息在 Studio 内可用。', action: '查看宿主边界' }
        },
        {
          id: 'capability-matrix', title: '能力与发布矩阵', tabLabel: '能力矩阵', type: 'table', scope: 'host', status: 'planned',
          intro: '规划 manifest、契约版本和三面能力；真实发布状态须来自独立证据。', primary: '查看发布检查项',
          columns: ['能力', 'Web Admin', 'Studio', 'VS Code', '设计状态'], rows: [['SQL / schema / Explain', '完整', '完整', '开发者入口', '已有基线'], ['九模型管理', '模型工作台', '同一 Web 工作台', '只读子集', '统一合同规划'], ['写审批 / 恢复', '治理面', '治理面 + native bridge', '深链接转交', '按模型合同'], ['MCP 配置 / 自检', '规划向导', '规划向导', '宿主适配', 'M47-U08'], ['安装 / 市场发布', '独立门禁', '干净 Windows 门禁', 'Extension Host 门禁', '未就绪']],
          fields: [{ label: '宿主', value: '全部', kind: 'select' }, { label: '能力状态', value: '全部', kind: 'select' }], tabs: ['宿主能力', '契约版本', '兼容性', '发布证据'],
          inspector: [{ label: '版本来源', value: 'manifest 待统一' }, { label: '局部证据', value: '不能升级为三面发布完成' }, { label: 'Graph', value: '保留 Beta 标记' }],
          empty: { title: '尚无统一 manifest', body: '可以查看规划矩阵，实际版本与能力由宿主和服务响应填充。', action: '查看规划范围' }
        },
        {
          id: 'about', title: '关于与帮助', tabLabel: '关于', type: 'overview', scope: 'host', status: 'existing', existingRoute: '/admin/app/about',
          intro: '统一 SonnetDB Workbench 品牌、版本、帮助、快捷键与支持信息。', primary: '查看使用指南',
          columns: ['入口', '内容', '范围'], rows: [['工作台指南', '连接 → 对象 → 查询 → 结果', '三个宿主'], ['模型指南', '九模型语义与限制', '数据库 / 实例'], ['安全与权限', '凭据、审批、外发和预算', '身份与宿主'], ['问题反馈', '诊断信息与版本摘要', '去除敏感数据']],
          fields: [{ label: '产品', value: 'SonnetDB Workbench' }, { label: '当前宿主', value: 'Web Admin 原型' }, { label: '版本', value: '以真实发行物为准' }, { label: '原型', value: 'M47 · 2026-10-04' }], tabs: ['产品信息', '指南', '快捷键', '许可证'],
          inspector: [{ label: '诊断摘要', value: '复制前预览敏感字段' }, { label: '发行物', value: 'Web Admin / Studio / VSIX' }, { label: '实现进度', value: '由路线图和验证记录判断' }],
          empty: { title: '查看 SonnetDB 使用指南', body: '从连接与查询开始，也可按模型查看专用工作台。', action: '打开工作台指南' }
        }
      ]
    }
  ],
  models: [
    {
      id: 'measurement', title: '时序 Measurement', objectName: 'Telemetry', scope: 'database', group: '时序', status: 'extension',
      intro: '复用数据点、文件导入、实时监控和 Schema；时间窗、TAG 与结果视图保持同一上下文。', primary: '查询数据点',
      tabs: ['数据点', 'SQL', '图表', '轨迹', 'Schema', '文件导入', '保留策略'],
      columns: ['time', 'DeviceID · TAG', 'Line · TAG', 'Temperature · FIELD', 'Pressure · FIELD'],
      rows: [['2026-10-04 09:00:00', 'Pump-01', 'Line-A', '67.4', '0.82'], ['2026-10-04 09:00:30', 'Pump-01', 'Line-A', '67.8', '0.83'], ['2026-10-04 09:01:00', 'Pump-02', 'Line-A', '65.8', '0.79'], ['2026-10-04 09:01:30', 'Pump-02', 'Line-A', '66.2', 'NULL']],
      inspector: [{ label: '对象', value: 'factory / Telemetry' }, { label: '时间范围', value: '09:00–10:00 · Asia/Shanghai' }, { label: '选中 Series', value: 'DeviceID=Pump-01, Line=Line-A' }, { label: '缺失 FIELD', value: 'NULL，不补造数值' }, { label: '名称合同', value: 'TAG/FIELD 保留创建拼写' }, { label: '证据', value: '静态示例，非实时采样' }],
      capabilities: [
        { id: 'measurement-points', status: 'existing', label: '时间窗与点数据读取', note: '按数据库 Measurement、时间范围和 TAG/FIELD 返回有界点；原型数据是静态示例。' },
        { id: 'measurement-schema', status: 'existing', label: 'TAG / FIELD Schema 检视', note: '保存创建时名称和角色；Point 摄取映射到既有拼写，不因大小写新增列。' },
        { id: 'measurement-import', status: 'existing', label: '文件导入映射与错误预览', note: '时间单位、时区、重复策略和行/字节限制需按真实端点核对。' },
        { id: 'measurement-chart', status: 'extension', label: '图表与轨迹结果视图', note: '只使用真实查询结果；NULL 不补零，轨迹须有适用坐标列。' },
        { id: 'measurement-retention', status: 'planned', label: '保留策略维护', note: '接口与影响计算尚未统一；当前只显示规划边界。' }
      ],
      stateMatrix: {
        normal: { label: '正常', status: 'existing', summary: '显示当前 Measurement 的时间窗、TAG/FIELD、点数和缺失值语义。', primary: '查询数据点', fields: ['数据库', 'Measurement', '时间范围', '时区', 'TAG / FIELD', '选中 Series', '结果上限'] },
        empty: { label: '空结果', status: 'existing', summary: '查询成功但没有点，保留 Measurement、时间窗和 TAG 过滤，不把空结果解释为 Schema 或存储故障。', primary: '调整时间范围', preserve: ['数据库 / Measurement identity', '时间范围', 'TAG 过滤'] },
        error: { label: '读取错误', status: 'extension', summary: '仅替换点结果区域，保留时间窗、TAG/FIELD 选择和其它页签。', primary: '检查并重试', preserve: ['Measurement', '时间范围', 'TAG/FIELD 选择'] },
        permission: { label: '无权限', status: 'existing', summary: '隐藏点值与 Schema 载荷，说明数据库 Measurement Read 权限来源。', primary: '查看数据库权限', preserve: ['数据库', 'Measurement'] },
        readonly: { label: '只读', status: 'existing', summary: '查询、图表、Schema、导出可用；写入、删除、文件导入和保留策略修改禁用并说明原因。', primary: '导出当前点数据', blocked: ['写入点', '删除点', '提交文件导入', '修改保留策略'] },
        longContent: { label: '长结果', status: 'extension', summary: '点表和图表按行/字节预算分页或折叠，保留时间、TAG、FIELD 原始拼写与 NULL；不加载无限历史。', primary: '查看有界结果', limits: ['最大点数与字节预算以服务端能力为准', '不虚构 continuation 或聚合证据'] }
      }
    },
    {
      id: 'table', title: '关系表', objectName: 'Assets', scope: 'database', group: '关系', status: 'existing',
      intro: '复用数据、设计器、索引、ER、DDL 和导入导出；行编辑先形成可审查草稿。', primary: '新建行草稿',
      tabs: ['数据', '设计器', '索引', 'ER 图', 'DDL', '导入 / 导出', 'Explain'],
      columns: ['AssetID', 'DeviceID', 'Name', 'Line', 'CommissionedAt', 'State'], rows: [['A-001', 'Pump-01', '冷却泵 01', 'Line-A', '2025-04-16', '运行'], ['A-002', 'Pump-02', '冷却泵 02', 'Line-A', '2025-04-16', '运行'], ['A-003', 'Valve-01', '调节阀 01', 'Line-B', '2025-05-03', '维护']],
      inspector: [{ label: '行主键', value: 'AssetID = A-001' }, { label: '选中列', value: 'DeviceID · STRING · NOT NULL' }, { label: '索引', value: '以实际 schema 返回为准' }, { label: '编辑状态', value: '草稿 → 差异预览 → 审批' }, { label: '结果限制', value: '分页与物化预算按能力显示' }],
      capabilities: [
        { id: 'table-data', status: 'existing', label: '行浏览与有界筛选', note: '筛选、排序、分页和物化预算遵循服务端能力；当前表格是静态设计样例。' },
        { id: 'table-schema', status: 'existing', label: '设计器、索引与 DDL 检视', note: '列名、类型、约束和索引以真实 schema 返回为准，SQL 名称保留原始拼写。' },
        { id: 'table-draft-write', status: 'extension', label: '行编辑与结构变更草稿', note: '新增、修改、ALTER、索引维护都先差异预览并经过服务端权限与审批。' },
        { id: 'table-import-export', status: 'existing', label: '导入 / 导出与列映射', note: '覆盖、追加、敏感列和失败报告必须明确范围，不把当前页当全表。' },
        { id: 'table-explain', status: 'extension', label: '共用 SQL Explain', note: '手动进入 Explain 结果；不自动重跑或虚构 planner 输出。' }
      ],
      stateMatrix: {
        normal: { label: '正常', status: 'existing', summary: '显示当前表的列、行、主键、分页与选中行 Inspector。', primary: '浏览数据', fields: ['数据库', '表', '筛选 / 排序', '分页上限', '物化预算', '选中主键', '草稿差异'] },
        empty: { label: '空结果', status: 'existing', summary: '筛选成功但没有行，保留表、筛选和排序输入，不把空结果解释为表不存在。', primary: '调整筛选', preserve: ['数据库 / 表 identity', '筛选 / 排序'] },
        error: { label: '读取错误', status: 'extension', summary: '只替换行结果区域，保留筛选、排序、草稿和设计器上下文。', primary: '检查并重试', preserve: ['表', '筛选 / 排序', '未提交草稿'] },
        permission: { label: '无权限', status: 'existing', summary: '隐藏行和字段载荷，说明数据库表 Read/Manage 权限要求。', primary: '查看数据库权限', preserve: ['数据库', '表'] },
        readonly: { label: '只读', status: 'existing', summary: '浏览、Schema、DDL、Explain 和当前结果导出可用；行编辑、ALTER、索引维护、导入覆盖和删除禁用。', primary: '导出当前结果', blocked: ['确认行修改', '执行 ALTER', '删除索引', '导入覆盖'] },
        longContent: { label: '长结果', status: 'extension', summary: '大结果按页与字节预算呈现，长字段折叠并保留类型/NULL；当前页不代表全表。', primary: '查看有界结果', limits: ['页面/物化上限以服务端能力为准', '不虚构全量行数或 continuation'] }
      }
    },
    {
      id: 'document', title: 'JSON 文档', objectName: 'Manuals', scope: 'database', group: '文档', status: 'existing',
      intro: '复用查询、更新、Validator、索引、Change Feed 和导入导出；JSON 属性键保持原语义。', primary: '执行 Find',
      tabs: ['Find', '文档', 'Aggregate', '索引', 'Validator', 'Change Feed', '导入 / 导出'],
      columns: ['_id', 'title', 'DeviceType', 'revision', 'updatedAt'], rows: [['manual-pump-01', '冷却泵维护手册', 'Pump', '2', '2026-09-28'], ['manual-valve-01', '调节阀点检说明', 'Valve', '1', '2026-09-18'], ['note-line-a-1004', 'Line-A 交接记录', 'Line', '4', '2026-10-04']],
      inspector: [{ label: '文档 ID', value: 'manual-pump-01' }, { label: '查看方式', value: 'JSON / Tree / Raw' }, { label: '示例 Payload', value: '{ "title": "冷却泵维护手册", "revision": 2 }' }, { label: '更新范围', value: '预览命中与局部差异' }, { label: '分页', value: '使用真实 continuation，不伪造游标' }],
      capabilities: [
        { id: 'document-find', status: 'existing', label: 'Find、筛选与真实 continuation', note: 'filter、projection、sort、limit 和服务端 continuation 保持文档上下文。' },
        { id: 'document-view', status: 'existing', label: 'JSON / Tree / Raw 检视', note: '属性键与数据值保持 JSON 原语义；长 Payload 只做有界呈现。' },
        { id: 'document-aggregate', status: 'extension', label: '受支持 Aggregate 阶段', note: '未知阶段明确拒绝，不宣称完整 MongoDB Pipeline 兼容。' },
        { id: 'document-index-validator', status: 'existing', label: '索引与 Validator 检查', note: '路径、字段、规则和错误位置来自真实 schema/validator 能力。' },
        { id: 'document-change-feed', status: 'existing', label: 'Change Feed 与导入导出', note: '断线、客户端接收时间和真实位点分开；replace/insert 需显式选择。' },
        { id: 'document-write-preview', status: 'extension', label: '更新与覆盖草稿预览', note: '写入、replace 和 Validator 变更必须展示差异、权限与影响。' }
      ],
      stateMatrix: {
        normal: { label: '正常', status: 'existing', summary: '显示当前 Collection 的文档 ID、字段、查询条件和 continuation 来源。', primary: '执行 Find', fields: ['数据库', '集合', 'Filter / Projection', 'Sort', 'Limit', 'Continuation', '文档 ID', 'Payload 呈现模式'] },
        empty: { label: '空结果', status: 'existing', summary: 'Find 成功但没有文档，保留集合、过滤和排序输入，不把空结果解释为索引损坏。', primary: '调整筛选', preserve: ['数据库 / 集合 identity', 'Filter / Projection', 'Sort'] },
        error: { label: '读取错误', status: 'extension', summary: '只替换文档结果区域，保留查询输入、选中 ID 和未提交草稿。', primary: '检查并重试', preserve: ['集合', 'Filter / Projection', 'Continuation'] },
        permission: { label: '无权限', status: 'existing', summary: '隐藏文档载荷与变更详情，说明数据库 Collection Read/Write 权限来源。', primary: '查看数据库权限', preserve: ['数据库', '集合'] },
        readonly: { label: '只读', status: 'existing', summary: 'Find、Tree/Raw、Change Feed、导出和索引查看可用；replace、Validator 修改、删除和导入写入禁用。', primary: '导出当前文档', blocked: ['更新文档', 'replace 覆盖', '修改 Validator', '导入写入'] },
        longContent: { label: '长文档', status: 'extension', summary: '长 JSON 默认折叠并显示大小，Tree/Raw 只展开有界路径；分页使用真实 continuation。', primary: '查看有限 Payload', limits: ['每页文档数与字节预算', '截断明确标记，不把截断文档当完整载荷', '不伪造 continuation'] }
      }
    },
    {
      id: 'kv', title: 'KV Keyspace', objectName: 'DeviceState', scope: 'database', group: 'KV', status: 'extension',
      intro: '复用前缀浏览、批量操作与统计；TTL 与类型化值入口合并到选中 key 的检查器。', primary: '读取 Key',
      tabs: ['浏览', '值检查器', 'TTL', '批量操作', '统计', '操作历史'],
      columns: ['Key', '类型', '大小', 'TTL', '版本 / CAS'], rows: [['device:Pump-01:state', 'JSON', '248 B', '59 秒 · 示例', '以服务端返回为准'], ['device:Pump-02:state', 'JSON', '232 B', '无过期', '以服务端返回为准'], ['line:A:last-checkpoint', 'Int64', '8 B', '无过期', '以服务端返回为准'], ['blob:calibration:01', 'Binary', '128 B', '无过期', '以服务端返回为准']],
      inspector: [{ label: 'Key', value: 'device:Pump-01:state' }, { label: '前缀', value: 'device:' }, { label: '查看方式', value: 'Text / JSON / Hex / Base64' }, { label: 'TTL', value: '显示剩余时长与绝对到期时间' }, { label: 'CAS', value: '仅能力支持时提供条件修改' }, { label: '键语义', value: '不套用 SQL 名称大小写合同' }],
      empty: { title: '当前前缀没有 Key', body: '保留 keyspace 与前缀输入，可调整前缀或清除过滤后重新读取。', action: '调整前缀' },
      capabilities: [
        { id: 'kv-scan', status: 'existing', label: 'Key 浏览与有界筛选', note: '页内结果不等同全 keyspace 扫描；continuation 以服务端能力为准。' },
        { id: 'kv-value-inspector', status: 'extension', label: 'Text / JSON / Hex / Base64 检查器', note: '保留原始字节，二进制不强制解码。' },
        { id: 'kv-conditional-write', status: 'extension', label: 'TTL / CAS 条件修改', note: '只有真实 API 返回支持时才开放写动作。' },
        { id: 'kv-full-stats', status: 'planned', label: '服务器全量统计', note: '当前只显示已加载页统计和未提供字段。' }
      ],
      stateMatrix: {
        normal: { label: '正常', status: 'existing', summary: '显示 key、类型、大小、TTL 和服务端版本。', primary: '读取 Key', fields: ['Keyspace', 'Prefix', '扫描上限'] },
        empty: { label: '空结果', status: 'existing', summary: '当前前缀没有 Key，不把空值冒充读取错误。', primary: '调整前缀', preserve: ['Keyspace', 'Prefix'] },
        error: { label: '读取错误', status: 'existing', summary: '保留筛选与已选 key，允许只重试当前 keyspace。', primary: '检查并重试', preserve: ['Prefix', '显示格式'] },
        permission: { label: '无权限', status: 'existing', summary: '隐藏值载荷，说明数据库 Keyspace Read/Write 权限来源。', primary: '查看所需权限', preserve: ['数据库', 'Keyspace'] },
        readonly: { label: '只读', status: 'existing', summary: '浏览、值检查和导出可用；TTL、CAS、批量删除禁用并说明原因。', primary: '导出当前已加载数据', blocked: ['预览 TTL 修改', '批量删除'] },
        longContent: { label: '长内容', status: 'extension', summary: '大值默认折叠并显示字节数，按 Text/JSON/Hex/Base64 选择有界预览。', primary: '切换有限预览', limits: ['最大预览字节以服务端能力为准', '不把截断值当完整值'] }
      }
    },
    {
      id: 'mq', title: 'SonnetMQ', objectName: 'DeviceEvents', scope: 'database', persistenceScope: 'instance', group: '消息 / MQ', status: 'extension',
      intro: '复用概览、消息、消费者组与配置；Topic 按数据库命名空间与权限进入统一资源树，物理 Store 共享实例 .system/mq。恢复与全局配置须核验实例影响；DLQ 页签按真实能力开放。', primary: '浏览消息',
      tabs: ['概览', '消息', '消费者组', '配置', '恢复边界', 'DLQ · 能力依赖', '审计'],
      columns: ['Offset', 'Timestamp', 'Key', 'Payload 摘要', 'Headers'], rows: [['10240', '2026-10-04 09:40:01', 'Pump-01', '{ "event": "temperature-alert", "value": 72.1 }', 'source=Line-A'], ['10241', '2026-10-04 09:40:05', 'Pump-02', '{ "event": "state-change", "state": "running" }', 'source=Line-A'], ['10242', '2026-10-04 09:40:09', 'Valve-01', '{ "event": "inspection-due" }', 'source=Line-B']],
      inspector: [{ label: '逻辑数据库', value: 'factory / DeviceEvents' }, { label: 'Topic identity', value: 'factory + DeviceEvents' }, { label: '逻辑 scope', value: 'database' }, { label: '物理 persistenceScope', value: 'instance' }, { label: '物理存储', value: '实例共享 .system/mq Store' }, { label: '选中 Offset', value: '10240 · 仅示例' }, { label: 'Payload', value: 'JSON / Text / Hex / Base64' }, { label: 'Consumer lag', value: '以真实运行时为准' }, { label: '权限', value: '数据库 Read/Write；全局 Store 管理另行核验' }, { label: '备份限制', value: '当前单库备份尚未覆盖实例共享 MQ Store' }],
      empty: { title: '当前 Topic 没有消息', body: '保留数据库与 Topic 身份，可调整 offset/time seek 或等待新消息；空 Topic 不等于 Store 故障。', action: '调整浏览范围' },
      capabilities: [
        { id: 'mq-browse', status: 'existing', label: '数据库 Topic 浏览与 seek', note: 'Read 权限按 database + Topic 校验；browse 不自动 ack。' },
        { id: 'mq-consumer-write', status: 'existing', label: '消费者组位点与发布预览', note: 'ack/reset/publish 需要数据库 Write 和真实能力。' },
        { id: 'mq-shared-store-boundary', status: 'extension', label: '共享 Store 恢复边界说明', note: '物理范围为实例 .system/mq，恢复可能影响多个数据库。' },
        { id: 'mq-dlq', status: 'planned', label: 'DLQ 浏览与重放', note: '仅在服务 capabilities 提供真实合同时开放。' }
      ],
      stateMatrix: {
        normal: { label: '正常', status: 'existing', summary: '显示当前数据库 Topic 的消息、offset、headers 和 consumer 摘要。', primary: '浏览消息', fields: ['数据库', 'Topic', 'Seek 模式', '最大条数'] },
        empty: { label: '空 Topic', status: 'existing', summary: '显示 0 条消息并保留 seek 输入；不把空结果解释为物理 Store 丢失。', primary: '调整浏览范围', preserve: ['数据库 + Topic identity', 'offset/time seek'] },
        error: { label: '运行时错误', status: 'extension', summary: '仅替换 Topic 结果区，保留逻辑身份并给出 runtime/端点错误原因。', primary: '检查并重试', preserve: ['数据库', 'Topic', 'seek'] },
        permission: { label: '无权限', status: 'existing', summary: '隐藏消息与 payload，说明数据库 Read 权限；实例 Store 管理权限单独核验。', primary: '查看数据库权限', preserve: ['数据库 + Topic identity'] },
        readonly: { label: '只读', status: 'existing', summary: 'browse、导出和审计查看可用；ack/reset/publish、配置与恢复写动作禁用。', primary: '导出当前消息', blocked: ['ack', 'reset', 'publish', '恢复共享 Store'] },
        longContent: { label: '长内容', status: 'extension', summary: 'payload 默认摘要，按有界字节切换 JSON/Text/Hex/Base64；浏览不自动加载无限历史。', primary: '查看有限 Payload', limits: ['最大条数与字节预算', 'offset 不是数据库备份位点'] }
      }
    },
    {
      id: 'vector', title: '向量索引', objectName: 'ManualEmbeddings.Embedding', scope: 'database', group: '搜索', status: 'extension',
      intro: '复用 raw / text embed、Top-K、过滤和索引参数；Profile 与质量证据独立展示。', primary: '执行向量检索',
      tabs: ['Search', '数据 / 导入', '索引参数', 'Profile', '命中详情', '质量证据'],
      columns: ['Rank', 'ID', 'Distance / Score', 'Source', '摘要'], rows: [['1', 'manual-pump-01#12', '0.084 · 示例', 'Manuals', '冷却泵轴承温度异常检查'], ['2', 'manual-pump-01#07', '0.112 · 示例', 'Manuals', '润滑与维护周期'], ['3', 'note-line-a#03', '0.156 · 示例', 'MaintenanceNotes', 'Line-A 温度排查记录']],
      inspector: [{ label: 'Query mode', value: 'Raw vector / 显式 text embedding' }, { label: '维度', value: '以真实 profile 为准' }, { label: 'Metric', value: '以索引定义为准' }, { label: 'Profile 身份', value: 'Provider / model / revision / normalization' }, { label: '检索预算', value: 'Top-K = 10 · 示例' }, { label: '质量证据', value: '示例排名不代表 Recall 通过' }],
      capabilities: [
        { id: 'vector-search', status: 'existing', label: 'Raw vector / 显式 text 向量检索', note: '维度、metric、Top-K 与 metadata filter 必须匹配真实索引和 profile。' },
        { id: 'vector-import', status: 'existing', label: '向量数据导入与校验', note: 'ID、维度、metadata 和错误位置先预览；长向量只做有限展示。' },
        { id: 'vector-index', status: 'existing', label: '索引参数与维护检视', note: '只暴露服务端支持的 kind、维度和 metric，不由 UI 增加引擎能力。' },
        { id: 'vector-profile', status: 'extension', label: '显式 Embedding Profile 身份', note: '展示 Provider/model/revision/normalization；缺 profile 时保持 raw 路径。' },
        { id: 'vector-hit-inspector', status: 'extension', label: '命中详情与源对象跳转', note: '区分 distance 与 score，源对象跳转保持数据库和原始名称上下文。' },
        { id: 'vector-quality-evidence', status: 'planned', label: 'Recall / 质量 / 成本证据', note: '需要真实模型、数据集、固定硬件和报告；静态排名与 hash fallback 不算证据。' }
      ],
      stateMatrix: {
        normal: { label: '正常', status: 'existing', summary: '显示索引、查询模式、维度/metric、Top-K 和命中详情。', primary: '执行向量检索', fields: ['数据库', '索引对象', 'Query mode', '维度 / Metric', 'Top-K', 'Metadata filter', '显式 Profile 身份'] },
        empty: { label: '空结果', status: 'existing', summary: '检索成功但没有命中，保留向量或文本输入、索引和过滤条件。', primary: '调整查询或过滤', preserve: ['数据库 / 索引 identity', 'Query mode', 'Top-K / filter'] },
        error: { label: '检索错误', status: 'extension', summary: '维度不匹配、profile 缺失或端点失败只替换命中区，保留输入以便检查。', primary: '检查并重试', preserve: ['Query input', '维度 / Metric', 'Profile identity'] },
        permission: { label: '无权限', status: 'existing', summary: '隐藏向量、metadata 和源文档载荷，说明数据库索引 Read/Manage 权限。', primary: '查看数据库权限', preserve: ['数据库', '索引对象'] },
        readonly: { label: '只读', status: 'existing', summary: '检索、Profile、命中查看和当前结果导出可用；导入、索引重建与参数修改禁用。', primary: '导出当前命中', blocked: ['导入向量', '重建索引', '修改索引参数'] },
        longContent: { label: '长向量/长 metadata', status: 'extension', summary: '向量按维度与字节预算折叠，命中 metadata 和源文档摘要有界呈现；不隐式切换模型。', primary: '查看有限命中详情', limits: ['维度/Top-K/字节预算以真实能力为准', '不使用 hash fallback 充当 text embedding', '不把静态排名当 Recall 证据'] }
      }
    },
    {
      id: 'fulltext', title: '全文索引', objectName: 'Manuals.SearchIndex', scope: 'database', group: '搜索', status: 'extension',
      intro: '复用全文查询、Analyzer、导入与索引；命中高亮、重建状态和统计共同解释结果。', primary: '执行全文检索',
      tabs: ['Search', 'Analyzer', '索引', '重建', '数据导入', '统计'],
      columns: ['ID', 'BM25', '字段', '命中摘要'], rows: [['manual-pump-01', '4.28 · 示例', 'title, body', '冷却泵…轴承温度…维护步骤'], ['note-line-a-1004', '3.61 · 示例', 'body', 'Line-A…温度告警…检查记录'], ['manual-pump-02', '2.84 · 示例', 'body', '温度传感器…校准流程']],
      inspector: [{ label: '查询', value: '冷却泵 温度' }, { label: '匹配', value: 'all / any / phrase / fuzzy' }, { label: 'Tokenizer', value: '以当前索引配置为准' }, { label: '高亮', value: '仅呈现真实命中片段' }, { label: '重建', value: '按服务器任务状态展示进度' }, { label: '数据', value: '静态示例，不是相关性评估' }],
      capabilities: [
        { id: 'fulltext-search', status: 'existing', label: '全文 term / phrase / fuzzy 检索', note: 'field、匹配模式、Top-K、BM25 与高亮来自真实索引响应。' },
        { id: 'fulltext-analyzer', status: 'existing', label: 'Analyzer token / position 检视', note: 'tokenizer 身份与分词结果需来自实际索引定义，原型示例不算实测。' },
        { id: 'fulltext-index', status: 'existing', label: '索引字段与配置检视', note: '删除、修改和兼容性先做差异预览，不把 UI 字段当引擎能力。' },
        { id: 'fulltext-rebuild', status: 'extension', label: '索引重建任务状态', note: '仅在真实任务 ID/终态存在时显示进度、取消或恢复。' },
        { id: 'fulltext-import', status: 'existing', label: '文档导入与重复策略', note: 'ID 路径、insert/replace 和错误定位明确展示，replace 不悄悄转 insert。' },
        { id: 'fulltext-stats', status: 'extension', label: '索引与词项统计', note: '只呈现服务端返回字段，缺失指标标记未提供。' }
      ],
      stateMatrix: {
        normal: { label: '正常', status: 'existing', summary: '显示当前索引的查询条件、匹配模式、BM25、字段和真实高亮片段。', primary: '执行全文检索', fields: ['数据库', '索引', 'Term / Phrase', '匹配模式', '字段', 'Top-K', 'Analyzer 身份', '重建任务 ID'] },
        empty: { label: '空结果', status: 'existing', summary: '检索成功但没有命中，保留关键词、字段和匹配模式，不把空结果解释为索引损坏。', primary: '调整检索条件', preserve: ['数据库 / 索引 identity', 'Term / Phrase', '字段 / 匹配模式'] },
        error: { label: '检索错误', status: 'extension', summary: '只替换命中区，保留 analyzer、字段和索引上下文并显示端点错误原因。', primary: '检查并重试', preserve: ['查询输入', '索引', '匹配模式'] },
        permission: { label: '无权限', status: 'existing', summary: '隐藏命中摘要、文档文本和词项载荷，说明数据库索引/集合 Read 权限。', primary: '查看数据库权限', preserve: ['数据库', '索引'] },
        readonly: { label: '只读', status: 'existing', summary: 'Search、Analyzer、索引查看、统计和当前命中导出可用；重建、导入、删除与配置修改禁用。', primary: '导出当前命中', blocked: ['重建索引', '导入文档', '删除索引', '修改 Analyzer'] },
        longContent: { label: '长文本/长命中', status: 'extension', summary: '长摘要与高亮片段折叠，按文档数/字节预算加载；只展示真实命中片段，不自动拉取全文。', primary: '查看有限命中', limits: ['Top-K 与摘要字节预算以服务端能力为准', '高亮不是相关性评估证据', '无任务 ID 不显示虚构进度或 resume'] }
      }
    },
    {
      id: 'bucket', title: '对象 Bucket', objectName: 'Evidence', scope: 'database', group: '对象', status: 'existing',
      intro: '复用对象浏览、Range 预览、治理、上传下载、Multipart、图片语义与审计；不扩大 S3 兼容声明。', primary: '上传对象',
      tabs: ['浏览', '预览', '上传 / 下载', 'Multipart', '版本与治理', '图片语义', '审计'],
      columns: ['Key', '大小', 'Content-Type', '版本', '更新于'], rows: [['2026-10-04/pump-01.jpg', '2.4 MB', 'image/jpeg', 'v-demo-12', '09:40 · 示例'], ['2026-10-04/line-a.csv', '84 KB', 'text/csv', 'v-demo-08', '09:30 · 示例'], ['manuals/pump-guide.pdf', '4.8 MB', 'application/pdf', 'v-demo-03', '09 月 28 日 · 示例']],
      inspector: [{ label: 'Key', value: '2026-10-04/pump-01.jpg' }, { label: 'Preview', value: 'Range 与大小预算明确' }, { label: 'Metadata', value: 'device=Pump-01, line=Line-A' }, { label: 'Checksum', value: '以服务端返回为准' }, { label: '治理', value: 'Retention / quota / legal hold / policy' }, { label: '版本', value: '版本删除与当前对象删除分开确认' }],
      empty: { title: '当前前缀没有对象', body: '保留 Bucket、prefix 和 delimiter，调整范围后重新读取；空列表不代表 Bucket 不存在。', action: '调整对象范围' },
      capabilities: [
        { id: 'object-list-preview', status: 'existing', label: '有界对象浏览与 Range 预览', note: '列表分页、Range 起点/长度和字节预算按真实 API。' },
        { id: 'object-transfer', status: 'existing', label: '上传 / 下载 / Multipart', note: '浏览器进度与服务器分片确认分开；complete/abort 需单独预览。' },
        { id: 'object-governance', status: 'existing', label: '版本与治理检查', note: 'retention、quota、legal hold 和 policy 由服务端返回。' },
        { id: 'object-semantic', status: 'extension', label: '图片语义查询', note: '仅使用显式真实 profile；合成模型/hash fallback 不算质量证据。' }
      ],
      stateMatrix: {
        normal: { label: '正常', status: 'existing', summary: '显示当前 prefix 下对象、大小、Content-Type、版本与时间。', primary: '浏览对象', fields: ['Bucket', 'Prefix', 'Delimiter', '列表上限'] },
        empty: { label: '空列表', status: 'existing', summary: '当前 prefix 没有对象，保留筛选与 Bucket 上下文。', primary: '调整对象范围', preserve: ['Bucket', 'Prefix', 'Delimiter'] },
        error: { label: '对象读取错误', status: 'extension', summary: '列表或 Range 失败时保留本地输入，不把浏览器缓存当服务端结果。', primary: '检查并重试', preserve: ['Key / Range', '传输草稿'] },
        permission: { label: '无权限', status: 'existing', summary: '隐藏对象 metadata/payload，提示 bucket 或数据库读取权限。', primary: '查看所需权限', preserve: ['Bucket', 'Prefix'] },
        readonly: { label: '只读', status: 'existing', summary: '浏览、Range、导出清单和审计查看可用；上传、complete、删除与治理修改禁用。', primary: '导出当前清单', blocked: ['上传对象', 'complete', '删除版本'] },
        longContent: { label: '长对象', status: 'existing', summary: '默认只取 Range 预览并显示大小/校验；不自动下载全量对象。', primary: '预览指定 Range', limits: ['Range 字节上限', 'Multipart 分片清单与服务器确认分开'] }
      }
    },
    {
      id: 'graph', title: 'Graph Beta', objectName: 'FactoryTopology', scope: 'database', group: '图 · Beta', status: 'extension',
      intro: '复用有界 Canvas、Schema/诊断、受限编辑、导入导出和维护审批；SQL/PGQ 转入共用查询器。', primary: '执行有界探索',
      tabs: ['Canvas', 'Schema / 索引', 'SQL / PGQ', '受限编辑', '导入 / 导出', '维护', '审计'],
      columns: ['Vertex / Edge', 'Label', 'Identity', 'Properties', '关系'], rows: [['Vertex', 'Device', 'Pump-01', '{ "line": "Line-A", "type": "Pump" }', 'INSTALLED_AT → Station-01'], ['Vertex', 'Station', 'Station-01', '{ "name": "冷却站" }', 'PART_OF → Line-A'], ['Edge', 'CONNECTED_TO', 'e-demo-17', '{ "flow": "coolant" }', 'Pump-01 → Valve-01']],
      inspector: [{ label: 'Graph 状态', value: 'Beta' }, { label: '有界画布', value: '最多 200 节点 / 400 边 · 设计预算' }, { label: '选中元素', value: 'Device / Pump-01' }, { label: '编辑路径', value: 'staged preview → 有效审批 → 执行' }, { label: '维护审批', value: '以服务器有效期与权限为准' }, { label: '路径证据', value: '示例关系不等同真实查询结果' }],
      empty: { title: '当前探索没有顶点或边', body: '保留图、起点、深度和节点/边预算，可调整探索起点后重新读取。', action: '调整探索起点' },
      capabilities: [
        { id: 'graph-canvas', status: 'existing', label: '有界 Canvas 探索', note: 'Graph Beta；节点/边上限和深度由真实能力与预算决定。' },
        { id: 'graph-schema-maintenance', status: 'existing', label: 'Schema、索引与维护预览', note: '诊断、repair/rebuild、checkpoint/compact 以服务器合同为准。' },
        { id: 'graph-edit-import', status: 'extension', label: '受限编辑与导入审批', note: 'Stage 仅生成草稿，批准前不改变图数据。' },
        { id: 'graph-pgq', status: 'planned', label: '完整 SQL / PGQ 覆盖', note: '仅在真实 PGQ 支持矩阵提供时开放，不从原型文本推断。' }
      ],
      stateMatrix: {
        normal: { label: '正常', status: 'existing', summary: '在节点/边预算内显示有界 Canvas、Schema 和选中元素。', primary: '执行有界探索', fields: ['图', '探索起点', '深度', '最大节点 / 边'] },
        empty: { label: '空图结果', status: 'existing', summary: '显示 0 节点 / 0 边并保留探索输入，不把无关系解释为故障。', primary: '调整探索起点', preserve: ['图上下文', '深度与预算'] },
        error: { label: '图查询错误', status: 'extension', summary: '只替换结果区域，说明 PGQ/诊断能力或服务错误来源。', primary: '检查并重试', preserve: ['SQL / PGQ 输入', '预算'] },
        permission: { label: '无权限', status: 'existing', summary: '隐藏顶点、边和属性，显示数据库 Graph Read/管理权限要求。', primary: '查看所需权限', preserve: ['数据库', '图名'] },
        readonly: { label: '只读', status: 'existing', summary: 'Canvas、Schema、SQL/PGQ 和导出可用；Stage、维护与导入批准禁用。', primary: '导出当前快照', blocked: ['Stage 编辑', 'repair/rebuild', '导入批准'] },
        longContent: { label: '长属性/大图', status: 'extension', summary: '属性折叠、关系分段加载并保持节点/边预算；不无限扩展画布。', primary: '收起属性并分段加载', limits: ['最多 200 节点 / 400 边（设计预算）', '完整 Payload 需 Inspector 有界查看'] }
      }
    }
  ]
};
