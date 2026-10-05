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
          empty: { title: '开始连接 SonnetDB', body: '添加一个连接后，可查看数据库、资源和最近工作区。', action: '添加连接' },
          capabilities: [
            { id: 'summary-database-overview', status: 'existing', label: '数据库与九模型摘要', note: '沿用当前 Dashboard/数据库列表返回的可访问资源摘要；原型数值为静态示例。' },
            { id: 'summary-health-snapshot', status: 'extension', label: '连接健康快照', note: '健康只来自明确服务响应并带检查时间；列表可见不等于服务全面健康。' },
            { id: 'summary-pending-items', status: 'extension', label: '待处理事项摘要', note: '汇总草稿、审批和持久任务入口并标记来源，不把本地草稿当服务器终态。' },
            { id: 'summary-workspace-entry', status: 'existing', label: '进入数据库工作台', note: '进入选中数据库的九模型逻辑上下文；MQ Topic identity 仍含 database + topic。' },
            { id: 'summary-mq-persistence-boundary', status: 'existing', label: 'MQ 持久化边界说明', note: 'MQ 逻辑 scope=database、persistenceScope=instance，物理 Store 为共享 .system/mq；当前单库备份不含该 Store。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'extension', summary: '显示连接、可访问数据库、九模型资源摘要与待处理入口；健康单独标注采样时间。', primary: '打开工作台', fields: ['连接', '数据库数', '最近访问', '待处理项', '活动工作区', '待审批', '需关注', '健康检查时间', '数据来源'] },
            empty: { label: '空实例摘要', status: 'extension', summary: '没有可访问数据库或待处理项时保留连接状态，说明添加连接或请求授权路径。', primary: '添加连接', preserve: ['连接', '服务地址', '权限入口'] },
            error: { label: '摘要读取错误', status: 'extension', summary: '仅替换摘要和数据库表，保留连接与最后快照并标记过期；不把旧列表当最新健康。', primary: '检查并重试', preserve: ['连接', '服务地址', '最后检查时间'], blocked: ['标记服务健康'] },
            permission: { label: '实例/数据库权限不足', status: 'existing', summary: '隐藏数据库、资源和待处理载荷，显示当前身份可请求的实例/数据库权限。', primary: '查看权限要求', preserve: ['连接', '服务地址'], blocked: ['打开受限数据库'] },
            readonly: { label: '只读', status: 'existing', summary: '可查看摘要并进入可读工作台；新建/删除数据库、批准事项和修改连接配置禁用。', primary: '打开工作台', blocked: ['新建数据库', '删除数据库', '批准待处理事项', '修改连接配置'] },
            longContent: { label: '长实例摘要', status: 'extension', summary: '数据库、资源和待处理列表按记录/字节预算分页或折叠，保留原始名称和未返回全部标记。', primary: '查看下一页', limits: ['分页与摘要预算以服务端能力为准', '不虚构全量数据库数量、健康或 continuation'] }
          }
        },
        {
          id: 'database-catalog', title: '数据库目录', tabLabel: '数据库', type: 'table', scope: 'instance', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '查看可访问数据库，进入九模型统一资源上下文。SonnetMQ Topic 使用数据库逻辑命名空间；物理 Store 共享实例 .system/mq。', primary: '新建数据库',
          columns: ['名称', '访问权限', '主要资源', 'Segment', '操作'], rows: [['factory', '管理', 'Telemetry · Assets · Manuals · DeviceEvents', '128', '打开'], ['analytics', '查询', 'DailySummary · FeatureVectors', '64', '打开'], ['sandbox', '查询', 'Examples · Sessions', '12', '打开']],
          fields: [{ label: '名称筛选', value: '', kind: 'input' }, { label: '权限', value: '全部', kind: 'select' }], tabs: ['可访问数据库', '最近访问'],
          inspector: [{ label: '选中数据库', value: 'factory' }, { label: '名称解析', value: '保留原始拼写' }, { label: '危险操作', value: '删除前预览资源与影响范围' }],
          empty: { title: '暂无可访问数据库', body: '可请求授权，管理员可新建数据库。', action: '查看当前权限' },
          capabilities: [
            { id: 'database-catalog-read', status: 'existing', label: '可访问数据库与九模型资源摘要', note: '目录结果按实例权限返回；资源树在选中数据库后保持统一的九模型上下文。原型数据为静态示例。' },
            { id: 'database-create-preview', status: 'extension', label: '新建数据库草稿与影响预览', note: '仅管理员可进入受控流程；SQL 名称保留创建拼写并按统一 binder 检查冲突。' },
            { id: 'database-delete-preview', status: 'extension', label: '删除前资源影响预览', note: '必须输入精确名称并显示九模型对象与不可逆影响；客户端禁用不代表已授权。' },
            { id: 'database-mq-context', status: 'existing', label: 'SonnetMQ 数据库逻辑上下文', note: 'Topic identity 始终包含 database + topic；物理持久化为实例共享 .system/mq，当前单库备份不含该 Store。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'existing', summary: '显示可访问数据库、权限和九模型资源摘要；选中后保留数据库上下文。', primary: '打开选中数据库', fields: ['名称', '权限', '资源', 'Segment', '选中数据库'] },
            empty: { label: '空目录', status: 'existing', summary: '没有可访问数据库时保留实例连接并说明请求授权或管理员新建路径。', primary: '查看当前权限', preserve: ['连接', '名称筛选', '权限'] },
            error: { label: '目录读取错误', status: 'extension', summary: '只替换数据库表，保留筛选与当前连接；不把旧快照当最新目录。', primary: '检查并重试', preserve: ['连接', '名称筛选', '权限'] },
            permission: { label: '无目录权限', status: 'existing', summary: '隐藏数据库与资源载荷，显示实例/数据库目录所需权限。', primary: '查看权限要求', preserve: ['连接', '名称筛选'] },
            readonly: { label: '只读', status: 'existing', summary: '可打开数据库和查看九模型资源；新建、删除与修改入口显示禁用原因。', primary: '打开选中数据库', blocked: ['新建数据库', '删除数据库', '修改目录'] },
            longContent: { label: '长目录', status: 'extension', summary: '数据库列表与资源摘要按页和字节预算呈现，保留名称原始拼写与未返回标记。', primary: '查看下一页', limits: ['分页/资源摘要上限以服务端能力为准', '不虚构全量数据库数量或 continuation'] }
          }
        },
        {
          id: 'connections', title: '连接管理', tabLabel: '连接', type: 'connection', scope: 'host', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '复用现有远程连接对话框，整理连接配置、凭据位置和测试结果。测试只读取服务健康与能力。', primary: '添加连接',
          columns: ['连接名称', '地址', '宿主', '凭据', '状态'], rows: [['Factory / Local', 'http://127.0.0.1:5080', 'Web / Studio', '当前会话', '已选择 · 示例'], ['Factory Readonly · 示例', 'https://db.example.internal', 'Web / VS Code', '宿主安全存储', '未测试'], ['Managed Local', 'http://127.0.0.1:5080', 'Studio', 'Windows 凭据管理器', '仅 Studio 可用']],
          fields: [{ label: '名称', value: 'Factory / Local', kind: 'input' }, { label: '服务地址', value: 'http://127.0.0.1:5080', kind: 'input' }, { label: '默认数据库', value: 'factory', kind: 'select' }, { label: '认证方式', value: 'Token', kind: 'select' }],
          tabs: ['连接库', '连接测试', '凭据说明'], inspector: [{ label: '测试状态', value: '原型未执行网络请求' }, { label: '切换连接', value: '先保留当前草稿与工作区' }, { label: '密钥', value: '不进入共享工作区文件' }],
          empty: { title: '还没有保存的连接', body: '输入服务地址并选择凭据来源，然后测试连接。', action: '添加连接' },
          capabilities: [
            { id: 'connection-profile', status: 'existing', label: '连接 profile 与宿主安全存储边界', note: '保存名称、地址、默认数据库和认证来源；Token 只进入当前会话或宿主安全存储，不进入共享工作区。' },
            { id: 'connection-health-check', status: 'extension', label: '服务健康检查', note: '单独报告 URL/传输可达性与服务健康；原型不发网络请求，不能返回测试成功。' },
            { id: 'connection-auth-check', status: 'extension', label: '认证检查', note: '认证失败与服务不可达分开呈现；凭据明文不进入结果或导出。' },
            { id: 'connection-object-permissions', status: 'planned', label: '数据库与对象权限检查', note: '需要真实 capabilities 与数据库授权合同；未知权限明确拒绝，不从 profile 推断对象可读。' },
            { id: 'connection-host-adapter', status: 'extension', label: 'Web / Studio / VS Code 宿主适配', note: '宿主能力和凭据来源分别显示，切换连接先保留当前工作区草稿。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'extension', summary: '显示已保存 profile 与最近测试时间；健康、认证、对象权限三个结果分开。', primary: '测试连接', fields: ['名称', 'URL', '默认数据库', '认证来源', '宿主', '测试时间'] },
            empty: { label: '空连接库', status: 'extension', summary: '没有保存连接时只显示添加 profile 的表单，保存不等于测试通过。', primary: '添加连接', preserve: ['当前宿主', '默认数据库草稿'] },
            error: { label: '连接测试错误', status: 'extension', summary: '保留 profile 草稿，分别说明 URL/健康、认证或能力检查失败；不清除其它工作区。', primary: '查看测试步骤', preserve: ['名称', '服务地址', '默认数据库', '认证方式'], blocked: ['标记为已验证'] },
            permission: { label: '对象权限未知/不足', status: 'planned', summary: '隐藏数据库与对象载荷，显示需要的身份、数据库和对象权限；不能把认证成功当作对象授权。', primary: '查看权限要求', preserve: ['连接 profile', '目标数据库'], blocked: ['打开对象数据'] },
            readonly: { label: '只读连接', status: 'extension', summary: '可查看 profile、测试结果和权限说明；保存 Token、修改 profile 与切换默认数据库按宿主策略禁用。', primary: '查看连接状态', blocked: ['保存凭据', '修改默认数据库'] },
            longContent: { label: '长能力清单', status: 'extension', summary: '服务 capabilities 与诊断详情按折叠和字节预算展示，保留未知项与最后检查时间。', primary: '查看能力摘要', limits: ['能力列表与诊断载荷按宿主/服务预算截断', '不把截断列表解释为完整支持矩阵'] }
          }
        },
        {
          id: 'recent', title: '最近工作区', tabLabel: '最近工作区', type: 'table', scope: 'host', status: 'planned',
          intro: '恢复对象、输入和过滤器。恢复工作区不会重放写操作或重新执行查询。', primary: '恢复选中工作区',
          columns: ['工作区', '连接 / 数据库', '类型', '保存时间', '草稿'], rows: [['产线温度分析', 'Factory / Local / factory', 'SQL', '今天 09:42', '已保存'], ['Assets', 'Factory / Local / factory', '关系表', '今天 09:30', '2 处未提交'], ['设备异常排查', 'Factory Readonly · 示例 / factory', 'Notebook · 规划', '昨天 16:20', '只读']],
          fields: [{ label: '搜索工作区', value: '', kind: 'input' }], tabs: ['最近', '已固定', '离线快照'],
          inspector: [{ label: '恢复内容', value: '页签 / 输入 / 过滤器 / 结果快照' }, { label: '连接检查', value: '恢复后重新核对权限' }, { label: 'M47 状态', value: 'Workspace Registry 规划' }],
          empty: { title: '还没有保存的工作区', body: '查询或对象浏览时可保存当前上下文。', action: '打开 SQL 工作区' },
          capabilities: [
            { id: 'recent-workspace-list', status: 'planned', label: '最近/固定工作区索引', note: 'Workspace Registry 尚未提供统一存储合同；列表与固定状态只在真实宿主能力返回后显示。' },
            { id: 'recent-context-restore', status: 'planned', label: '工作区上下文恢复', note: '只恢复页签、输入、过滤器和有界快照；恢复后重新检查身份与 capabilities，不自动执行查询或写操作。' },
            { id: 'recent-offline-snapshot', status: 'planned', label: '离线快照查看', note: '快照必须带采集时间、预算和过期标记；联网后仍只恢复上下文。' },
            { id: 'recent-connection-recheck', status: 'planned', label: '恢复后的连接与权限复核', note: '连接或数据库权限变化时明确阻断恢复，不从旧快照推断当前授权。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'planned', summary: '显示最近、固定和离线工作区及选中项的恢复内容；恢复只回填上下文。', primary: '恢复选中工作区', fields: ['宿主', '连接', '数据库', '对象', '工作区', '当前页签', '输入 / 过滤器', '保存时间', '快照时间', '草稿状态'] },
            empty: { label: '空工作区', status: 'planned', summary: '没有可恢复工作区时保留当前宿主和连接，说明保存入口；不创建后端资产。', primary: '打开 SQL 工作区', preserve: ['宿主', '连接', '搜索工作区'] },
            error: { label: '工作区读取错误', status: 'planned', summary: '只替换工作区列表，保留搜索和当前宿主；旧快照标记来源与过期状态。', primary: '检查并重试', preserve: ['宿主', '连接', '搜索工作区', '过滤页签'], blocked: ['标记恢复成功'] },
            permission: { label: '恢复权限不足', status: 'planned', summary: '隐藏受限输入、结果和对象载荷，显示宿主/连接/数据库授权要求。', primary: '查看权限要求', preserve: ['宿主', '连接', '工作区名称'], blocked: ['恢复受限载荷'] },
            readonly: { label: '只读', status: 'planned', summary: '可浏览工作区元数据并回填输入；执行查询、提交写入和修改保存项禁用。', primary: '查看工作区摘要', blocked: ['执行恢复后的查询', '重放写操作', '修改固定状态'] },
            longContent: { label: '长工作区内容', status: 'planned', summary: '工作区列表、输入和快照按记录/字节预算折叠并标记未返回全部，不加载无限历史。', primary: '查看恢复预算', limits: ['列表与快照预算待 Workspace Registry 合同确认', '不虚构 continuation、任务 ID 或后台进度'] }
          }
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
          intro: '把说明、SQL 单元和结果快照组织成可复核的排查文档。Notebook 为 M47 规划；当前原型只预览手动单元边界，不调用后端。', primary: '预览当前单元',
          columns: ['单元', '类型', '内容', '状态'], rows: [['01', '说明', '核对 Pump-01 的温度与维护记录', '已保存 · 示例'], ['02', 'SQL', 'SELECT … FROM Telemetry …', '未执行'], ['03', 'SQL', 'SELECT … FROM Assets …', '未执行'], ['04', '说明', '记录结论与证据边界', '草稿']],
          fields: [{ label: 'Notebook 名称', value: '设备异常排查', kind: 'input' }, { label: '数据库', value: 'factory', kind: 'select' }, { label: '当前单元', value: '02 · SQL', kind: 'select' }, { label: '执行状态', value: '规划 · 未执行', kind: 'select' }], tabs: ['单元', '大纲', '版本与导出'],
          inspector: [{ label: '当前单元', value: '02 · SQL' }, { label: '执行范围', value: '单元内 SQL，仅手动执行' }, { label: '可提交资产', value: '不含密钥与生产数据快照' }],
          empty: { title: '从一段说明开始', body: '添加说明或 SQL 单元，保存排查过程。', action: '添加 SQL 单元' },
          capabilities: [
            { id: 'notebook-draft', status: 'planned', label: '说明与 SQL 单元草稿', note: 'Notebook 入口和统一保存合同尚未提供；原型仅展示单元组织和草稿边界。' },
            { id: 'notebook-manual-cell', status: 'planned', label: '手动运行当前 SQL 单元', note: '未来仅允许用户显式运行当前单元；本原型拒绝调用后端、自动运行和后台重放。' },
            { id: 'notebook-result-snapshot', status: 'planned', label: '有界结果快照', note: '结果快照需用户显式选择并带数据库、预算和采样时间；不默认保存生产数据。' },
            { id: 'notebook-export', status: 'planned', label: '脱敏导出', note: '导出合同待确认，始终排除凭据与未选择的结果载荷。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'planned', summary: '展示说明与 SQL 单元顺序、当前单元和草稿状态；执行仍是规划能力。', primary: '预览当前单元', fields: ['Notebook 名称', '数据库', '当前单元', '单元类型', '输入', '执行状态', '结果快照', '保存版本'], blocked: ['自动执行', '全部运行', '提交写操作'] },
            empty: { label: '空 Notebook', status: 'planned', summary: '没有单元时只创建本地草稿，不建立后端 Notebook 或执行请求。', primary: '添加说明或 SQL 单元', preserve: ['Notebook 名称', '数据库'] },
            error: { label: '规划错误', status: 'planned', summary: '显示合同缺口或单元校验问题，保留文本和草稿；原型不伪造服务错误。', primary: '查看执行边界', preserve: ['当前单元输入', 'Notebook 草稿'], blocked: ['重试后端调用'] },
            permission: { label: '无数据库权限', status: 'planned', summary: '隐藏结果载荷并说明数据库 Read/Write 权限；规划页不会通过 UI 绕过授权。', primary: '查看数据库权限', preserve: ['数据库', 'Notebook 草稿'], blocked: ['运行 SQL 单元'] },
            readonly: { label: '只读', status: 'planned', summary: '可编辑说明、查看草稿和导出脱敏文档；单元执行、写入和自动重放保持禁用。', primary: '导出草稿', blocked: ['运行当前单元', '全部运行', '写入数据库'] },
            longContent: { label: '长 Notebook', status: 'planned', summary: '单元文本和快照按单元/字节预算折叠，显示未返回全部；不自动拉取结果。', primary: '查看快照预算', limits: ['单元与快照预算待真实合同确认', '不虚构 continuation、任务 ID 或后台进度'] }
          }
        },
        {
          id: 'history', title: '执行历史', tabLabel: '执行历史', type: 'table', scope: 'host', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '复用现有 History Drawer，按连接、数据库和模型找回查询上下文。', primary: '恢复输入',
          columns: ['时间', '数据库 / 对象', '动作', '状态', '耗时', '结果'], rows: [['09:42:16', 'factory / Telemetry', 'SELECT', '成功 · 示例', '24 ms', '4 行'], ['09:37:08', 'factory / Assets', '写入预览', '待确认 · 示例', '—', '2 行草稿'], ['09:31:54', 'factory / Manuals', '全文查询', '截断 · 示例', '18 ms', '前 25 条']],
          fields: [{ label: '连接', value: 'Factory / Local', kind: 'select' }, { label: '数据库', value: 'factory', kind: 'select' }, { label: '模型', value: '全部', kind: 'select' }, { label: '动作', value: '全部', kind: 'select' }, { label: '状态', value: '全部', kind: 'select' }, { label: '时间范围', value: '今天', kind: 'select' }], tabs: ['查询', '模型操作', '审批记录'],
          inspector: [{ label: '恢复方式', value: '仅恢复输入，不自动执行' }, { label: '历史来源', value: '本地工作台历史' }, { label: '审计区别', value: '服务端审计请到治理' }],
          empty: { title: '还没有执行历史', body: '手动执行查询后，这里保存输入、状态与耗时。', action: '打开 SQL 工作区' },
          capabilities: [
            { id: 'history-local', status: 'existing', label: '本地工作台执行历史', note: '保存连接、数据库、输入、状态、耗时和返回/影响数量；当前记录为静态示例。' },
            { id: 'history-input-restore', status: 'existing', label: '恢复输入与过滤器', note: '恢复只填回 SQL/模型输入和上下文，绝不自动执行查询或写操作。' },
            { id: 'history-model-filter', status: 'extension', label: '按九模型与数据库筛选', note: 'MQ 按 database + topic 逻辑身份过滤；实例共享 .system/mq 物理边界单独说明。' },
            { id: 'history-server-audit', status: 'extension', label: '服务端审计跳转', note: '本地 History 与服务端 Audit 分开标记；只有真实审计 API 返回的记录才能显示为服务器审计。' },
            { id: 'history-write-replay', status: 'planned', label: '写操作恢复/重放', note: '当前明确拒绝自动重放；恢复写草稿必须重新预览、核对权限和获取新的审批。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'existing', summary: '按连接、数据库、模型、动作和时间显示本地历史，并在 Inspector 标记来源。', primary: '恢复输入', fields: ['时间', '对象', '动作', '状态', '耗时', '返回/影响数量', '连接', '数据库'] },
            empty: { label: '空历史', status: 'existing', summary: '没有本地记录时保留筛选条件并引导打开工作区；不把服务端审计当作本地历史。', primary: '打开 SQL 工作区', preserve: ['连接', '动作', '状态', '时间范围'] },
            error: { label: '历史读取错误', status: 'extension', summary: '只替换历史列表，保留筛选和当前工作区；恢复动作等待列表有效。', primary: '检查并重试', preserve: ['筛选条件', '当前页签'], blocked: ['恢复输入'] },
            permission: { label: '审计权限不足', status: 'extension', summary: '隐藏 SQL、Payload 和对象细节，仅显示可见元数据与所需审计权限。', primary: '查看权限要求', preserve: ['连接', '数据库', '时间范围'], blocked: ['查看结果载荷'] },
            readonly: { label: '只读', status: 'existing', summary: '可筛选、查看和恢复输入；恢复后仍需用户手动执行，写审批与重放禁用。', primary: '恢复输入', blocked: ['自动执行', '重放写操作', '修改历史'] },
            longContent: { label: '长历史', status: 'extension', summary: '历史按记录数/字节预算分页，长 SQL 与 Payload 折叠并标记未返回全部。', primary: '查看截断说明', limits: ['记录数、SQL 字节和 Payload 预算以真实 API 为准', '不虚构全量历史或可恢复任务 ID'] }
          }
        }
      ]
    },
    {
      id: 'observe', label: '观测', icon: 'activity', pages: [
        {
          id: 'metrics', title: '性能指标', tabLabel: '性能指标', type: 'overview', scope: 'instance', status: 'existing', existingRoute: '/admin/app/monitoring',
          intro: '复用 Prometheus 指标与采样窗口；未启用指标时展示配置原因。原型数值为示例。', primary: '刷新指标',
          columns: ['数据库', 'MemTable', '数据点', 'Segment', '待 Flush'], rows: [['factory', '24.8 MB', '82,430', '128', '1'], ['analytics', '8.2 MB', '24,100', '64', '0'], ['sandbox', '1.1 MB', '2,050', '12', '0']],
          fields: [{ label: '写入速率', value: '2.4k 点/秒 · 示例' }, { label: '查询 P95', value: '24 ms · 示例' }, { label: 'WAL fsync P95', value: '3.2 ms · 示例' }, { label: '窗口', value: '最近 10 分钟' }, { label: '采样来源', value: '/metrics' }, { label: '采样时间', value: '原型未采样' }, { label: '指标能力', value: '最小指标集可单独展示' }],
          tabs: ['吞吐与延迟', '内存与存储', 'AI 用量'], inspector: [{ label: '采样来源', value: '/metrics' }, { label: '采样时间', value: '原型未采样' }, { label: '能力降级', value: '最小指标集可单独展示' }],
          empty: { title: '等待指标采样', body: '若完整指标未启用，仍可查看最小指标集及启用说明。', action: '查看采样说明' },
          capabilities: [
            { id: 'metrics-sample', status: 'existing', label: 'Prometheus 指标采样', note: '沿用 /metrics 与时间窗口；页面示例数值不等于已采样证据。' },
            { id: 'metrics-minimal', status: 'extension', label: '最小指标降级', note: '完整指标未启用时显示实际提供的最小集合与原因，不用 0 值填充。' },
            { id: 'metrics-offline-snapshot', status: 'extension', label: '离线最后样本', note: '离线保留最后样本并明确采样时间和过期状态；不标记为当前健康。' },
            { id: 'metrics-ai-usage', status: 'extension', label: 'AI 用量与成本来源说明', note: '真实 Provider usage 与估算值分开；示例请求/Token 数不构成质量或成本报告。' },
            { id: 'metrics-export', status: 'existing', label: '有界指标导出', note: '导出遵循时间窗口、行/字节预算和权限；不能扩展为全量服务器诊断。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'existing', summary: '显示最近有效采样的吞吐、延迟、内存与采样时间；示例数值明确标记。', primary: '刷新指标', fields: ['窗口', '写入速率', 'query P95', 'WAL fsync P95', '内存', '采样来源', '采样时间', '指标能力'] },
            empty: { label: '无采样', status: 'existing', summary: '窗口内没有指标时显示未采样/未启用原因，不生成 0 值图。', primary: '查看采样说明', preserve: ['窗口', '指标能力', '最后样本时间'] },
            error: { label: '采样错误', status: 'extension', summary: '保留最后有效样本并标记过期，仅替换当前刷新结果和错误原因。', primary: '检查并重试', preserve: ['窗口', '最后样本', '采样时间'], blocked: ['标记为当前健康'] },
            permission: { label: '指标权限不足', status: 'extension', summary: '隐藏受限指标载荷，显示 /metrics 或实例观测权限要求；不以客户端禁用推断能力。', primary: '查看权限要求', preserve: ['实例范围', '窗口'], blocked: ['导出受限指标'] },
            readonly: { label: '只读', status: 'existing', summary: '可查看、筛选与导出已授权样本；采样配置和告警写入保持禁用。', primary: '导出当前样本', blocked: ['修改采样配置', '创建告警规则'] },
            longContent: { label: '长时间窗', status: 'extension', summary: '时间序列按点数/字节预算降采样或折叠，保留窗口、步长与未返回全部标记。', primary: '查看采样预算', limits: ['点数与字节预算由指标端点返回', '不虚构全量历史、精确 P95 或质量/成本证据'] }
          }
        },
        {
          id: 'events', title: '实时事件', tabLabel: '实时事件', type: 'table', scope: 'instance', status: 'existing', existingRoute: '/admin/app/events',
          intro: '查看数据库与运行时事件；暂停只暂停当前视图接收。', primary: '暂停视图',
          columns: ['时间', '级别', '来源', '数据库', '事件'], rows: [['09:42:18', '信息', 'SQL', 'factory', '查询结束 · 示例'], ['09:40:04', '信息', 'Catalog', 'sandbox', '数据库已加载 · 示例'], ['09:35:50', '警告', 'SSE', '实例', '事件连接重连 · 示例']],
          fields: [{ label: '级别', value: '全部', kind: 'select' }, { label: '来源', value: '全部', kind: 'select' }], tabs: ['事件流', '数据库事件', '连接状态'],
          inspector: [{ label: '传输', value: 'SSE' }, { label: '连接中断', value: '保留已接收事件并显示时间' }, { label: '事件留存', value: '本地显示不等同持久审计' }],
          empty: { title: '尚未收到事件', body: '连接建立后等待下一条运行时或数据库事件。', action: '检查事件连接' },
          capabilities: [
            { id: 'events-sse-stream', status: 'existing', label: 'SSE 事件流', note: '接收数据库与运行时事件；事件时间、接收时间和来源分开呈现，原型不订阅真实流。' },
            { id: 'events-view-pause', status: 'existing', label: '暂停当前视图', note: '暂停只停止当前页面接收/渲染；不会暂停服务器生产，也不改变其它订阅者。' },
            { id: 'events-filter', status: 'existing', label: '级别/来源/数据库筛选', note: '筛选仅作用于当前视图，保留实例事件与数据库事件的范围标记。' },
            { id: 'events-reconnect-state', status: 'extension', label: 'SSE 断线与重连状态', note: '显示最后帧时间、断线原因和重连状态；断线不自动声称事件已持久保存。' },
            { id: 'events-local-buffer', status: 'extension', label: '本地已接收列表保留', note: '断线后保留本地已接收事件并标记时间；本地列表不等同服务端审计。' },
            { id: 'events-server-audit', status: 'planned', label: '服务端事件审计', note: '持久审计入口和保留策略尚未统一；未知能力不得用本地列表补齐。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'existing', summary: '显示按级别、来源和数据库筛选的事件流，并在 Inspector 区分事件时间、接收时间与 SSE 状态。', primary: '暂停视图', fields: ['级别', '来源', '数据库/实例', '事件时间', '接收时间', '正文/Payload', 'SSE 状态'] },
            empty: { label: '空事件流', status: 'existing', summary: '连接可用但当前窗口没有事件，保留筛选并说明等待范围；空结果不表示服务器没有事件。', primary: '检查事件连接', preserve: ['级别筛选', '来源筛选', '数据库筛选', '连接状态'] },
            error: { label: 'SSE/事件读取错误', status: 'extension', summary: '只替换当前事件结果区，保留已接收列表和筛选；显示断线原因与最后帧时间。', primary: '检查并重试', preserve: ['级别筛选', '来源筛选', '数据库筛选', '本地已接收事件'], blocked: ['标记为服务器已暂停'] },
            permission: { label: '事件权限不足', status: 'existing', summary: '隐藏受限事件正文和 Payload，显示实例/数据库事件 Read 权限要求；不能从连接成功推断事件授权。', primary: '查看权限要求', preserve: ['级别筛选', '来源筛选', '数据库筛选'], blocked: ['查看受限 Payload'] },
            readonly: { label: '只读', status: 'existing', summary: '可筛选、暂停本地视图、查看已授权事件并导出当前样本；服务器订阅和清理动作保持禁用。', primary: '导出当前事件', blocked: ['暂停服务器事件流', '清理服务器事件'] },
            longContent: { label: '长事件流', status: 'extension', summary: '事件正文按条数和字节预算折叠或截断，显示未返回全部；本地缓冲仍标注接收时间。', primary: '查看事件载荷预算', limits: ['事件条数/字节预算由 SSE 与服务端能力返回', '截断事件不代表完整 Payload', '本地已接收列表不等同持久事件审计'] }
          }
        },
        {
          id: 'slow-queries', title: '慢查询', tabLabel: '慢查询', type: 'table', scope: 'instance', status: 'extension', existingRoute: '/admin/app/events',
          intro: '将已有事件页与 SlowQueryDrawer 的慢查询入口整理为同一任务。', primary: '打开查询输入',
          columns: ['时间', '数据库', '耗时', '行数', '状态', 'SQL 摘要'], rows: [['09:22:16', 'factory', '824 ms', '10,000', '成功 · 示例', 'SELECT … FROM Telemetry'], ['09:15:08', 'analytics', '612 ms', '0', '失败 · 示例', 'SELECT … JOIN …']],
          fields: [{ label: '数据库', value: '全部', kind: 'select' }, { label: '最短耗时', value: '500 ms', kind: 'input' }, { label: '时间范围', value: '最近 1 小时', kind: 'select' }], tabs: ['慢查询列表', '选中 SQL', '解释入口'],
          inspector: [{ label: '阈值', value: '以服务端配置为准' }, { label: 'SQL 内容', value: '按权限展示并支持脱敏' }, { label: 'EXPLAIN', value: '手动打开，避免自动重跑' }],
          empty: { title: '窗口内没有慢查询', body: '可扩大时间窗口，或查看当前慢查询阈值。', action: '查看阈值说明' },
          capabilities: [
            { id: 'slow-query-events', status: 'existing', label: '慢查询事件读取', note: '耗时、状态、行数和数据库来自已接收事件；原型样例不是服务端慢查询报告。' },
            { id: 'slow-query-filters', status: 'extension', label: '耗时/数据库/时间筛选', note: '复用 Events 与 SlowQueryDrawer 的筛选输入，阈值以服务端配置为准。' },
            { id: 'slow-query-input-restore', status: 'existing', label: '恢复 SQL 输入', note: '只恢复选中慢查询文本、数据库和接收时间到编辑器，不自动重跑原请求。' },
            { id: 'slow-query-explain', status: 'extension', label: '手动 Explain 入口', note: 'Explain 需用户显式打开并执行，计划状态在真实 planner 返回前保持未执行。' },
            { id: 'slow-query-payload-permission', status: 'existing', label: 'SQL/Payload 权限呈现', note: '按数据库/对象权限显示脱敏 SQL 或隐藏正文；客户端禁用不等于授权。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'extension', summary: '显示按数据库、最短耗时和时间范围筛选的慢查询事件，以及状态、行数、耗时和 SQL 摘要。', primary: '打开查询输入', fields: ['数据库', '最短耗时', '时间范围', '事件时间', '耗时', '行数', '状态', 'SQL 摘要', '阈值来源'] },
            empty: { label: '窗口无慢查询', status: 'existing', summary: '没有符合窗口和阈值的已接收事件时保留筛选，不把空结果解释为查询健康或阈值为零。', primary: '查看阈值说明', preserve: ['数据库筛选', '最短耗时', '时间范围'] },
            error: { label: '慢查询读取错误', status: 'extension', summary: '只替换慢查询列表，保留筛选和已选 SQL 草稿；不自动重跑失败的慢请求。', primary: '检查并重试', preserve: ['数据库筛选', '最短耗时', '时间范围', '选中 SQL 输入'], blocked: ['自动重跑慢查询'] },
            permission: { label: 'SQL/Payload 无权限', status: 'existing', summary: '隐藏 SQL 正文、参数和结果载荷，显示数据库/对象 Read 权限要求；仍可保留无敏感摘要与耗时（若服务允许）。', primary: '查看权限要求', preserve: ['数据库筛选', '最短耗时', '时间范围'], blocked: ['查看受限 SQL', '打开受限 Payload'] },
            readonly: { label: '只读', status: 'extension', summary: '可筛选、恢复 SQL 输入和手动打开 Explain；不自动执行、不修改慢查询阈值或删除事件。', primary: '恢复查询输入', blocked: ['自动重跑', '修改服务端阈值', '删除慢查询事件'] },
            longContent: { label: '长慢查询', status: 'extension', summary: 'SQL、参数和事件列表按行/字节预算折叠，显示未返回全部；Explain 仍须手动执行。', primary: '查看结果预算', limits: ['慢查询条数、SQL 字节和参数预算以真实服务为准', '截断 SQL 不可直接当作可执行完整输入', '不显示虚构的 Explain 计划或 continuation'] }
          }
        },
        {
          id: 'alerts', title: '告警规则', tabLabel: '告警规则', type: 'form', scope: 'instance', status: 'planned',
          intro: '规划阈值、窗口与通知路由。当前管理路由未提供统一告警配置。', primary: '预览规则',
          columns: ['规则', '指标', '窗口', '阈值', '启用'], rows: [['查询延迟', 'query P95', '5 分钟', '> 500 ms', '规划草稿'], ['磁盘容量', 'free bytes', '10 分钟', '< 10 GB', '规划草稿']],
          fields: [{ label: '规则名称', value: '查询延迟', kind: 'input' }, { label: '指标', value: 'query P95', kind: 'select' }, { label: '评估窗口', value: '5 分钟', kind: 'select' }, { label: '阈值', value: '500 ms', kind: 'input' }, { label: '通知路由', value: '尚未配置', kind: 'select' }], tabs: ['规则', '评估记录', '通知路由'],
          inspector: [{ label: '后端状态', value: '统一告警接口待规划' }, { label: '通知动作', value: '本原型不会发送消息' }, { label: '权限', value: '实例管理权限' }],
          empty: { title: '尚未配置告警', body: '先确认指标来源和通知路由，再创建规则草稿。', action: '创建规则草稿' },
          capabilities: [
            { id: 'alert-rule-draft', status: 'planned', label: '规则草稿与条件预览', note: '指标、窗口、阈值、去抖和数据库/实例范围只保存为页面草稿；当前无统一告警后端。' },
            { id: 'alert-evaluation', status: 'planned', label: '评估记录', note: '真实评估、触发和恢复记录尚未提供；样例不能显示为已触发告警。' },
            { id: 'alert-notification-routing', status: 'planned', label: '通知路由说明', note: '邮件、Webhook 或消息通道尚未配置；规则预览绝不会发送通知。' },
            { id: 'alert-admin-permission', status: 'planned', label: '实例管理权限检查', note: '需要服务端实例管理、指标读取与通知配置权限合同；未知权限明确保持不可用。' }
          ],
          stateMatrix: {
            normal: { label: '规划正常', status: 'planned', summary: '显示规则草稿、指标来源、评估窗口、阈值、去抖和通知路由说明；不会启动评估或发送通知。', primary: '预览规则', fields: ['规则名称', '指标', '评估窗口', '阈值', '去抖窗口', '通知路由', '作用范围', '草稿状态'] },
            empty: { label: '空规则', status: 'planned', summary: '没有规则时只创建页面内存草稿，先确认指标来源和通知路由；不建立服务端规则。', primary: '创建规则草稿', preserve: ['规则名称', '指标', '评估窗口', '阈值', '通知路由'] },
            error: { label: '规划合同错误', status: 'planned', summary: '显示缺失的指标、窗口或通知合同，保留规则草稿；原型不伪造评估失败或重试后端调用。', primary: '查看规划边界', preserve: ['规则名称', '指标', '评估窗口', '阈值', '通知路由'], blocked: ['启用规则', '发送通知'] },
            permission: { label: '实例管理权限不足', status: 'planned', summary: '隐藏指标载荷、评估记录和通知配置，显示实例管理与指标权限要求；不从页面表单推断授权。', primary: '查看权限要求', preserve: ['规则草稿', '作用范围'], blocked: ['保存规则', '启用规则', '配置通知'] },
            readonly: { label: '只读', status: 'planned', summary: '可查看规则草稿和条件预览；规则保存、启用、修改通知路由与发送动作全部禁用。', primary: '查看规则草稿', blocked: ['保存规则', '启用规则', '发送通知', '修改通知路由'] },
            longContent: { label: '长评估记录', status: 'planned', summary: '规则、评估记录和通知说明按记录数/字节预算折叠，标记未返回全部；不显示虚构的触发数量。', primary: '查看评估预算', limits: ['规则与评估历史预算待真实告警服务合同确认', '通知载荷按通道字节预算显示', '规划草稿/样例不代表已发送消息或真实告警'] }
          }
        },
        {
          id: 'runtime', title: '运行时诊断', tabLabel: '运行时', type: 'table', scope: 'instance', status: 'planned',
          intro: '规划引擎任务、请求预算和诊断采集的统一入口；不把缺失的运行时 DTO 表示为已有。', primary: '预览诊断采集',
          columns: ['组件', '范围', '可见信息', '设计状态'], rows: [['WAL / Flush', '数据库', '积压、最近错误、时间戳', '能力适配待确认'], ['SonnetMQ', 'factory · Store 为实例共享', 'topic / consumer runtime', '复用已有 MQ 运行时'], ['Graph Beta', '数据库', '遍历诊断、预算、审计', '复用已有 Graph 能力']],
          fields: [{ label: '组件', value: '全部', kind: 'select' }, { label: '诊断预算', value: '30 秒 / 5 MB', kind: 'input' }], tabs: ['运行时', '诊断采集', '预算与取消'],
          inspector: [{ label: '采集边界', value: '有界、可取消、按权限脱敏' }, { label: '实现状态', value: '聚合视图规划' }, { label: '证据', value: '原型不生成运行诊断报告' }],
          empty: { title: '当前能力未提供诊断信息', body: '可查看已暴露指标，或转入对应模型运行时。', action: '查看能力矩阵' },
          capabilities: [
            { id: 'runtime-component-index', status: 'planned', label: '组件运行时索引', note: 'WAL/Flush、SonnetMQ、Graph Beta 等组件统一索引待 DTO 与权限合同；缺少能力显示未提供。' },
            { id: 'runtime-diagnostic-collection', status: 'planned', label: '有界诊断采集', note: '采集必须受时间/字节预算、脱敏和用户取消约束；当前原型不执行采集。' },
            { id: 'runtime-cancel', status: 'planned', label: '采集取消与服务器终态', note: '区分请求已取消、服务器仍运行和服务器终态；界面结束不能解释为健康或完成。' },
            { id: 'runtime-native-handoff', status: 'extension', label: '转入模型原生运行时', note: '已有 MQ/Graph 诊断可跳转其原生工作台；跳转不复制或扩大能力合同。' },
            { id: 'runtime-redaction', status: 'planned', label: '诊断载荷脱敏', note: '敏感字段在采集前按真实权限和脱敏策略处理；未知策略保持不可用。' }
          ],
          stateMatrix: {
            normal: { label: '规划正常', status: 'planned', summary: '显示组件索引、范围和诊断预算；可预览未来采集边界，但不把静态组件行当作健康报告。', primary: '预览诊断采集', fields: ['组件', '范围', '可见信息', '时间预算', '字节预算', '脱敏策略', '取消能力', '证据状态'] },
            empty: { label: '无诊断能力', status: 'planned', summary: '当前组件没有暴露诊断能力时显示未提供，并可查看能力矩阵或转入已暴露的原生页；不填充 0 值。', primary: '查看能力矩阵', preserve: ['组件筛选', '诊断预算'] },
            error: { label: '采集错误', status: 'planned', summary: '只替换诊断采集结果，保留组件、预算和取消状态；客户端失败不解释为组件健康。', primary: '检查并重试', preserve: ['组件筛选', '诊断预算', '脱敏策略'], blocked: ['标记组件健康'] },
            permission: { label: '诊断权限不足', status: 'planned', summary: '隐藏诊断载荷和敏感字段，显示实例/组件权限要求；未知能力与无权限分开呈现。', primary: '查看权限要求', preserve: ['组件筛选', '诊断预算'], blocked: ['查看受限诊断'] },
            readonly: { label: '只读', status: 'planned', summary: '可查看已授权摘要、预算说明并转入原生诊断页；采集、取消服务器任务和配置修改保持禁用。', primary: '查看诊断摘要', blocked: ['开始诊断采集', '修改预算', '标记组件健康'] },
            longContent: { label: '长诊断载荷', status: 'planned', summary: '日志、任务和组件诊断按时间/字节预算折叠，显示未返回全部并保留取消状态。', primary: '查看采集预算', limits: ['采集时间与字节预算由真实组件能力返回', '长日志默认脱敏和摘要，不自动拉取全量', '没有服务器终态不能显示完成、健康或可恢复任务'] }
          }
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
          empty: { title: '尚无 Modbus 源或端点', body: '由管理员配置运行时，之后可查看绑定与待审批写入。', action: '查看配置说明' },
          capabilities: [
            { id: 'modbus-runtime', status: 'existing', label: 'Modbus Runtime 状态', note: '复用现有运行时返回源、端点和连接状态；原型静态配置不代表现场已连接。' },
            { id: 'modbus-bindings', status: 'existing', label: '源/端点/数据库表绑定', note: '显示地址、寄存器范围和数据库/表绑定；绑定权限按真实数据库和实例合同核验。' },
            { id: 'modbus-pending-write', status: 'existing', label: 'Pending 待审批写入', note: '现场写入保留请求 ID、寄存器范围和影响预览，批准/拒绝沿用现有流程。' },
            { id: 'modbus-audit', status: 'existing', label: 'Audit 审计', note: '区分预览、批准、拒绝和执行终态；审计记录来源与请求关联标识必须来自真实返回。' },
            { id: 'modbus-write-approval', status: 'existing', label: '批准写入边界', note: '非管理员或未知权限不能绕过确认直接写现场端点；页面刷新不代表写入成功。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'existing', summary: '显示数据库选择、Runtime 状态、源/端点/表绑定，以及 Pending 与 Audit 页签中的请求和终态。', primary: '刷新状态', fields: ['数据库', '源/端点', '模式', '地址', '寄存器范围', '数据库/表绑定', 'Runtime 状态', '请求 ID', '审批状态'] },
            empty: { label: '空 Modbus 配置', status: 'existing', summary: '没有源或端点时保留数据库选择，并说明管理员配置与权限路径；空列表不表示服务异常。', primary: '查看配置说明', preserve: ['数据库选择', 'Runtime 筛选'] },
            error: { label: 'Runtime/读取错误', status: 'extension', summary: '只替换 Runtime、Pending 或 Audit 当前结果区，保留数据库、源/端点和请求草稿；不把刷新失败当作现场断电。', primary: '检查并重试', preserve: ['数据库选择', '源/端点筛选', '寄存器范围', '选中请求'], blocked: ['确认现场写入'] },
            permission: { label: 'Modbus 权限不足', status: 'existing', summary: '隐藏端点、寄存器和写请求载荷，显示实例/数据库与批准权限要求；不能从可见菜单推断管理员资格。', primary: '查看权限要求', preserve: ['数据库选择', '源/端点筛选'], blocked: ['查看寄存器载荷', '批准现场写入', '拒绝现场写入'] },
            readonly: { label: '只读', status: 'existing', summary: '可查看 Runtime、Pending 摘要和 Audit；现场写入、批准/拒绝、配置修改保持禁用并说明原因。', primary: '查看运行时', blocked: ['批准现场写入', '拒绝现场写入', '修改端点配置'] },
            longContent: { label: '长寄存器/审计内容', status: 'extension', summary: '端点、寄存器范围和 Audit 记录按页数/字节预算展示，保留请求 ID 与未返回全部标记。', primary: '查看传输限制', limits: ['端点与寄存器读取须有界并遵循服务端上限', 'Audit 按真实 continuation/分页能力显示', '浏览器进度或页面刷新不能代替现场写入服务器终态'] }
          }
        },
        {
          id: 'imports', title: '导入任务', tabLabel: '导入任务', type: 'table', scope: 'database', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '统一呈现已有模型导入入口；持久 resume 仅在真实后端能力存在时开放。', primary: '选择导入目标',
          columns: ['任务', '模型 / 目标', '格式', '进度', '状态'], rows: [['Telemetry_1004.csv', '时序 / Telemetry', 'CSV', '预览 100 行', '待确认 · 示例'], ['Assets.csv', '关系 / Assets', 'CSV', '映射 8 列', '草稿 · 示例'], ['Manuals.ndjson', '文档 / Manuals', 'NDJSON', '校验 20 文档', '草稿 · 示例']],
          fields: [{ label: '模型', value: '全部', kind: 'select' }, { label: '任务状态', value: '全部', kind: 'select' }], tabs: ['任务列表', '字段映射', '校验与错误'],
          inspector: [{ label: '文件来源', value: '本机选择，尚未上传' }, { label: '恢复能力', value: '按模型能力显示' }, { label: '权限', value: '目标数据库写入 / 导入权限' }],
          empty: { title: '还没有导入任务', body: '先选择模型与目标对象，检查字段映射及重复记录策略。', action: '选择导入目标' },
          capabilities: [
            { id: 'import-target-selection', status: 'extension', label: '模型与目标对象选择', note: '统一入口只索引已有模型导入器；数据库、模型和目标对象身份必须明确。' },
            { id: 'import-field-mapping', status: 'extension', label: '字段映射预览', note: '映射保留目标 schema 的原始名称与类型，确认前只在本地草稿中展示。' },
            { id: 'import-validation', status: 'extension', label: '行/字段校验与错误定位', note: '时间单位、重复策略和失败行为以真实端点能力为准，错误不得静默跳过。' },
            { id: 'import-staged-preview', status: 'extension', label: '导入影响预览', note: '提交前显示行数/字节、覆盖或追加范围与权限；原型不上传、不执行。' },
            { id: 'import-persistent-resume', status: 'planned', label: '持久导入恢复', note: '只有真实任务身份与 resume 合同存在时才开放；否则明确要求重新选择文件。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'extension', summary: '显示模型、目标对象、文件、映射、校验与导入任务状态；动作停在预览或真实合同入口。', primary: '选择导入目标', fields: ['数据库', '模型', '目标对象', '源文件', '格式', '行数 / 字节', '字段映射', '重复策略', '失败策略', '错误位置', '任务状态'] },
            empty: { label: '空导入列表', status: 'extension', summary: '没有导入任务时保留数据库和筛选条件，先选择目标并建立本地映射草稿。', primary: '选择导入目标', preserve: ['数据库', '模型筛选', '任务状态筛选'] },
            error: { label: '导入校验错误', status: 'extension', summary: '只替换校验与错误区域，保留文件引用、映射和重复策略；不把局部校验通过当作可提交。', primary: '返回修正映射', preserve: ['数据库', '目标对象', '源文件', '字段映射', '失败策略'], blocked: ['提交导入'] },
            permission: { label: '导入权限不足', status: 'existing', summary: '隐藏文件内容、字段值与目标载荷，显示数据库/模型导入或写入权限要求。', primary: '查看权限要求', preserve: ['数据库', '模型', '目标对象'], blocked: ['上传或提交导入'] },
            readonly: { label: '只读', status: 'existing', summary: '可查看任务、映射与错误摘要；上传、覆盖、追加和确认导入禁用。', primary: '查看导入摘要', blocked: ['上传文件', '确认导入', '覆盖目标', '恢复导入任务'] },
            longContent: { label: '长导入内容', status: 'extension', summary: '文件、映射和错误按行/字节预算分页或折叠，明确未返回全部；不伪造全量计数或 resume。', primary: '查看校验预算', limits: ['文件预览、错误行和映射字节上限以服务端能力为准', '无持久任务合同则只能重新选择文件'] }
          }
        },
        {
          id: 'transfers', title: '对象传输', tabLabel: '对象传输', type: 'table', scope: 'database', status: 'extension', existingRoute: '/admin/app/sql',
          intro: '整理现有对象上传下载和 Multipart；明确字节、分片、校验与取消状态。', primary: '新建上传',
          columns: ['对象', '方向', '大小', '分片', '状态'], rows: [['evidence/2026-10-04/pump-01.jpg', '上传', '2.4 MB', '单文件', '待确认 · 示例'], ['archive/line-a-1004.zip', '上传', '128 MB', '3 / 8', '可恢复会话 · 示例'], ['manuals/pump-guide.pdf', '下载', '4.8 MB', 'Range', '未开始 · 示例']],
          fields: [{ label: 'Bucket', value: 'Evidence', kind: 'select' }, { label: '方向', value: '全部', kind: 'select' }], tabs: ['传输', 'Multipart 会话', '校验与错误'],
          inspector: [{ label: '目标 Bucket', value: 'Evidence' }, { label: '校验', value: '以服务器 checksum 为准' }, { label: '大文件', value: '按能力显示 Range 与分片' }],
          empty: { title: '还没有对象传输', body: '选择 Bucket 与文件，检查大小、版本和校验方式。', action: '新建上传' },
          capabilities: [
            { id: 'transfer-session-list', status: 'extension', label: '对象传输与 Multipart 会话', note: '复用对象工作台按 Bucket/Key/版本查看真实会话；原型记录为静态示例。' },
            { id: 'transfer-upload-preview', status: 'extension', label: '上传与 complete 预览', note: '上传、complete、abort 分步呈现，浏览器字节进度不等于服务器提交终态。' },
            { id: 'transfer-download-range', status: 'extension', label: '下载与 Range 预览', note: '按 Range 和字节预算预览对象，不自动下载全量对象。' },
            { id: 'transfer-checksum', status: 'extension', label: '服务器校验与错误', note: 'checksum、分片校验和版本以服务器响应为准；客户端计算不能冒充服务器确认。' },
            { id: 'transfer-cancel', status: 'planned', label: '传输取消与终态', note: '只有真实取消接口和服务器终态可用时才显示完成/取消；本原型不发请求。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'extension', summary: '显示 Bucket、Key、方向、版本、大小、分片、校验和取消状态；每个动作保留服务器边界。', primary: '新建上传', fields: ['数据库', 'Bucket', 'Key', '方向', '版本', '字节', '已完成分片', '有效期', 'checksum', '取消状态'] },
            empty: { label: '空传输列表', status: 'extension', summary: '没有传输或会话时保留 Bucket、方向和对象筛选，说明新建上传/下载入口。', primary: '新建上传', preserve: ['数据库', 'Bucket', '方向', 'Key / Prefix'] },
            error: { label: '传输或校验错误', status: 'extension', summary: '只替换分片/校验结果，保留 Key、版本和本地草稿；不把客户端进度当服务器终态。', primary: '检查并重试', preserve: ['Bucket', 'Key', '版本', '方向', '已完成分片'], blocked: ['标记 complete 成功'] },
            permission: { label: '对象权限不足', status: 'existing', summary: '隐藏对象 metadata、Range 内容与上传载荷，显示 Bucket/数据库读写权限要求。', primary: '查看权限要求', preserve: ['数据库', 'Bucket', 'Key'], blocked: ['读取对象内容', '上传或删除对象'] },
            readonly: { label: '只读', status: 'existing', summary: '可浏览会话、Range 预览、checksum 和审计；上传、complete、abort、删除与版本治理禁用。', primary: '预览指定 Range', blocked: ['上传对象', 'complete', 'abort', '删除对象版本'] },
            longContent: { label: '长对象/长会话', status: 'extension', summary: '对象清单、分片和 Range 按条数/字节预算分页或折叠，不自动下载全量内容。', primary: '查看传输预算', limits: ['Range 与分片清单上限以服务端能力为准', 'Multipart 完成需服务器确认，不能由浏览器进度推断'] }
          }
        },
        {
          id: 'jobs', title: '任务与位点', tabLabel: '任务与位点', type: 'table', scope: 'instance', status: 'planned',
          intro: '规划任务、位点、重试和派生发布的统一索引；实际 resume 继续由各模型合同决定。', primary: '查看选中任务',
          columns: ['任务', '范围', '阶段', '位点 / 版本', '可用动作'], rows: [['RAG Manuals', 'factory / copilot-docs', '待续跑 · 示例', 'generation demo-07', '转至 RAG'], ['对象语义处理', 'factory / Evidence', '待处理 · 示例', 'object version demo-12', '转至对象'], ['MQ 消费', 'factory / DeviceEvents · 实例共享 Store', '浏览示例', 'offset 10240', '转至 SonnetMQ']],
          fields: [{ label: '范围', value: '实例与数据库', kind: 'select' }, { label: '阶段', value: '全部', kind: 'select' }], tabs: ['任务索引', '位点', '重试与恢复'],
          inspector: [{ label: '统一 DTO', value: '规划，尚无通用任务接口' }, { label: '恢复边界', value: '不可把客户端重试当持久 resume' }, { label: '取消', value: '区分取消请求与服务器终态' }],
          empty: { title: '当前没有可展示的任务', body: '按模型能力查看任务或恢复入口。', action: '查看任务能力' },
          capabilities: [
            { id: 'jobs-model-index', status: 'planned', label: '跨模型任务索引', note: '统一 DTO 尚未提供；列表必须标明任务来源模型与数据库，不替代模型原生任务。' },
            { id: 'jobs-rag-persistent', status: 'existing', label: 'RAG 持久任务入口', note: '复用 generation、expected revision 和 profile 合同；续跑前需匹配真实服务器任务。' },
            { id: 'jobs-object-version', status: 'extension', label: '对象版本与传输任务', note: '对象 version、Multipart 会话和服务器终态保持独立语义。' },
            { id: 'jobs-mq-offset', status: 'extension', label: 'SonnetMQ Topic 位点', note: 'offset 归属 database + Topic；物理持久化 scope=instance、共享 .system/mq，恢复影响其它数据库需单独核验。' },
            { id: 'jobs-resume-boundary', status: 'planned', label: '任务恢复边界', note: '没有持久任务身份、版本和权限合同时不提供通用 resume 或立即重试；客户端取消与服务器终态分开。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'planned', summary: '按模型显示任务、位点/版本、发布点和恢复原因，并转入对应原生工作流。', primary: '查看选中任务', fields: ['范围', '数据库', '模型', '任务 ID', 'generation', 'revision', 'offset', '对象版本', '发布点', '状态', '可恢复原因'] },
            empty: { label: '空任务索引', status: 'planned', summary: '没有统一任务记录时保留实例/数据库筛选，提示从模型原生页查看任务能力。', primary: '查看任务能力', preserve: ['范围筛选', '数据库筛选', '阶段筛选'] },
            error: { label: '任务读取错误', status: 'planned', summary: '只替换索引结果，保留筛选和选中模型；不把客户端重试标为持久恢复。', primary: '检查并重试', preserve: ['范围', '数据库', '模型', '任务筛选'], blocked: ['立即重试任务'] },
            permission: { label: '任务权限不足', status: 'planned', summary: '隐藏任务载荷、位点和恢复细节，说明实例/数据库/模型任务权限要求。', primary: '查看权限要求', preserve: ['范围', '数据库', '模型'], blocked: ['读取位点', '恢复或重试任务'] },
            readonly: { label: '只读', status: 'planned', summary: '可查看任务摘要、位点和版本说明并转到原生查看页；恢复、重试、发布和删除禁用。', primary: '查看选中任务', blocked: ['恢复任务', '立即重试', '发布新版本', '删除任务'] },
            longContent: { label: '长任务索引', status: 'planned', summary: '任务、日志和位点按记录/字节预算分页，区分未返回全部与未知状态；不拼接异构 continuation。', primary: '查看任务预算', limits: ['统一任务 DTO、分页和 continuation 待合同确认', 'generation、revision、offset、object version 不混为同一恢复数字', 'MQ 位点恢复需说明实例共享 Store 影响'] }
          }
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
          empty: { title: '连接一个 AI 客户端', body: '先选择客户端与数据库，检查工具范围和配置落点。', action: '开始接入向导' },
          capabilities: [
            { id: 'ai-connect-streamable-http', status: 'existing', label: 'typed Streamable HTTP MCP', note: '服务端 typed HTTP 入口已经存在；原型不发起请求，也不把静态 Endpoint 当作连通证据。' },
            { id: 'ai-connect-config-wizard', status: 'planned', label: '客户端配置向导', note: 'WorkBuddy/Claude/Cursor/Codex 的用户/项目配置生成仍待 M47-U08；预览不能写入用户或项目配置。' },
            { id: 'ai-connect-stdio-bridge', status: 'planned', label: 'sonnetdb mcp stdio bridge', note: 'stdio bridge 与配置自检尚未实现，不能显示安装、启动或 tools/list 成功。' },
            { id: 'ai-connect-tools-list', status: 'planned', label: '实际 tools/list 与只读清单', note: '工具清单必须来自真实身份和服务响应；默认只读，写入/删除不由 MCP 直接提供。' },
            { id: 'ai-connect-egress-policy', status: 'planned', label: '显式数据外发预览', note: '出域范围由用户授权、工具边界和宿主策略共同决定；凭据永不作为工具结果外发。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'planned', summary: '展示客户端、typed HTTP 传输、数据库、配置范围、结果预算与自检待执行状态；配置与 tools/list 仍是预览。', primary: '生成配置预览', fields: ['客户端', '传输方式', '数据库', '配置范围', 'HTTP Endpoint', '结果预算', '工具清单状态', '外发策略'] },
            empty: { label: '未接入', status: 'planned', summary: '没有已保存接入 profile 时只保留向导输入；不创建配置、不调用 HTTP/stdio。', primary: '开始接入向导', preserve: ['客户端', '数据库', '配置范围'] },
            error: { label: '接入自检错误', status: 'planned', summary: '保留传输、Endpoint、预算和配置草稿，仅标记实际失败步骤；不能把局部连接成功写成 tools/list 或质量成功。', primary: '查看自检步骤', preserve: ['客户端', '传输方式', 'Endpoint', '配置范围'], blocked: ['标记接入已验证', '写入客户端配置'] },
            permission: { label: '接入权限不足', status: 'planned', summary: '隐藏工具清单与数据库载荷，显示需要的实例/数据库身份和宿主配置权限。', primary: '查看权限要求', preserve: ['客户端', '数据库', '配置范围'], blocked: ['查看受限工具', '导出受限数据'] },
            readonly: { label: '只读', status: 'planned', summary: '可查看传输说明、预算和外发规则；配置写入、stdio 安装、工具调用与任何写入入口禁用。', primary: '查看接入边界', blocked: ['保存客户端配置', '安装 stdio bridge', '调用写工具', '批准数据外发'] },
            longContent: { label: '长工具与诊断内容', status: 'planned', summary: 'tools/list、schema、诊断和外发规则按条数/字节预算折叠并标记未返回全部；不虚构 continuation 或自检终态。', primary: '查看接入预算', limits: ['工具清单与诊断上限由服务和宿主合同提供', '截断清单不代表完整能力矩阵', '凭据与敏感字段始终脱敏'] }
          }
        },
        {
          id: 'copilot-settings', title: 'Copilot 与 Provider', tabLabel: 'Provider', type: 'form', scope: 'instance', status: 'existing', existingRoute: '/admin/app/ai-settings',
          intro: '复用已有账号绑定、Provider、模型目录、测试和用量；真实模型质量与成本单独验收。', primary: '预览配置变更',
          columns: ['用途', 'Provider / 模型', '配置来源', '状态'], rows: [['Chat', '平台模型 / 由服务端返回', 'sonnetdb.com 账号', '未读取 · 原型'], ['Embedding', '显式 profile', '服务端配置', '未读取 · 原型']],
          fields: [{ label: 'Provider', value: '以当前服务器配置为准', kind: 'select' }, { label: 'Chat 模型', value: '尚未加载模型目录', kind: 'select' }, { label: '外发范围', value: '仅显式允许内容', kind: 'select' }, { label: '凭据', value: '••••••••', kind: 'secret' }],
          tabs: ['Provider', '账号绑定', '模型目录', '用量', '测试'], inspector: [{ label: '管理权限', value: '实例管理员' }, { label: '测试边界', value: '连接成功不等同质量门禁' }, { label: '估算用量', value: '缺 usage 时单独标注估算' }],
          empty: { title: 'Provider 尚未配置', body: '管理员可绑定账号或配置受支持 Provider。', action: '查看配置步骤' },
          capabilities: [
            { id: 'copilot-account-binding', status: 'existing', label: '账号绑定与 Provider profile', note: '复用既有 sonnetdb.com 设备码/授权边界；原型不发起授权、轮询或保存凭据。' },
            { id: 'copilot-model-catalog', status: 'existing', label: '真实模型目录', note: '模型 ID、Provider 与 embedding profile 只能来自服务端实际目录，不预设不存在的模型。' },
            { id: 'copilot-usage-evidence', status: 'extension', label: 'usage 与成本口径', note: '实际 usage、估算 usage、请求数和成本分开记录；缺 usage 时不把估算当真实证据。' },
            { id: 'copilot-provider-test', status: 'existing', label: '连接与配置测试', note: '连接/认证测试与真实模型质量、成本门禁分开；连接成功不能升级为质量 PASS。' },
            { id: 'copilot-quality-cost-gate', status: 'planned', label: '真实模型质量与成本门禁', note: '需要独立真实模型、质量、成本与固定环境证据；原型静态目录不构成通过。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'existing', summary: '展示服务器返回的 Provider/profile、模型目录、Token 到期、usage 口径与外发范围；测试结果与质量/成本证据分开。', primary: '预览配置变更', fields: ['Provider', 'Chat 模型', 'Embedding profile', '账号状态', 'Token 到期', 'usage 时间窗', '实际/估算标记', '外发范围', '质量/成本证据'] },
            empty: { label: '未配置 Provider', status: 'existing', summary: '没有已配置 Provider 时保留实例身份与配置步骤，不预填模型 ID、不生成账号绑定结果。', primary: '查看配置步骤', preserve: ['实例连接', 'Provider 选择', '外发范围'] },
            error: { label: 'Provider/目录读取错误', status: 'extension', summary: '仅替换 Provider、模型或 usage 面板，保留已加载配置并标记过期；不把连接测试或缓存目录当质量证据。', primary: '检查读取步骤', preserve: ['Provider', '外发范围', '账号草稿'], blocked: ['标记模型质量通过', '标记成本通过'] },
            permission: { label: '实例管理权限不足', status: 'existing', summary: '隐藏模型目录、usage 与凭据状态，显示实例管理员或 Provider 读取权限要求。', primary: '查看权限要求', preserve: ['实例连接', '目标 Provider'], blocked: ['查看敏感配置', '修改 Provider'] },
            readonly: { label: '只读', status: 'existing', summary: '可浏览 profile、模型目录、usage 口径与测试边界；账号绑定、Provider 修改和外发范围保存禁用。', primary: '查看 Provider 状态', blocked: ['绑定账号', '保存 Provider', '修改外发范围', '执行质量/成本门禁'] },
            longContent: { label: '长模型与 usage 内容', status: 'extension', summary: '模型目录、版本、usage 明细和测试日志按记录/字节预算分页或折叠，保留实际/估算标记。', primary: '查看 usage 预算', limits: ['目录与 usage 上限以服务端返回为准', '不虚构模型 ID、价格、质量分数或成本报告', '脱敏后才可导出诊断'] }
          }
        },
        {
          id: 'rag', title: 'RAG 管理', tabLabel: 'RAG', type: 'table', scope: 'database', status: 'existing', existingRoute: '/admin/app/rag',
          intro: '复用已发布快照、持久任务续跑、派生重建与清理；不将已有能力重新列为空壳。', primary: '预览重建 / 换代',
          columns: ['Stream', 'Active revision', 'Profile', '内容 / 分块', '待续跑任务'], rows: [['copilot-docs', '7 · 示例', 'factory-text-v2 · 示例', '128 / 864 · 示例', '待处理 · 示例'], ['maintenance-notes', '3 · 示例', 'factory-text-v2 · 示例', '42 / 210 · 示例', '无 · 示例']],
          fields: [{ label: '数据库', value: 'factory', kind: 'select' }, { label: 'Stream', value: 'copilot-docs', kind: 'input' }, { label: '目标 profile', value: '选择服务器已配置 profile', kind: 'select' }], tabs: ['已发布快照', '持久任务', '派生重建', '退役清理', '审计'],
          inspector: [{ label: '模型身份', value: 'Provider / model / revision / dimensions' }, { label: '发布边界', value: '新版本完整发布前保留当前版本' }, { label: '写入', value: '服务器校验数据库管理权限' }],
          empty: { title: '尚无已发布快照', body: '先通过实际摄取入口创建首个版本，再在这里重建与维护。', action: '查看摄取说明' },
          capabilities: [
            { id: 'rag-published-snapshot', status: 'existing', label: '已发布快照与 active revision', note: '复用数据库 RAG 已发布快照；新版本完整发布前继续保留当前 active revision。' },
            { id: 'rag-persistent-task', status: 'existing', label: '持久任务与受控续跑', note: '续跑必须匹配真实任务、generation、expected revision 与 profile identity；没有持久任务 ID 不显示 resume。' },
            { id: 'rag-profile-identity', status: 'existing', label: 'Profile / Provider / dimensions 身份', note: '目标 profile 必须来自服务端已配置项；profile 不匹配时拒绝续跑或发布。' },
            { id: 'rag-derived-rebuild', status: 'existing', label: '派生重建与换代预览', note: '重建、换代和发布经过数据库权限与写审批；仅展示影响预览，不自动执行。' },
            { id: 'rag-retirement-cleanup', status: 'existing', label: '退役与清理预览', note: '使用中的版本延期处理，原始主数据保留；版本计数和范围来自服务器。' },
            { id: 'rag-audit', status: 'extension', label: 'RAG 审计与 continuation', note: '审计按真实 continuation 分页；原型不伪造任务状态、进度或服务器终态。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'existing', summary: '展示数据库 Stream、active revision、profile、generation/expected revision、持久任务和受控重建入口。', primary: '预览重建 / 换代', fields: ['数据库', 'Stream', 'Active revision', 'Profile identity', 'Generation', 'Expected revision', '内容 / 分块', '任务 ID', '发布状态', '清理范围'] },
            empty: { label: '空 RAG 目录', status: 'existing', summary: '没有已发布快照或任务时保留数据库/Stream/profile 输入，说明实际摄取入口；不创建派生资源。', primary: '查看摄取说明', preserve: ['数据库', 'Stream', '目标 profile'] },
            error: { label: 'RAG 任务/快照错误', status: 'extension', summary: '仅替换快照或任务结果，保留 Stream、profile、generation 和 expected revision；不把客户端重试当持久续跑。', primary: '检查并重试', preserve: ['数据库', 'Stream', 'Profile identity', 'Generation', 'Expected revision'], blocked: ['自动续跑', '发布新 revision'] },
            permission: { label: '数据库 RAG 权限不足', status: 'existing', summary: '隐藏内容、分块和任务载荷，显示数据库对象/任务管理权限要求。', primary: '查看数据库权限', preserve: ['数据库', 'Stream', 'Profile identity'], blocked: ['读取内容', '恢复任务', '清理版本'] },
            readonly: { label: '只读', status: 'existing', summary: '可查看快照、profile、任务和审计；重建、发布、清理、续跑与删除全部禁用并说明原因。', primary: '查看 RAG 状态', blocked: ['重建派生版本', '续跑任务', '发布 revision', '清理退役版本'] },
            longContent: { label: '长文档与任务日志', status: 'extension', summary: '文档、分块、任务日志和审计按记录/字节预算分页，保留 profile、generation 和 revision 关系；不拼接异构 continuation。', primary: '查看任务预算', limits: ['内容与日志上限由服务端合同提供', '安全整数溢出或范围异常时拒绝写入', '截断内容不视为完整文档或任务终态'] }
          }
        },
        {
          id: 'tool-permissions', title: '工具权限与外发', tabLabel: '工具权限', type: 'table', scope: 'database', status: 'planned',
          intro: '规划可见的 MCP 工具清单、预算和外发范围；默认只读，写入走工作台审批。', primary: '预览权限范围',
          columns: ['工具类别', '数据范围', '权限', '预算', '外发'], rows: [['Schema / describe', 'factory schema', '只读', '有界 metadata', '按宿主配置'], ['SQL select / explain', 'factory', '只读', '100 行 / 1 MB / 30 秒', '按显式允许范围'], ['模型 browse / search', '已授权对象', '只读', '按模型预算', '按宿主配置'], ['写入 / 删除', '工作台审批', 'MCP 不提供', '—', '—']],
          fields: [{ label: '客户端', value: '全部', kind: 'select' }, { label: '工具类别', value: '全部', kind: 'select' }], tabs: ['工具权限', '结果预算', '数据外发', '调用记录'],
          inspector: [{ label: '有效权限', value: '用户权限 ∩ 工具权限 ∩ 宿主策略' }, { label: '权限状态', value: '设计示例，不是实际授权' }, { label: '服务端校验', value: '任何客户端开关不能扩大权限' }],
          empty: { title: '尚未加载工具清单', body: 'AI Connect 自检成功后，按当前身份显示实际 tools/list。', action: '打开 AI Connect' },
          capabilities: [
            { id: 'tool-permissions-intersection', status: 'planned', label: '有效权限交集', note: '有效权限是用户授权 × 工具边界 × 宿主策略；界面开关不能扩大服务器授权。' },
            { id: 'tool-permissions-readonly-default', status: 'planned', label: '默认只读工具边界', note: 'Schema/describe、SQL select/explain 与模型 browse/search 默认只读；MCP 不提供直接写入/删除入口。' },
            { id: 'tool-permissions-budget', status: 'extension', label: '结果预算与截断', note: 'maxRows、字节、超时和取消由服务端能力约束；客户端偏好不能扩大上限。' },
            { id: 'tool-permissions-egress', status: 'planned', label: '显式数据外发策略', note: '只允许用户明确选择且通过宿主策略的 schema/metadata/结果范围；凭据和未授权敏感字段永不外发。' },
            { id: 'tool-permissions-call-audit', status: 'planned', label: '调用记录与留存', note: '调用来源、工具版本、范围和留存权限需真实记录合同；原型不显示工具执行成功。' }
          ],
          stateMatrix: {
            normal: { label: '正常', status: 'planned', summary: '展示实际工具清单（若服务返回）、用户/工具/宿主有效权限交集、预算、数据范围与调用记录来源。', primary: '预览权限范围', fields: ['客户端', '用户身份', '数据库', '工具类别', '有效权限交集', 'maxRows', '字节预算', '超时/取消', '数据外发范围', '调用记录来源'] },
            empty: { label: '未加载工具清单', status: 'planned', summary: '没有真实 tools/list 或权限响应时保留客户端/数据库筛选，不能把静态类别当实际授权。', primary: '打开 AI Connect', preserve: ['客户端', '数据库', '工具类别'] },
            error: { label: '权限/工具读取错误', status: 'planned', summary: '仅替换工具与预算结果，保留身份、数据库和筛选；不把缓存或客户端开关标成有效权限。', primary: '检查并重试', preserve: ['客户端', '用户身份', '数据库', '工具类别'], blocked: ['标记工具可用', '扩大结果预算'] },
            permission: { label: '权限不足', status: 'planned', summary: '隐藏工具 schema、数据范围和调用载荷，显示缺少的实例/数据库/宿主权限。', primary: '查看权限要求', preserve: ['客户端', '用户身份', '数据库'], blocked: ['查看受限工具', '外发受限数据'] },
            readonly: { label: '只读', status: 'planned', summary: '可查看工具边界、预算和外发说明；修改策略、批准外发、写入/删除与直接 MCP 写工具全部禁用。', primary: '查看只读边界', blocked: ['保存权限策略', '批准数据外发', '执行写入/删除', '修改服务器预算'] },
            longContent: { label: '长工具与结果 schema', status: 'planned', summary: '工具 schema、字段说明、调用记录和结果预览按条数/字节预算折叠，标记未返回全部；不伪造完整 tools/list。', primary: '查看工具预算', limits: ['schema/调用记录上限以真实服务和宿主合同为准', '截断结果不代表完整数据', '凭据永不进入工具结果或导出'] }
          }
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
