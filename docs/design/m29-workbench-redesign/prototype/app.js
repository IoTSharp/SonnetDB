const modelMeta = {
  sql: { group: 'Measurements', icon: '⌁', label: '时序 / SQL', tabs: ['查询', '结果', '图表', '轨迹'] },
  table: { group: 'Tables', icon: '▦', label: '关系表', tabs: ['数据', '设计器', '索引', '导入 / 导出', 'ER', 'DDL'] },
  document: { group: 'Collections', icon: '◫', label: '文档集合', tabs: ['Documents', '查询', 'Validator', '索引', '导入 / 导出'] },
  kv: { group: 'KV Keyspaces', icon: '⌘', label: 'KV Keyspace', tabs: ['浏览器', '批量操作', '统计'] },
  mq: { group: 'MQ Topics', icon: '♧', label: 'SonnetMQ Topic', tabs: ['概览', '消息', '消费者组', '配置'] },
  vector: { group: 'Vector Indexes', icon: '⌬', label: '向量索引', tabs: ['检索 Playground', '索引详情'] },
  fulltext: { group: 'FullText Indexes', icon: 'FT', label: '全文索引', tabs: ['检索', 'Analyzer', '索引详情'] },
  bucket: { group: 'Object Buckets', icon: '▱', label: '对象桶', tabs: ['对象', '治理', 'Multipart', '审计'] }
};

const state = { model: 'sql', object: 'device_metrics', subtab: '查询' };
const workbench = document.querySelector('#workbench');
const breadcrumb = document.querySelector('#breadcrumb');
const activeObjectTab = document.querySelector('#activeObjectTab');

const rows = {
  machines: [
    ['M-101', '冲压机 A1', 'LINE-1', '运行中', '78.4', '2026-07-11 14:31:48'],
    ['M-102', '焊接机器人 B2', 'LINE-1', '维护', '12.0', '2026-07-11 13:58:17'],
    ['M-103', '视觉检测 C1', 'LINE-2', '运行中', '65.7', '2026-07-11 14:31:44'],
    ['M-104', '包装机 D4', 'LINE-2', '告警', '91.2', '2026-07-11 14:30:05'],
    ['M-105', '输送带 E3', 'LINE-3', '离线', '0.0', '2026-07-11 12:46:20']
  ],
  mq: [
    ['125,678', '2026-07-11 14:31:48.123', 'deviceId=PLC-101, type=telemetry', '{ "temperature": 56.7, "pressure": 1.21 }'],
    ['125,677', '2026-07-11 14:31:47.987', 'deviceId=PLC-102, type=telemetry', '{ "temperature": 48.1, "pressure": 1.08 }'],
    ['125,676', '2026-07-11 14:31:47.876', 'deviceId=SENSOR-77, type=telemetry', '{ "vibration": 0.032, "status": "OK" }'],
    ['125,675', '2026-07-11 14:31:47.765', 'deviceId=ROBOT-9, type=event', '{ "cycle": 8812, "durationMs": 246 }'],
    ['125,674', '2026-07-11 14:31:47.654', 'deviceId=PLC-101, type=telemetry', '{ "temperature": 56.4, "pressure": 1.20 }']
  ]
};

function escapeHtml(value) {
  return String(value).replace(/[&<>'"]/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[char]);
}

function header(metrics, primaryLabel = '刷新', stageAction = false) {
  const meta = modelMeta[state.model];
  return `
    <div class="wb-titlebar">
      <span class="object-icon">${meta.icon}</span>
      <div class="title-copy"><h1>${escapeHtml(state.object)}</h1><p>${meta.label} · factory-cluster / factory</p></div>
      <div class="metrics">${metrics.map(m => `<div class="metric"><span>${m[0]}</span><strong class="${m[2] || ''}">${m[1]}</strong></div>`).join('')}</div>
      <div class="wb-actions">
        <button class="secondary" type="button" data-action="refresh">↻ ${primaryLabel}</button>
        ${stageAction ? '<button class="primary stage-action" type="button">暂存变更</button>' : ''}
        <button class="quiet" type="button" data-action="more">•••</button>
      </div>
    </div>
    <nav class="subtabs">${meta.tabs.map(tab => `<button type="button" class="subtab ${tab === state.subtab ? 'active' : ''}" data-subtab="${tab}">${tab}</button>`).join('')}</nav>`;
}

function toolbar(content) { return `<div class="toolbar">${content}</div>`; }
function table(headers, body, widths = []) {
  return `<table><thead><tr>${headers.map((h, i) => `<th${widths[i] ? ` style="width:${widths[i]}"` : ''}>${h}</th>`).join('')}</tr></thead><tbody>${body.map((r, ri) => `<tr class="${ri === 0 ? 'selected' : ''}">${r.map(c => `<td>${c}</td>`).join('')}</tr>`).join('')}</tbody></table>`;
}

function inspector(title, body) {
  return `<aside class="inspector"><div class="inspector-head"><strong>${title}</strong><button class="quiet" type="button">×</button></div>${body}</aside>`;
}

function sqlView() {
  if (state.subtab === '查询') {
    return `${header([['Measurement', '1'], ['Fields', '6'], ['Series', '128']], '运行查询')}
      <div class="wb-content content-grid no-toolbar">
        <section class="data-pane" style="display:grid;grid-template-rows:minmax(220px,45%) 44px minmax(0,1fr)">
          <div style="display:grid;grid-template-columns:52px 1fr;min-height:0;border-bottom:1px solid var(--line)">
            <div class="code-block" style="border:0;border-radius:0;color:var(--muted);line-height:24px">1\n2\n3\n4\n5\n6</div>
            <pre class="code-block" style="border:0;border-left:1px solid var(--line);border-radius:0;line-height:24px">SELECT time, deviceId, temperature, pressure, vibration
FROM device_metrics
WHERE time &gt; now() - interval '1 hour'
  AND lineId = 'LINE-1'
ORDER BY time DESC
LIMIT 100;</pre>
          </div>
          ${toolbar('<button class="primary" type="button">▶ 运行</button><button class="secondary stage-action" type="button">暂存写入</button><span class="spacer"></span><span>完成 · 12 ms · 100 rows</span>')}
          <div class="data-pane">${table(['time', 'deviceId', 'temperature', 'pressure', 'vibration'], [
            ['2026-07-11 14:31:48', 'PLC-101', '56.7', '1.21', '0.032'], ['2026-07-11 14:31:46', 'PLC-102', '48.1', '1.08', '0.018'], ['2026-07-11 14:31:44', 'SENSOR-77', '42.8', '1.14', '0.026'], ['2026-07-11 14:31:42', 'ROBOT-9', '51.3', '1.19', '0.021']
          ])}</div>
        </section>
        ${inspector('查询上下文', '<div class="inspector-section"><h3>对象</h3><dl class="kv-list"><div><dt>数据库</dt><dd>factory</dd></div><div><dt>Measurement</dt><dd>device_metrics</dd></div><div><dt>时间范围</dt><dd>最近 1 小时</dd></div><div><dt>Series</dt><dd>128</dd></div></dl></div><div class="inspector-section"><h3>字段</h3><div class="token-cloud"><span>temperature f64</span><span>pressure f64</span><span>vibration f64</span><span>status string</span></div></div>')}
      </div>`;
  }
  if (state.subtab === '图表') return `${header([['Series', '4'], ['Points', '5.2k'], ['Window', '1h']], '刷新图表')}<div class="wb-content"><div class="chart" style="height:55%">${[42,55,44,72,61,79,69,83,74,88,64,78,91,82,73,86,68,76,89,80].map(h => `<span style="--h:${h}%"></span>`).join('')}</div><div class="form-workspace"><h2>温度与压力趋势</h2><p>LINE-1 · 10 秒聚合 · 最近 1 小时</p></div></div>`;
  if (state.subtab === '轨迹') return `${header([['Devices', '18'], ['Points', '1.4k'], ['CRS', 'WGS84']], '刷新轨迹')}<div class="wb-content er-canvas"><div class="empty"><div><strong>轨迹地图工作区</strong><p>地图图层、时间播放、设备轨迹和坐标系切换在此呈现。</p></div></div></div>`;
  return `${header([['Rows', '100'], ['Elapsed', '12 ms'], ['Export', 'CSV/JSON']], '重新运行')}<div class="wb-content data-pane">${table(['time','deviceId','temperature','pressure','status'], [['14:31:48','PLC-101','56.7','1.21','OK'],['14:31:46','PLC-102','48.1','1.08','OK'],['14:31:44','SENSOR-77','42.8','1.14','WARN']])}</div>`;
}

function tableView() {
  if (state.subtab === '数据') {
    const body = rows.machines.map((r, i) => [
      `<span class="mono">${r[0]}</span>`, r[1], r[2], `<span class="state ${r[3] === '告警' ? 'danger' : r[3] === '维护' ? 'warning' : r[3] === '运行中' ? 'success' : ''}">${r[3]}</span>`, r[4], `<span class="mono">${r[5]}</span>`
    ]);
    return `${header([['Rows', '248'], ['Columns', '9'], ['Primary key', 'machine_id']], '刷新数据', true)}
      <div class="wb-content">${toolbar('<input placeholder="过滤行或输入条件"><select><option>全部列</option><option>status</option><option>line_id</option></select><button class="secondary" type="button">应用</button><span class="spacer"></span><button class="secondary" type="button">＋ 新增行</button><button class="secondary stage-action" type="button">删除选中</button>')}
      <div class="content-grid"><section class="data-pane">${table(['machine_id','name','line_id','status','load_pct','updated_at'], body, ['110px','190px','100px','100px','90px','190px'])}</section>
      ${inspector('行详情 · M-101', '<div class="inspector-section"><h3>字段</h3><dl class="kv-list"><div><dt>machine_id</dt><dd>M-101</dd></div><div><dt>name</dt><dd>冲压机 A1</dd></div><div><dt>line_id</dt><dd>LINE-1</dd></div><div><dt>status</dt><dd><span class="state success">运行中</span></dd></div><div><dt>load_pct</dt><dd>78.4</dd></div></dl></div><div class="inspector-section"><button class="primary stage-action" type="button">暂存行更新</button></div>')}</div></div>`;
  }
  if (state.subtab === '设计器') return `${header([['Columns', '9'], ['Pending DDL', '2', 'warning'], ['Nullable', '3']], '刷新 Schema', true)}<div class="wb-content form-workspace"><section class="form-section"><h2>表定义</h2><div class="field-grid"><label class="field"><span>表名</span><input value="machines"></label><label class="field"><span>Schema</span><input value="factory"></label></div></section><section class="form-section"><h2>列</h2><div class="column-list"><div class="column-row header"><span></span><span>列名</span><span>数据类型</span><span>Nullable</span><span>操作</span></div>${[['machine_id','VARCHAR(32)','否'],['name','VARCHAR(120)','否'],['line_id','VARCHAR(32)','否'],['status','VARCHAR(24)','否'],['load_pct','DOUBLE','是']].map((r,i)=>`<div class="column-row"><span>${i===0?'🔑':'⋮⋮'}</span><input value="${r[0]}"><select><option>${r[1]}</option></select><span>${r[2]}</span><button class="quiet" type="button">•••</button></div>`).join('')}</div><button class="secondary" type="button" style="margin-top:10px">＋ 添加列</button></section><section class="form-section"><h2>DDL 预览</h2><pre class="code-block">ALTER TABLE machines ADD COLUMN maintenance_owner VARCHAR(80);\nALTER TABLE machines RENAME COLUMN load TO load_pct;</pre></section></div>`;
  if (state.subtab === '索引') return `${header([['Indexes', '4'], ['Rebuildable', '3'], ['Pending', '1', 'warning']], '刷新索引', true)}<div class="wb-content">${toolbar('<button class="primary" type="button">＋ 创建索引</button><span class="spacer"></span><input placeholder="筛选索引">')}<div class="content-grid"><section class="data-pane">${table(['索引','类型','列','唯一','状态','操作'], [['PK_machines','PRIMARY','machine_id','是','<span class="state success">可用</span>','•••'],['IX_machines_line','BTREE','line_id','否','<span class="state success">可用</span>','重建　•••'],['IX_machines_status','BTREE','status','否','<span class="state warning">待重建</span>','重建　•••']])}</section>${inspector('索引详情', '<div class="inspector-section"><dl class="kv-list"><div><dt>名称</dt><dd>IX_machines_line</dd></div><div><dt>列</dt><dd>line_id</dd></div><div><dt>类型</dt><dd>BTREE</dd></div><div><dt>大小</dt><dd>1.8 MiB</dd></div><div><dt>更新时间</dt><dd>14:18:32</dd></div></dl></div>')}</div></div>`;
  if (state.subtab === '导入 / 导出') return `${header([['Mode', 'Import'], ['Format', 'CSV'], ['Target', 'machines']], '新建任务')}<div class="wb-content form-workspace"><div class="steps"><div class="step active"><b>1</b>选择文件</div><div class="step"><b>2</b>列映射</div><div class="step"><b>3</b>预检</div><div class="step"><b>4</b>执行</div><div class="step"><b>5</b>报告</div></div><div class="drop-zone"><div><strong>拖入 CSV、JSON 或 JSONL 文件</strong><p>Studio 将优先打开原生文件选择器；Web 版本使用浏览器文件输入。</p><button class="primary" type="button">选择文件</button></div></div><section class="form-section" style="margin-top:20px"><h2>导出当前表</h2><div class="field-grid"><label class="field"><span>格式</span><select><option>CSV</option><option>JSON</option><option>DDL + Data</option></select></label><label class="field"><span>范围</span><select><option>全部行</option><option>当前过滤结果</option><option>选中行</option></select></label></div></section></div>`;
  if (state.subtab === 'ER') return `${header([['Tables', '12'], ['Relations', '8'], ['Focus', 'machines']], '重新布局')}<div class="wb-content er-canvas"><div class="relation-line a"></div><div class="relation-line b"></div>${erTable('one','production_lines',['line_id PK','name','site_id FK'])}${erTable('two','machines',['machine_id PK','line_id FK','status','load_pct'])}${erTable('three','work_orders',['order_id PK','machine_id FK','state'])}</div>`;
  return `${header([['Object', 'TABLE'], ['Columns', '9'], ['Indexes', '4']], '复制 DDL')}<div class="wb-content form-workspace"><h2>对象定义</h2><pre class="code-block" style="min-height:420px">CREATE TABLE machines (
  machine_id VARCHAR(32) NOT NULL PRIMARY KEY,
  name VARCHAR(120) NOT NULL,
  line_id VARCHAR(32) NOT NULL,
  status VARCHAR(24) NOT NULL,
  load_pct DOUBLE,
  updated_at TIMESTAMP NOT NULL,
  CONSTRAINT fk_machines_line
    FOREIGN KEY (line_id) REFERENCES production_lines(line_id)
);

CREATE INDEX IX_machines_line ON machines(line_id);
CREATE INDEX IX_machines_status ON machines(status);</pre></div>`;
}

function erTable(cls, title, fields) { return `<section class="er-table ${cls}"><header>${title}</header>${fields.map(field => `<div><span>${field}</span><span>⋮</span></div>`).join('')}</section>`; }

function documentView() {
  if (state.subtab === 'Documents') return `${header([['Documents', '18.4k'], ['Indexes', '3'], ['Validator', 'error']], '运行 Find', true)}<div class="wb-content">${toolbar('<input value="{ status: &quot;warning&quot; }"><select><option>Sort: createdAt desc</option></select><button class="primary" type="button">Find</button><span class="spacer"></span><button class="secondary" type="button">＋ 文档</button>')}<div class="content-grid"><section class="data-pane">${table(['_id','deviceId','type','severity','createdAt'], [['evt_01J31','PLC-101','temperature','<span class="state warning">warning</span>','2026-07-11 14:28:22'],['evt_01J30','ROBOT-9','cycle','<span class="state info">info</span>','2026-07-11 14:26:11'],['evt_01J29','SENSOR-77','vibration','<span class="state danger">critical</span>','2026-07-11 14:24:03']])}</section>${inspector('文档详情', '<div class="inspector-section"><h3>JSON</h3><pre class="code-block">{\n  "_id": "evt_01J31",\n  "deviceId": "PLC-101",\n  "type": "temperature",\n  "severity": "warning",\n  "value": 86.4,\n  "createdAt": "2026-07-11T14:28:22Z"\n}</pre></div><div class="inspector-section"><button class="primary stage-action" type="button">暂存替换</button> <button class="secondary stage-action" type="button">删除</button></div>')}</div></div>`;
  if (state.subtab === '查询') return `${header([['Operation', 'Aggregate'], ['Stages', '3'], ['Result', '12']], '执行查询')}<div class="wb-content split-layout"><aside class="list-pane"><header><strong>操作</strong></header>${['Find','Count','Distinct','Aggregate'].map((x,i)=>`<button class="item ${i===3?'active':''}" type="button"><strong>${x}</strong><small>Document query</small></button>`).join('')}</aside><section class="form-workspace"><h2>Aggregation Pipeline</h2><pre class="code-block">[\n  { "$match": { "severity": { "$in": ["warning", "critical"] } } },\n  { "$group": { "_id": "$deviceId", "count": { "$sum": 1 } } },\n  { "$sort": { "count": -1 } }\n]</pre><div style="margin-top:12px"><button class="primary" type="button">运行</button></div></section></div>`;
  if (state.subtab === 'Validator') return `${header([['Action', 'error'], ['Level', 'strict'], ['Samples', '24']], '运行样本预检', true)}<div class="wb-content content-grid no-toolbar"><section class="form-workspace"><h2>Schema Validator</h2><pre class="code-block" style="min-height:330px">{\n  "$jsonSchema": {\n    "required": ["deviceId", "type", "createdAt"],\n    "properties": {\n      "deviceId": { "bsonType": "string" },\n      "severity": { "enum": ["info", "warning", "critical"] },\n      "createdAt": { "bsonType": "date" }\n    }\n  }\n}</pre><div style="margin-top:12px"><button class="secondary" type="button">格式化</button> <button class="primary stage-action" type="button">暂存 Validator</button></div></section>${inspector('样本预检', '<div class="inspector-section"><dl class="kv-list"><div><dt>通过</dt><dd>23</dd></div><div><dt>警告</dt><dd>0</dd></div><div><dt>拒绝</dt><dd>1</dd></div></dl></div><div class="inspector-section"><h3>拒绝样本</h3><pre class="code-block">evt_01J22\nmissing: createdAt</pre></div>')}</div>`;
  if (state.subtab === '索引') return `${header([['JSON path', '2'], ['FullText', '1'], ['Pending', '0']], '刷新索引', true)}<div class="wb-content data-pane">${table(['索引','类型','路径 / 字段','状态','操作'], [['idx_device_created','JSON PATH','deviceId, createdAt','<span class="state success">可用</span>','•••'],['idx_severity','JSON PATH','severity','<span class="state success">可用</span>','•••'],['docs.search','FULLTEXT','type, message','<span class="state success">可用</span>','重建']])}</div>`;
  return `${header([['Format', 'JSONL'], ['Mode', 'Insert'], ['Last run', '18.4k']], '新建导入')}<div class="wb-content form-workspace"><div class="steps"><div class="step active"><b>1</b>文件</div><div class="step"><b>2</b>模式</div><div class="step"><b>3</b>预检</div><div class="step"><b>4</b>报告</div></div><div class="drop-zone"><div><strong>选择 JSON 或 JSONL</strong><p>支持 insert 与 replace；写入前执行 validator 样本预检。</p><button class="primary" type="button">选择文件</button></div></div></div>`;
}

function kvView() {
  if (state.subtab === '浏览器') return `${header([['Total keys', '12.8k'], ['Expiring', '3.2k'], ['Nearest TTL', '18s', 'warning']], '扫描 Keyspace', true)}<div class="wb-content">${toolbar('<input value="device:"><select><option>100 keys</option><option>250 keys</option></select><button class="primary" type="button">扫描</button><span class="spacer"></span><button class="secondary" type="button">＋ 新增 Key</button>')}<div class="content-grid"><div class="split-layout"><aside class="list-pane"><header><strong>前缀</strong></header>${['device:','device:PLC-','device:SENSOR-','session:','cache:'].map((x,i)=>`<button class="item ${i===1?'active':''}" type="button"><strong>${x}</strong><span>${[12480,4820,3160,1290,840][i]}</span><small>namespace prefix</small></button>`).join('')}</aside><section class="data-pane">${table(['Key','类型','TTL','大小'], [['device:PLC-101:state','JSON','42s','384 B'],['device:PLC-101:lastSeen','String','18s','28 B'],['device:PLC-102:state','JSON','55s','378 B'],['device:PLC-103:alarm','String','persistent','64 B']])}</section></div>${inspector('Key 详情', '<div class="inspector-section"><dl class="kv-list"><div><dt>Key</dt><dd>device:PLC-101:state</dd></div><div><dt>类型</dt><dd>JSON</dd></div><div><dt>TTL</dt><dd>42 秒</dd></div><div><dt>过期时间</dt><dd>14:32:30</dd></div></dl></div><div class="inspector-section"><h3>值 · JSON</h3><pre class="code-block">{\n  "status": "online",\n  "temperature": 56.7,\n  "line": "LINE-1"\n}</pre></div><div class="inspector-section"><button class="primary stage-action" type="button">暂存 Set</button> <button class="secondary stage-action" type="button">修改 TTL</button></div>')}</div></div>`;
  if (state.subtab === '批量操作') return `${header([['Selected prefix', 'device:PLC-'], ['Matched', '4,820'], ['Risk', 'High', 'warning']], '重新统计', true)}<div class="wb-content form-workspace"><section class="form-section"><h2>批量操作</h2><div class="field-grid"><label class="field"><span>操作</span><select><option>批量 Set</option><option>批量 Remove</option><option>前缀删除</option><option>清理过期</option></select></label><label class="field"><span>目标前缀</span><input value="device:PLC-"></label><label class="field"><span>TTL 策略</span><select><option>保留 TTL</option><option>设为持久</option><option>指定秒数</option></select></label><label class="field"><span>预计影响</span><input value="4,820 keys" readonly></label></div></section><section class="form-section"><h2>预检结果</h2>${table(['范围','Keys','Bytes','最早过期','操作'], [['device:PLC-*','4,820','18.2 MiB','18s','等待暂存']])}<button class="danger stage-action" type="button" style="margin-top:14px">暂存前缀删除</button></section></div>`;
  return `${header([['Total', '12.8k'], ['Active', '9.6k'], ['Expired', '184']], '刷新统计')}<div class="wb-content"><div class="chart" style="height:44%">${[36,44,48,53,61,57,69,64,73,78,71,82,75,88,80,76].map(h=>`<span style="--h:${h}%"></span>`).join('')}</div><div class="form-workspace"><h2>Keyspace 生命周期</h2><p>过去 24 小时 active / expiring / expired key 变化。</p></div></div>`;
}

function mqView() {
  if (state.subtab === '概览') return `${header([['Messages', '48'], ['Consumer lag', '4', 'warning'], ['Consumers', '1', 'success']], '刷新监控')}<div class="wb-content"><div class="chart" style="height:42%">${[31,38,45,42,54,60,52,67,74,66,78,82,75,89,84,72,80,91].map(h=>`<span style="--h:${h}%"></span>`).join('')}</div><div class="form-workspace"><div class="field-grid"><section><h2>Topic 状态</h2><dl class="kv-list"><div><dt>High water</dt><dd>125,678</dd></div><div><dt>Low water</dt><dd>121,024</dd></div><div><dt>Segment</dt><dd>000000000001E920</dd></div></dl></section><section><h2>Retention / DLQ</h2><dl class="kv-list"><div><dt>Retention</dt><dd>7 days / 4 GiB</dd></div><div><dt>DLQ</dt><dd>telemetry.raw.dlq</dd></div><div><dt>DLQ messages</dt><dd>3</dd></div></dl></section></div></div></div>`;
  if (state.subtab === '消息') return `${header([['Messages', '48'], ['Consumer lag', '4', 'warning'], ['Consumers', '1', 'success']], '刷新消息', true)}<div class="wb-content">${toolbar('<button class="secondary" type="button">Ⅱ 暂停消费</button><select><option>实时消费</option><option>从 Offset</option></select><input value="125,650"><button class="secondary" type="button">定位</button><select><option>最近 1 小时</option></select><span class="spacer"></span><button class="primary" type="button">发布测试消息</button>')}<div class="content-grid"><section class="data-pane">${table(['偏移量','时间','Headers','Payload'], rows.mq.map(r=>[`<span class="mono">${r[0]}</span>`,`<span class="mono">${r[1]}</span>`,escapeHtml(r[2]),escapeHtml(r[3])]), ['100px','190px','260px'])}</section>${inspector('消息详情', '<div class="inspector-section"><dl class="kv-list"><div><dt>偏移量</dt><dd>125,678</dd></div><div><dt>时间</dt><dd>2026-07-11 14:31:48.123</dd></div><div><dt>大小</dt><dd>512 B</dd></div></dl></div><div class="inspector-section"><h3>Headers (5)</h3><dl class="kv-list"><div><dt>deviceId</dt><dd>PLC-101</dd></div><div><dt>type</dt><dd>telemetry</dd></div><div><dt>partition</dt><dd>2</dd></div></dl></div><div class="inspector-section"><h3>Payload · JSON</h3><pre class="code-block">{\n  "deviceId": "PLC-101",\n  "temperature": 56.7,\n  "pressure": 1.21,\n  "status": "OK"\n}</pre></div>')}</div></div>`;
  if (state.subtab === '消费者组') return `${header([['Groups', '3'], ['Total lag', '4', 'warning'], ['Active', '2', 'success']], '刷新消费者')}<div class="wb-content">${toolbar('<input placeholder="筛选消费者组"><span class="spacer"></span><button class="secondary stage-action" type="button">Ack Offset</button>')}<div class="content-grid"><section class="data-pane">${table(['消费者组','状态','Committed','High water','Lag','最后活动'], [['iotsharp-rule-engine','<span class="state success">active</span>','125,674','125,678','4','14:31:47'],['telemetry-exporter','<span class="state success">active</span>','125,678','125,678','0','14:31:48'],['diagnostic-worker','<span class="state warning">idle</span>','125,661','125,678','17','13:58:02']])}</section>${inspector('消费者组详情', '<div class="inspector-section"><dl class="kv-list"><div><dt>Group</dt><dd>iotsharp-rule-engine</dd></div><div><dt>Committed</dt><dd>125,674</dd></div><div><dt>Lag</dt><dd>4</dd></div><div><dt>Delivery</dt><dd>pull / durable</dd></div></dl></div><div class="chart" style="height:130px">'+[30,42,34,55,48,72,60,44].map(h=>`<span style="--h:${h}%"></span>`).join('')+'</div>')}</div></div>`;
  return `${header([['Durability', 'durable'], ['Retention', '7 days'], ['Segments', '12']], '刷新配置')}<div class="wb-content form-workspace"><section class="form-section"><h2>Topic 配置</h2><div class="field-grid"><label class="field"><span>Retention age</span><input value="7 days" readonly></label><label class="field"><span>Retention size</span><input value="4 GiB" readonly></label><label class="field"><span>Flush on publish</span><input value="true" readonly></label><label class="field"><span>Group commit</span><input value="enabled" readonly></label><label class="field"><span>Segment max bytes</span><input value="128 MiB" readonly></label><label class="field"><span>DLQ topic</span><input value="telemetry.raw.dlq" readonly></label></div></section><section class="form-section"><p><span class="state info">只读</span> 当前页面展示 Topic 运行配置。维护动作必须通过既有管理 API 和写审批。</p></section></div>`;
}

function vectorView() {
  if (state.subtab === '检索 Playground') return `${header([['Rows', '84.2k'], ['Dimension', '768'], ['Metric', 'cosine']], '刷新索引')}<div class="wb-content content-grid no-toolbar"><section class="data-pane" style="display:grid;grid-template-rows:auto minmax(0,1fr)"><div class="form-workspace" style="border-bottom:1px solid var(--line)"><div class="field-grid"><label class="field"><span>查询模式</span><select><option>Text Embed</option><option>Raw Vector</option></select></label><label class="field"><span>Top-K</span><input value="10"></label><label class="field" style="grid-column:1/-1"><span>查询文本</span><textarea>变频电机轴承温度升高并伴随高频振动</textarea></label><label class="field" style="grid-column:1/-1"><span>Metadata filter</span><input value="lineId = 'LINE-1' AND time &gt; now() - interval '7 days'"></label></div><div style="margin-top:12px"><button class="primary" type="button">生成向量并检索</button> <span class="state success">dimension match · 768</span></div></div><div class="data-pane">${table(['Rank','Score','deviceId','摘要','time'], [['1','0.942','M-104','轴承温度 86.4°C，高频振动 0.83g','2026-07-11 13:22'],['2','0.918','M-101','主轴温升趋势超过基线 18%','2026-07-10 18:42'],['3','0.887','M-118','驱动端振动频谱出现 2x 峰值','2026-07-09 09:13']])}</div></section>${inspector('命中详情 · #1', '<div class="inspector-section"><dl class="kv-list"><div><dt>Score</dt><dd>0.942</dd></div><div><dt>Device</dt><dd>M-104</dd></div><div><dt>Line</dt><dd>LINE-1</dd></div><div><dt>Time</dt><dd>2026-07-11 13:22</dd></div></dl></div><div class="inspector-section"><h3>Fields</h3><pre class="code-block">temperature: 86.4\nvibration_rms: 0.83\nfrequency_peak: 118.2\nstatus: warning</pre></div><div class="inspector-section"><h3>Tags</h3><div class="token-cloud"><span>lineId=LINE-1</span><span>deviceType=motor</span></div></div>')}</div>`;
  return `${header([['Rows', '84.2k'], ['Dimension', '768'], ['Metric', 'cosine']], '刷新统计')}<div class="wb-content form-workspace"><section class="form-section"><h2>索引定义</h2><dl class="kv-list"><div><dt>Measurement</dt><dd>maintenance_embeddings</dd></div><div><dt>Column</dt><dd>embedding</dd></div><div><dt>Metric</dt><dd>cosine</dd></div><div><dt>Dimension</dt><dd>768</dd></div><div><dt>Rows</dt><dd>84,218</dd></div></dl></section><section class="form-section"><h2>HNSW 参数</h2><div class="field-grid"><label class="field"><span>M</span><input value="16" readonly></label><label class="field"><span>efConstruction</span><input value="200" readonly></label><label class="field"><span>efSearch</span><input value="64" readonly></label><label class="field"><span>Build state</span><input value="ready" readonly></label></div></section></div>`;
}

function fulltextView() {
  if (state.subtab === '检索') return `${header([['Documents', '1.24m'], ['Terms', '4.8m'], ['Tokenizer', 'jieba']], '刷新索引', true)}<div class="wb-content">${toolbar('<input style="min-width:360px" value="轴承 温度 异常"><select><option>All terms</option><option>Any term</option><option>Phrase</option><option>Fuzzy</option></select><select><option>全部字段</option><option>title</option><option>message</option></select><button class="primary" type="button">检索</button>')}<div class="content-grid"><section class="data-pane">${table(['Score','标题','高亮摘要','来源','time'], [['12.84','M-104 轴承温度异常','驱动端 <mark>轴承</mark> <mark>温度</mark> 超过阈值…','alarm','14:22:18'],['11.26','LINE-1 点检记录','发现高频振动，建议检查 <mark>轴承</mark>…','inspection','12:08:44'],['9.72','预测维护建议','<mark>温度</mark>与载荷相关性出现<mark>异常</mark>…','copilot','09:41:02']])}</section>${inspector('检索命中详情', '<div class="inspector-section"><dl class="kv-list"><div><dt>Score</dt><dd>12.84</dd></div><div><dt>Index</dt><dd>docs.search</dd></div><div><dt>Tokenizer</dt><dd>jieba</dd></div></dl></div><div class="inspector-section"><h3>完整文档</h3><pre class="code-block">{\n  "title": "M-104 轴承温度异常",\n  "message": "驱动端轴承温度超过阈值...",\n  "source": "alarm"\n}</pre></div>')}</div></div>`;
  if (state.subtab === 'Analyzer') return `${header([['Tokenizer', 'jieba'], ['Tokens', '9'], ['Mode', 'search']], '刷新配置')}<div class="wb-content form-workspace"><section class="form-section"><h2>分词器预览</h2><label class="field"><span>输入文本</span><textarea>变频电机轴承温度升高并伴随高频振动</textarea></label><div style="margin-top:12px"><button class="primary" type="button">Analyze</button> <button class="secondary" type="button">使用查询文本</button></div></section><section class="form-section"><h2>Tokens</h2><div class="token-cloud"><span>变频</span><span>电机</span><span>轴承</span><span>温度</span><span>升高</span><span>伴随</span><span>高频</span><span>振动</span></div></section></div>`;
  return `${header([['Documents', '1.24m'], ['Terms', '4.8m'], ['Tokenizer', 'jieba']], '刷新详情', true)}<div class="wb-content form-workspace"><section class="form-section"><h2>索引定义</h2><dl class="kv-list"><div><dt>Collection</dt><dd>device-events</dd></div><div><dt>Fields</dt><dd>title, message, tags</dd></div><div><dt>Tokenizer</dt><dd>jieba / CJK</dd></div><div><dt>Documents</dt><dd>1,240,882</dd></div><div><dt>Terms</dt><dd>4,814,092</dd></div></dl></section><button class="danger stage-action" type="button">暂存 Rebuild</button></div>`;
}

function bucketView() {
  if (state.subtab === '对象') return `${header([['Objects', '18.2k'], ['Current bytes', '86.4 GiB'], ['Versions', '24.8k']], '刷新对象', true)}<div class="wb-content">${toolbar('<button class="secondary" type="button">↑ 上传</button><button class="secondary" type="button">＋ 新建前缀</button><input value="factory/line-1/"><span class="spacer"></span><select><option>100 objects</option></select>')}<div class="content-grid"><section class="data-pane"><div class="file-grid">${[['▱','..','返回上级'],['□','2026-07-11/','前缀 · 1,284 对象'],['□','2026-07-10/','前缀 · 1,518 对象'],['▧','line-1-telemetry.parquet','84.2 MiB · 14:28'],['▧','inspection-photos.zip','1.8 GiB · 12:40'],['▧','alarm-export.jsonl','42.8 MiB · 09:18']].map(f=>`<div class="file-row"><span>${f[0]}</span><strong>${f[1]}</strong><small>${f[2]}</small></div>`).join('')}</div></section>${inspector('对象详情', '<div class="inspector-section"><dl class="kv-list"><div><dt>Key</dt><dd>factory/line-1/line-1-telemetry.parquet</dd></div><div><dt>大小</dt><dd>84.2 MiB</dd></div><div><dt>版本</dt><dd>3</dd></div><div><dt>ETag</dt><dd>9f2a…e81c</dd></div><div><dt>Content-Type</dt><dd>application/parquet</dd></div></dl></div><div class="inspector-section"><button class="primary" type="button">下载</button> <button class="secondary" type="button">Presigned URL</button></div>')}</div></div>`;
  if (state.subtab === '治理') return `${header([['Versioning', 'Enabled'], ['Retention', '30 days'], ['Quota left', '13.6 GiB']], '刷新治理', true)}<div class="wb-content form-workspace"><section class="form-section"><h2>生命周期</h2><div class="field-grid"><label class="field"><span>规则状态</span><select><option>启用</option></select></label><label class="field"><span>前缀</span><input value="factory/"></label><label class="field"><span>转为归档</span><input value="7 days"></label><label class="field"><span>删除非当前版本</span><input value="30 days"></label></div></section><section class="form-section"><h2>Retention / Legal hold</h2><div class="field-grid"><label class="field"><span>默认保留</span><input value="30 days"></label><label class="field"><span>Legal hold</span><select><option>关闭</option></select></label><label class="field"><span>容量配额</span><input value="100 GiB"></label><label class="field"><span>剩余</span><input value="13.6 GiB" readonly></label></div></section><section class="form-section"><button class="primary stage-action" type="button">暂存治理变更</button></section></div>`;
  if (state.subtab === 'Multipart') return `${header([['Sessions', '3'], ['Parts', '28'], ['Pending bytes', '2.4 GiB']], '刷新会话', true)}<div class="wb-content">${toolbar('<input placeholder="筛选 upload id 或 key"><span class="spacer"></span><button class="secondary" type="button">发起 Multipart</button>')}<div class="content-grid"><section class="data-pane">${table(['Upload ID','Object key','Parts','Bytes','Started','状态'], [['up_72f1','factory/line-1/archive-0711.zip','12','1.8 GiB','14:02','<span class="state info">uploading</span>'],['up_6cc4','models/anomaly-v4.onnx','8','512 MiB','13:42','<span class="state warning">stalled</span>'],['up_5a88','exports/telemetry.parquet','8','128 MiB','12:18','<span class="state success">ready</span>']])}</section>${inspector('Multipart 会话', '<div class="inspector-section"><dl class="kv-list"><div><dt>Upload ID</dt><dd>up_72f1</dd></div><div><dt>Parts</dt><dd>12 / 16</dd></div><div><dt>Bytes</dt><dd>1.8 GiB</dd></div></dl></div><div class="inspector-section"><button class="primary stage-action" type="button">完成上传</button> <button class="danger stage-action" type="button">中止</button></div>')}</div></div>`;
  return `${header([['Events', '284'], ['Denied', '7', 'warning'], ['Window', '24h']], '刷新审计')}<div class="wb-content">${toolbar('<select><option>全部动作</option><option>PUT</option><option>GET</option><option>DELETE</option></select><input placeholder="用户、Key 或 request id"><select><option>最近 24 小时</option></select>')}<div class="data-pane" style="height:calc(100% - 54px)">${table(['时间','Actor','动作','Object key','结果','Request ID'], [['14:28:18','operator-a','GET','factory/line-1/line-1-telemetry.parquet','<span class="state success">200</span>','req_8f21'],['14:21:03','release-service','PUT','models/anomaly-v4.onnx','<span class="state success">200</span>','req_8e72'],['13:58:44','viewer-b','DELETE','factory/line-1/archive.zip','<span class="state danger">403</span>','req_8d19']])}</div></div>`;
}

function render() {
  const meta = modelMeta[state.model];
  breadcrumb.innerHTML = `工厂数据 <span>›</span> factory <span>›</span> ${meta.group} <span>›</span> ${escapeHtml(state.object)}`;
  activeObjectTab.innerHTML = `<span>${meta.icon}</span> ${escapeHtml(state.object)} <b>×</b>`;
  const renderers = { sql: sqlView, table: tableView, document: documentView, kv: kvView, mq: mqView, vector: vectorView, fulltext: fulltextView, bucket: bucketView };
  workbench.innerHTML = `<div class="wb-shell">${renderers[state.model]()}</div>`;
  bindWorkbench();
}

function bindWorkbench() {
  document.querySelectorAll('[data-subtab]').forEach(button => button.addEventListener('click', () => {
    state.subtab = button.dataset.subtab;
    render();
  }));
  document.querySelectorAll('.stage-action').forEach(button => button.addEventListener('click', () => openApproval(button.textContent.trim())));
}

function selectModel(model, object) {
  if (!modelMeta[model]) return;
  state.model = model;
  state.object = object || ({ sql: 'device_metrics', table: 'machines', document: 'device-events', kv: 'device-cache', mq: 'telemetry.raw', vector: 'embeddings.vector', fulltext: 'docs.search', bucket: 'raw-data' })[model];
  state.subtab = modelMeta[model].tabs[0];
  document.querySelectorAll('.tree-item').forEach(item => item.classList.toggle('active', item.dataset.model === model && item.dataset.object === state.object));
  render();
}

document.querySelectorAll('.tree-item').forEach(item => item.addEventListener('click', () => selectModel(item.dataset.model, item.dataset.object)));
document.querySelectorAll('.group-label').forEach(button => button.addEventListener('click', () => {
  const group = button.closest('.tree-group');
  group.classList.toggle('open');
  group.querySelectorAll('.tree-item').forEach(item => item.hidden = !group.classList.contains('open'));
  button.firstElementChild.textContent = group.classList.contains('open') ? '⌄' : '›';
}));

document.querySelectorAll('.rail-item[data-model]').forEach(button => button.addEventListener('click', () => selectModel(button.dataset.model)));

document.querySelector('#treeSearch').addEventListener('input', event => {
  const value = event.target.value.trim().toLowerCase();
  document.querySelectorAll('.tree-group').forEach(group => {
    let hasMatch = group.dataset.group.toLowerCase().includes(value);
    group.querySelectorAll('.tree-item').forEach(item => {
      const match = !value || item.textContent.toLowerCase().includes(value) || item.dataset.model.includes(value);
      item.classList.toggle('filtered-out', !match);
      hasMatch ||= match;
    });
    group.classList.toggle('filtered-out', !hasMatch);
  });
});

function toggleSurface(id, force) {
  const element = document.querySelector(id);
  const open = force ?? !element.classList.contains('open');
  element.classList.toggle('open', open);
  element.setAttribute('aria-hidden', String(!open));
}

document.querySelector('#resultsButton').addEventListener('click', () => toggleSurface('#resultDrawer'));
document.querySelector('#historyButton').addEventListener('click', () => toggleSurface('#historyDrawer'));
document.querySelector('#connectionButton').addEventListener('click', () => toggleSurface('#connectionPopover'));
document.querySelectorAll('.drawer-close').forEach(button => button.addEventListener('click', () => {
  const parent = button.closest('.drawer, .popover, .modal-backdrop');
  if (parent) { parent.classList.remove('open'); parent.setAttribute('aria-hidden', 'true'); }
}));

function openApproval(action) {
  document.querySelector('#approvalTarget').textContent = state.object;
  document.querySelector('#approvalAction').textContent = action || '暂存变更';
  const preview = state.model === 'kv' ? `REMOVE PREFIX '${state.object}:device:PLC-';` : state.model === 'mq' ? `ACK TOPIC ${state.object} GROUP iotsharp-rule-engine OFFSET 125678;` : state.model === 'bucket' ? `ALTER BUCKET ${state.object} SET RETENTION = '30 days';` : state.model === 'document' ? `REPLACE DOCUMENT ${state.object} ID 'evt_01J31';` : `UPDATE ${state.object} SET status = @p0 WHERE id = @p1;`;
  document.querySelector('#approvalPreview').textContent = preview;
  document.querySelector('#approvalCheck').checked = false;
  document.querySelector('.confirm-approval').disabled = true;
  toggleSurface('#approvalModal', true);
}

document.querySelector('#approvalCheck').addEventListener('change', event => { document.querySelector('.confirm-approval').disabled = !event.target.checked; });
document.querySelector('.cancel-approval').addEventListener('click', () => toggleSurface('#approvalModal', false));
document.querySelector('.confirm-approval').addEventListener('click', () => { toggleSurface('#approvalModal', false); toggleSurface('#resultDrawer', true); });

document.addEventListener('keydown', event => {
  if (event.key === 'Escape') ['#approvalModal','#connectionPopover','#historyDrawer','#resultDrawer'].some(id => {
    const el = document.querySelector(id); if (!el.classList.contains('open')) return false; toggleSurface(id, false); return true;
  });
});

render();
