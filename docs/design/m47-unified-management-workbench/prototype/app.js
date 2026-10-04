/* M47 评审原型：仅本机示例数据和交互，不调用 Server、AI 或 MCP。 */
'use strict';
const catalog = window.M47_CATALOG;
const state = { pageId: 'sql', section: 'workbench', task: 0, resultView: '表格', mode: 'normal', host: 'web', selectedRow: 0, inspectorView: 'info', opened: ['sql'], history: [], connection: 'Factory / Local', drawerFocus: null, drafts: {} };
const $ = (id) => document.getElementById(id);
const esc = (value) => String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const iconPaths = {
  overview:'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
  workbench:'M3 5h18v14H3z M3 9h18 M8 9v10',
  observe:'M3 12h4l3-7 4 14 3-7h4',
  flows:'M4 4h5v5H4z M15 15h5v5h-5z M6 9v8h9 M9 6h8v9',
  ai:'M12 3v3 M12 18v3 M3 12h3 M18 12h3 M6 6l2 2 M16 16l2 2 M6 18l2-2 M16 8l2-2 M8 8h8v8H8z',
  govern:'M12 3l8 4v5c0 5-8 9-8 9s-8-4-8-9V7z M8 12l3 3 5-6',
  settings:'M9 3h6l1 4 4 1v8l-4 1-1 4H9l-1-4-4-1V8l4-1z M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6',
  search:'M10 4a6 6 0 1 0 0 12 6 6 0 0 0 0-12 M15 15l5 5',
  chevron:'M8 10l4 4 4-4', bell:'M6 16h12l-2-3V9a4 4 0 0 0-8 0v4z M10 20h4',
  plus:'M12 5v14 M5 12h14', refresh:'M20 9a8 8 0 1 0-1 9 M20 3v6h-6',
  panel:'M3 4h18v16H3z M9 4v16',close:'M6 6l12 12 M18 6 6 18',
  copy:'M8 8h12v12H8z M16 8V4H4v12h4',lock:'M7 10V7a5 5 0 0 1 10 0v3 M5 10h14v11H5z M12 14v3',
  play:'M8 4l12 8-12 8z', stop:'M6 6h12v12H6z',database:'M4 6c0-4 16-4 16 0s-16 4-16 0 M4 6v12c0 4 16 4 16 0V6 M4 12c0 4 16 4 16 0',
  measurement:'M3 15l4-6 4 8 4-12 3 7h3',table:'M3 4h18v16H3z M3 9h18 M8 9v11 M15 9v11 M3 14h18',
  document:'M8 3H5v18h3 M16 3h3v18h-3 M10 8h4 M10 12h4 M10 16h4',kv:'M8 5a5 5 0 1 0 4 8l7 7 2-2-3-3 2-2-4-4a5 5 0 0 0-8-4',
  mq:'M3 5h18v12H9l-6 4z M7 9h10 M7 13h6',vector:'M5 5h4v4H5z M15 15h4v4h-4z M15 5h4v4h-4z M5 15h4v4H5z M9 7h6 M7 9v6 M17 9v6 M9 17h6',
  fulltext:'M3 5h18 M8 5v14 M4 19h8 M15 10h6 M15 15h6 M15 20h4',bucket:'M3 6h7l2 3h9v11H3z',
  graph:'M5 7a3 3 0 1 0 0 .1 M18 4a3 3 0 1 0 0 .1 M15 18a3 3 0 1 0 0 .1 M8 7l7-2 M7 10l6 6 M18 7l-2 8',
  info:'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18 M12 7v1 M12 11v6',
  history:'M3 11a9 9 0 1 1 3 8 M3 5v6h6 M12 7v6l4 2',file:'M5 3h9l5 5v13H5z M14 3v6h5 M8 13h8 M8 17h6',download:'M12 3v12 M7 10l5 5 5-5 M4 17v4h16v-4',
};
function icon(name) { return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${iconPaths[name] || iconPaths.file}"></path></svg>`; }
function icons(root = document) { root.querySelectorAll('[data-icon]').forEach((el) => { el.innerHTML = icon(el.dataset.icon); }); }
function allPages() { return catalog.sections.flatMap((s) => s.pages.map((p) => ({ ...p, section: s.id }))); }
function findPage(id) { return allPages().find((p) => p.id === id) || catalog.models.find((p) => p.id === id); }
function current() { return findPage(state.pageId) || allPages()[0]; }
function currentTask() { return window.M47_TASK_DETAILS?.[state.pageId]?.[state.task]; }
function draftKey(name) { return `${state.pageId}:${state.task}:${name}`; }
function button(label, action, primary = false, glyph = '') { return `<button class="button${primary ? ' primary' : ''}" data-action="${action}">${glyph ? icon(glyph) : ''}${esc(label)}</button>`; }
function scopeName(scope) { return scope === 'instance' ? '实例 Factory / Local' : scope === 'host' ? '当前宿主' : '数据库 factory'; }
function isModel(page) { return catalog.models.some((m) => m.id === page.id); }
function sectionFor(page) { return page.section || 'workbench'; }
function primaryAction(page) {
  const label = page.primary || '';
  if (/添加.*连接/.test(label)) return 'add-connection';
  if (/新建|创建/.test(label)) return 'new-resource';
  if (/打开工作台/.test(label)) return 'new-query';
  if (/恢复/.test(label)) return 'restore-context';
  if (/配置|令牌|编辑|设置|保存|删除|暂存|导入|重建/.test(label)) return 'approve';
  return 'run';
}
function readOnly() { return state.mode === 'readonly' || state.host === 'vscode' || innerWidth < 800; }
function blocked() { return readOnly() || ['offline', 'permission', 'loading', 'error'].includes(state.mode); }
function navigate(id, add = true) {
  const page = findPage(id);
  if (!page) return;
  state.pageId = id; state.section = sectionFor(page); state.task = 0; state.selectedRow = 0; state.inspectorView = 'info';
  if (add && !state.opened.includes(id)) state.opened = [...state.opened.slice(-7), id];
  if (location.hash !== `#${id}`) history.pushState(null, '', `#${id}`);
  $('shell').classList.toggle('results-hidden', !['sql','notebook'].includes(page.type));
  render();
}
function renderRail() {
  $('rail').innerHTML = catalog.sections.map((s) => `${s.id === 'settings' ? '<div class="rail-spacer"></div>' : ''}<button class="rail-item${s.id === state.section ? ' active' : ''}" data-section="${s.id}" aria-label="${esc(s.label)}" ${s.id === state.section ? 'aria-current="page"' : ''}>${icon(s.id)}<small>${esc(s.label)}</small></button>`).join('');
}
function renderSidebar() {
  const section = catalog.sections.find((s) => s.id === state.section);
  const term = $('resourceSearch').value.trim().toLowerCase();
  $('sidebarTitle').textContent = state.section === 'workbench' ? '资源浏览器' : section.label;
  if (state.section !== 'workbench') {
    $('sidebarBody').innerHTML = `<div class="sidebar-section-label">${scopeName(current().scope)}</div>` + section.pages.filter((p) => `${p.title} ${p.intro}`.toLowerCase().includes(term)).map((p) => `<button class="secondary-item${p.id === state.pageId ? ' active' : ''}" data-page="${p.id}">${esc(p.tabLabel || p.title)}${p.status === 'planned' ? '<span class="planned-marker">规划</span>' : ''}</button>`).join('');
    return;
  }
  const matched = catalog.models.filter((m) => `${m.title} ${m.objectName} ${m.group}`.toLowerCase().includes(term));
  const group = (m) => `<details class="tree-group" open><summary>${icon(m.id)}<span>${esc(m.group || m.title)}</span><small>${m.id === 'graph' ? 'Beta' : '1'}</small></summary><button class="tree-item${state.pageId === m.id ? ' active' : ''}" data-page="${m.id}">${icon(m.id)}<span class="name">${esc(state.mode === 'long' ? m.objectName + '_production_line_02_2026_archive_extended' : m.objectName)}</span></button></details>`;
  $('sidebarBody').innerHTML = `<div class="sidebar-section-label">查询与最近工作</div>${section.pages.filter((p) => p.title.toLowerCase().includes(term)).map((p) => `<button class="secondary-item${state.pageId === p.id ? ' active' : ''}" data-page="${p.id}">${icon(p.type === 'sql' ? 'play' : 'file')}${esc(p.tabLabel || p.title)}${p.status === 'planned' ? '<span class="planned-marker">规划</span>' : ''}</button>`).join('')}<div class="tree-scope">数据库资源</div><div class="tree-context">${icon('database')} factory <span>9 个模型</span></div>${matched.map(group).join('')}${!matched.length && term ? '<div class="notice">没有匹配的资源，尝试缩短搜索名称。</div>' : ''}`;
}
function renderTabs() {
  $('workspaceTabs').innerHTML = state.opened.map((id) => {
    const page = findPage(id);
    return `<div class="workspace-tab${id === state.pageId ? ' active' : ''}"><button class="tab-open" data-page="${id}" ${id === state.pageId ? 'aria-current="page"' : ''}>${icon(isModel(page) ? page.id : page.type === 'sql' ? 'play' : page.section)}<span>${esc(page.objectName || page.tabLabel || page.title)}</span></button><button class="tab-close" aria-label="关闭 ${esc(page.objectName || page.title)}" data-close-tab="${id}">${icon('close')}</button></div>`;
  }).join('') + `<button class="workspace-tab tab-add" data-action="new-query" aria-label="新建查询">${icon('plus')}</button><span class="tab-spacer"></span><button class="icon-button" data-action="history" aria-label="打开历史">${icon('history')}</button>`;
  $('taskTabs').innerHTML = (current().tabs || ['概览']).map((tab, i) => `<button class="task-tab${i === state.task ? ' active' : ''}" data-task="${i}" ${i === state.task ? 'aria-current="page"' : ''}>${esc(tab)}</button>`).join('');
}
function table(columns, rows, result = false) {
  const cells = (row) => row.map((value) => {
    const text = String(value);
    const numeric = /^-?[\d,.]+(?:\s*(?:ms|%|MB|GiB|℃))?$/.test(text);
    const healthy = ['运行中', '健康', '已完成', 'Active', 'Healthy', 'online', 'ready', '成功'].includes(text);
    const warning = ['待核对', '规划', 'Degraded', '部分失败'].includes(text);
    return `<td class="${numeric ? 'numeric' : ''}" title="${esc(text)}">${healthy || warning ? `<span class="cell-state${warning ? ' warn' : ''}">${esc(text)}</span>` : esc(text)}</td>`;
  }).join('');
  return `<div class="data-table-wrap"><table class="data-table"><thead><tr><th class="row-index">#</th>${columns.map((c) => `<th>${esc(c)}</th>`).join('')}</tr></thead><tbody>${rows.map((row, i) => `<tr data-row="${i}" class="${i === state.selectedRow ? 'selected' : ''}" tabindex="0" aria-selected="${i === state.selectedRow}"><td class="row-index">${i + 1}</td>${cells(row)}</tr>`).join('')}</tbody></table></div>${!result && !rows.length ? emptyContent() : ''}`;
}
const sqlColumns = ['time', 'DeviceID', 'temperature', 'pressure', 'quality'];
const sqlRows = [
  ['2026-10-04 09:42:00.000', 'Pump-01', '72.6', '0.84', 'online'],
  ['2026-10-04 09:41:55.000', 'Pump-01', '72.4', '0.83', 'online'],
  ['2026-10-04 09:41:50.000', 'Pump-02', '68.1', '0.79', 'online'],
  ['2026-10-04 09:41:45.000', 'Pump-02', '68.3', '0.80', 'online'],
  ['2026-10-04 09:41:40.000', 'Pump-02', '71.2', '0.81', 'online'],
];
function sqlEditor() {
  const draft = state.drafts[draftKey('sql')];
  return `<div class="content-toolbar"><div class="actions"><label>数据库 <select aria-label="查询数据库"><option>factory</option><option>analytics</option></select></label><label>预算 <select aria-label="结果预算"><option>100 行 / 1 MiB</option><option>1000 行 / 4 MiB</option></select></label></div><div class="actions"><button class="text-button" data-action="format">格式化</button>${button('EXPLAIN','explain')}${button('执行','run',true,'play')}</div></div><div class="editor"><div class="line-numbers" aria-hidden="true">1<br>2<br>3<br>4<br>5<br>6</div><div class="sql-editor" id="sqlEditor" role="textbox" aria-label="SQL 编辑器" aria-multiline="true" contenteditable="true" spellcheck="false"><span class="kw">SELECT</span> time, DeviceID, temperature, pressure, quality
<span class="kw">FROM</span> "Telemetry"
<span class="kw">WHERE</span> time &gt;= <span class="str">'2026-10-04T09:00:00Z'</span>
  <span class="kw">AND</span> DeviceID <span class="kw">IN</span> (<span class="str">'Pump-01'</span>, <span class="str">'Pump-02'</span>)
<span class="kw">ORDER BY</span> time <span class="kw">DESC</span>
<span class="kw">LIMIT</span> 100;</div></div><div class="editor-footer"><span>SonnetDB SQL · UTF-8 · 内存草稿</span><span><kbd>Ctrl Enter</kbd> 执行选中或当前语句</span></div>`.replace(/(<div class="sql-editor"[^>]*>)[\s\S]*?(<\/div><\/div><div class="editor-footer">)/, (match, open, close) => draft === undefined ? match : open + esc(draft) + close);
}
function chart() {
  return `<div class="chart-plane"><svg viewBox="0 0 720 150" preserveAspectRatio="none" role="img" aria-label="示例设备温度趋势"><g stroke="#e5ebf2" stroke-width="1"><path d="M0 30H720 M0 70H720 M0 110H720 M0 149H720"/></g><path d="M0 95L30 90 60 96 90 73 120 82 150 68 180 71 210 48 240 54 270 58 300 43 330 48 360 33 390 43 420 50 450 30 480 36 510 20 540 37 570 41 600 25 630 32 660 14 690 24 720 18" fill="none" stroke="#0f6cbd" stroke-width="2"/><path d="M0 128L30 121 60 128 90 118 120 127 150 119 180 122 210 106 240 118 270 111 300 103 330 111 360 96 390 107 420 99 450 105 480 95 510 100 540 90 570 103 600 99 630 84 660 98 690 90 720 88" fill="none" stroke="#7f9db7" stroke-width="1.6"/></svg><div class="chart-labels"><span>09:00</span><span>09:15</span><span>09:30</span><span>09:45</span></div></div>`;
}
function emptyContent() { const e = current().empty || { title:'当前范围内没有数据', body:'检查对象、时间范围或过滤条件，再尝试读取。', action:'重置筛选' }; return `<div class="empty-state">${icon('database')}<h3>${esc(e.title)}</h3><p>${esc(e.body)}</p>${button(e.action,'reset-filter',true)}</div>`; }
function fields(items) { return `<div class="form-grid">${items.map((f) => { const key = draftKey(f.label); const value = state.drafts[key] ?? f.value; return `<label class="field">${esc(f.label)}${f.kind === 'textarea' ? `<textarea data-draft="${esc(key)}">${esc(value)}</textarea>` : f.kind === 'select' ? `<select data-draft="${esc(key)}"><option>${esc(value)}</option></select>` : `<input data-draft="${esc(key)}" value="${esc(value)}" ${f.kind === 'secret' ? 'type="password"' : ''}>`}<small>${esc(f.help || '修改后进入预览，不会自动执行。')}</small></label>`; }).join('')}</div>`; }
function detail(items) { return `<dl class="detail-list">${items.map((f) => `<div><dt>${esc(f.label)}</dt><dd>${esc(f.value)}</dd></div>`).join('')}</dl>`; }
function overview(page) {
  return `<div class="metric-strip"><div class="metric"><span>实例状态</span><strong>健康</strong><small>示例快照 · 09:42:00</small></div><div class="metric"><span>数据库</span><strong>2</strong><small>factory · analytics</small></div><div class="metric"><span>当前连接</span><strong>24</strong><small>含 2 个管理会话</small></div><div class="metric"><span>写入速率</span><strong>1,248</strong><small>points / s</small></div></div><div class="section-head"><div><h3>写入与查询</h3><small>最近 45 分钟 · 示例趋势</small></div><button class="text-button" data-action="refresh">刷新</button></div>${chart()}<div class="section-head"><h3>${esc(page.tabs[state.task] || '实例资源')}</h3><button class="text-button" data-action="connections">管理连接</button></div>${table(page.columns, page.rows)}`;
}
function modelContent(page) {
  const tab = page.tabs[state.task];
  const queryLike = /查询|SQL|PGQ/.test(tab);
  const transfer = /导入|上传|Multipart|Import/.test(tab);
  const maintenance = /治理|维护|Retention|配置|参数|策略/.test(tab);
  const editing = /编辑|设计器|Validator|Restricted/.test(tab);
  if (queryLike) return sqlEditor();
  if (transfer) return `<div class="form-plane"><h3>${esc(tab)}</h3><p>先选择文件并检查范围，再预览变更。</p>${fields([{label:'文件',value:page.id === 'bucket' ? 'inspection-images.zip' : 'sample-data.jsonl',help:'原型使用示例文件名，未读取本地文件。'},{label:'目标',value:page.objectName},{label:'批次大小',value:'100',help:'停止仅阻止后续批次；已提交批次保留。'},{label:'冲突处理',value:'拒绝并生成报告',kind:'select'}])}<div class="section-head">${button('检查文件与映射','import-check',true)}${button('查看任务进度','tasks')}</div><div class="notice">${icon('info')} ${page.id === 'bucket' ? 'Multipart 恢复需依据服务端会话与分片清单。' : '本地校验与服务端预检分开显示；持久续传能力按模型提供。'}</div></div>`;
  if (maintenance || editing || /Schema|索引/.test(tab)) return `<div class="form-plane"><h3>${esc(tab)}</h3>${detail(page.inspector || [])}${fields([{label:editing ? '目标字段 / 标签' : '目标对象',value:page.objectName},{label:editing ? '拟定变更' : '维护范围',value:editing ? '在此编辑草稿' : '仅当前对象'}])}<div class="section-head">${button('预览变更','approve',true)}${button('查看审计','audit')}</div><div class="notice">${icon('lock')} 所有写入与维护先确认连接、对象和影响范围。</div></div>`;
  if (page.id === 'graph' && /Canvas|画布|概览/.test(tab)) return `<div class="content-toolbar"><div class="actions"><label>顶点预算 <select><option>100</option><option>250</option></select></label><label>跳数 <select><option>1</option><option>2</option></select></label></div><div class="actions">${button('读取快照','run',true)}${button('SQL / PGQ','new-query')}</div></div><div class="graph-canvas"><svg viewBox="0 0 680 320" role="img" aria-label="示例有界属性图：设备、站点、产线和阀门"><g stroke="#c5d2e0" stroke-width="1.5"><path d="M340 155L120 90 M120 90L535 65 M340 155L530 250"/></g><g class="node"><circle cx="340" cy="155" r="32"/><text x="340" y="158" text-anchor="middle">Pump-01</text><circle cx="120" cy="90" r="26"/><text x="120" y="94" text-anchor="middle">Station-01</text><circle cx="535" cy="65" r="26"/><text x="535" y="69" text-anchor="middle">Line-A</text><circle cx="530" cy="250" r="26"/><text x="530" y="254" text-anchor="middle">Valve-01</text></g><g fill="#64748b" font-size="11"><text x="190" y="110">INSTALLED_AT</text><text x="310" y="62">PART_OF</text><text x="420" y="215">CONNECTED_TO</text></g></svg><div class="graph-legend"><span>4 顶点</span><span>3 边</span><span>快照 #42</span><span>预算内 · Graph Beta</span></div></div>`;
  if (page.id === 'fulltext' && /检索|Search/.test(tab)) return `<div class="content-toolbar"><div class="actions"><input aria-label="全文检索内容" value="泵站 振动告警"><select aria-label="匹配方式"><option>All / 全部词</option><option>Phrase / 短语</option></select></div>${button('检索','run',true,'search')}</div><div class="search-hits">${page.rows.slice(0,4).map((row,i)=>`<article class="search-hit"><h3>${esc(row[0])}</h3><p>设备运行记录：<mark>泵站</mark>异常与<mark>振动告警</mark>的处置、检查和恢复过程。检查器可查看原始文档与索引信息。</p><small>BM25 ${(8.73-i*.92).toFixed(2)} · 示例命中 · docs.search</small></article>`).join('')}</div>`;
  const prefix = page.id === 'kv' ? 'device:' : page.id === 'bucket' ? 'inspection/' : '';
  return `<div class="content-toolbar"><div class="actions"><input aria-label="过滤当前数据" placeholder="${prefix ? '前缀' : '过滤字段或名称'}" value="${prefix}"><select aria-label="当前范围"><option>${page.id === 'mq' ? '从 offset 1024' : '当前范围'}</option></select></div><div class="actions">${button(page.id === 'vector' ? 'Top-K 检索' : '读取','run',true)}${button('检查器','toggle-inspector')}</div></div>${page.id === 'measurement' && /监控|图表/.test(tab) ? chart() : ''}${table(page.columns, page.rows)}<div class="editor-footer"><span>${page.scope === 'instance' ? '实例资源 · 持久化不属于单库备份' : '数据按当前过滤上下文读取'}</span><button class="text-button" data-action="export">导出当前已加载数据</button></div>`;
}
function taskContent(page, task) {
  const header = `<div class="form-plane task-heading"><h3>${esc(task.title)}</h3><p>${esc(task.description)}</p>${task.fields.length ? fields(task.fields) : ''}<div class="section-head">${button(task.action, 'task-preview', true)}${task.view === 'import' ? button('共享导入流程', 'import-check') : ''}</div>${task.status === 'planned' ? `<div class="notice warning">${icon('info')} 规划能力 · 仅评审输入与流程，不能调用或执行。</div>` : ''}</div>`;
  if (task.view === 'chart') {
    const numeric = task.rows.map(row => Number.parseFloat(row[1]));
    const usable = numeric.every(Number.isFinite) && numeric.length > 1;
    const low = Math.min(...numeric), high = Math.max(...numeric);
    const points = usable ? numeric.map((value, index) => `${40 + index * 560 / (numeric.length - 1)},${140 - (value - low) * 100 / Math.max(1, high - low)}`).join(' ') : '';
    return header + (usable ? `<div class="chart-plane"><svg viewBox="0 0 640 180" role="img" aria-label="${esc(task.title)}静态样例趋势"><path d="M40 20V150H600" fill="none" stroke="#c5d2e0"/><polyline points="${points}" fill="none" stroke="#0f6cbd" stroke-width="2"/></svg><div class="chart-labels">${task.rows.map(row => `<span>${esc(row[0])}</span>`).join('')}</div><p>示例序列：${esc(task.columns[1])}；原始值见下表。</p></div>` : '<div class="notice">数据尚未提供，当前仅评审图表配置。</div>') + table(task.columns, task.rows);
  }
  if (task.view === 'graph') return header + `<div class="notice">${icon('info')} ER 关系示例按下方来源逐条核对；不把同名列自动推断为外键。</div>` + table(task.columns, task.rows);
  return header + (task.columns.length ? table(task.columns, task.rows) : '');
}
function content(page) {
  if (state.mode === 'empty') return emptyContent();
  if (state.mode === 'loading') return '<div class="skeleton" role="status" aria-label="正在加载当前对象"><div class="skeleton-line"></div><div class="skeleton-line"></div><div class="skeleton-line"></div><div class="skeleton-line"></div><p>正在读取当前对象，其他工作区保持可用。</p></div>';
  if (state.mode === 'permission') return '<div class="empty-state">' + icon('lock') + '<h3>没有读取此范围的权限</h3><p>目标上下文已保留，数据内容不会显示。请申请读取权限或切换连接。</p>' + button('查看所需权限','permission-info',true) + '</div>';
  if (state.mode === 'error') return '<div class="empty-state">' + icon('info') + '<h3>当前对象读取失败</h3><p>端点未返回可用结果。输入和其他资源分组已保留，可以单独重试。</p>' + button('重试当前对象','retry',true) + '</div>';
  const task = window.M47_TASK_DETAILS?.[page.id]?.[state.task];
  if (task && (state.task > 0 || task.status === 'planned')) return taskContent(page, task);
  if (isModel(page)) return modelContent(page);
  if (page.type === 'sql') return sqlEditor();
  if (page.type === 'notebook') return `<div class="notebook-cell"><div class="notebook-comment"><h3>设备温度分析</h3><p>保存查询与说明；恢复工作区只恢复输入，不自动执行。</p></div>${sqlEditor()}</div><div class="form-plane"><div class="notice">${icon('info')} Notebook 为规划中的工作区资产；这里仅演示其页面结构。</div></div>`;
  if (page.type === 'overview') return overview(page);
  if (page.type === 'ai-connect') return `<div class="form-plane"><h3>连接只读 MCP 工具</h3><p>选择宿主、数据库与连接方式后检查配置。</p>${fields(page.fields)}<div class="code-block" style="margin-top:20px"><pre>{
  "mcpServers": {
    "sonnetdb": {
      "url": "https://db.example.test/mcp/factory",
      "headers": { "Authorization": "Bearer &lt;运行时凭据&gt;" }
    }
  }
}</pre></div><div class="section-head">${button('复制脱敏配置','copy-config',true)}${button('连接自检','mcp-test')}</div><div class="notice warning">${icon('info')} 配置生成与 stdio bridge 属于规划。复制只含占位凭据；自检结果为原型演示。</div></div>`;
  if (page.type === 'form') return `<div class="form-plane"><div class="form-group"><h3>${esc(page.tabs[state.task])}</h3><p>${esc(page.intro)}</p>${fields(page.fields)}</div><div class="actions">${button('预览设置','approve',true)}${button('恢复当前值','reset-filter')}</div>${page.columns.length ? `<div class="section-head"><h3>当前配置</h3></div>${table(page.columns,page.rows)}` : ''}</div>`;
  if (page.type === 'connection') return `<div class="form-plane"><h3>连接库</h3><p>连接配置与凭据分开保存，切换后不自动执行查询或写入。</p>${connectionList()}<div class="section-head">${button('添加远程连接','add-connection',true)}${button('检查健康','connection-test')}</div>${state.host === 'studio' ? `<div class="form-group"><h3>托管本地 Server</h3>${detail([{label:'data root',value:'D:\\SonnetDB\\data（示例）'},{label:'宿主状态',value:'示例快照 · 已启动'},{label:'WebView2',value:'安装证据待验证'}])}${button('停止本地 Server','approve')}</div>` : ''}</div>`;
  return `<div class="content-toolbar"><div class="actions"><input placeholder="筛选名称或状态" aria-label="筛选列表"><select aria-label="过滤范围"><option>${scopeName(page.scope)}</option></select></div><div class="actions">${button(page.primary || '刷新','run',true)}${button('导出','export')}</div></div>${table(page.columns,page.rows)}`;
}
function renderState() {
  const descriptions = {
    offline:'连接已离线 · 保留最后的示例快照与草稿，写操作暂停。重新连接不会自动重放。',
    readonly:state.host === 'vscode' ? '开发者宿主只读预览 · 复杂治理交由 Web Admin / Studio。' : '当前上下文只读 · 读取和导出可用，写入需要相应权限。',
    error:'局部读取失败 · 此对象请求失败，其他分组与工作区不受影响。',
    partial:'局部失败 · 向量分组读取失败，其他模型仍可读取；可单独重试失败分组。',
    permission:'权限不足 · 数据载荷已隐藏，保留连接和对象上下文。',
    truncated:'结果已截断 · 达到 100 行 / 1 MiB 预算；导出只含当前已加载数据。',
    timeout:'请求超时 · 查询可取消；写请求结果需要核对，不提供盲目重试。',
  };
  let text = descriptions[state.mode] || '';
  if (!text && (state.host === 'vscode' || innerWidth < 800)) text = state.host === 'vscode' ? '开发者预览 · 写入和治理转交 Web Admin / Studio。' : '窄屏巡检 · 浏览与查询可用，编辑和批量治理请使用桌面。';
  $('stateBanner').hidden = !text; $('stateBanner').classList.toggle('error',['error','permission'].includes(state.mode));
  $('stateBanner').innerHTML = text ? `${icon(['permission','readonly'].includes(state.mode) ? 'lock' : 'info')}<span>${esc(text)}</span>${['offline','error','partial'].includes(state.mode) ? '<button class="text-button" data-action="retry">检查并重试</button>' : ''}` : '';
  $('readonlyTag').hidden = !readOnly();
  $('connectionDot').classList.toggle('offline',state.mode === 'offline');
  $('footerDot').classList.toggle('offline',state.mode === 'offline');
}
function resultData() { const p=current(), task=currentTask(); return state.task > 0 && task ? {columns:task.columns,rows:task.rows} : p.type === 'sql' ? {columns:sqlColumns,rows:sqlRows} : {columns:p.columns || [],rows:p.rows || []}; }
function renderResults() {
  const tabs=current().type === 'sql' && state.task === 0 ? ['表格','JSON','图表','EXPLAIN'] : ['表格','JSON'];
  if (!tabs.includes(state.resultView)) state.resultView = '表格';
  $('resultTabs').innerHTML=tabs.map((t)=>`<button class="result-tab${state.resultView===t?' active':''}" data-result="${t}">${t}</button>`).join('');
  const data=resultData();
  const hideData=['permission','error'].includes(state.mode);
  $('resultBody').innerHTML = hideData ? '<div class="payload-box"><p>当前没有可显示的结果载荷。</p></div>' : state.mode === 'empty' ? '<div class="payload-box"><p>查询已完成 · 0 行。</p></div>' : state.resultView==='JSON' ? `<div class="payload-box"><pre>${esc(JSON.stringify(data.rows.slice(0,3).map((row)=>Object.fromEntries(data.columns.map((c,i)=>[c,row[i]]))),null,2))}</pre></div>` : state.resultView==='图表' ? chart() : state.resultView==='EXPLAIN' ? `<div class="payload-box"><h3>计划预览</h3><pre>Limit (100)
  └─ Sort · time DESC
     └─ Measurement scan · Telemetry
        └─ Filter · time / DeviceID

示例计划结构，不代表本次 Server 执行结果。
具体预算、回退与阻塞信息由真实 EXPLAIN 提供。</pre></div>` : table(data.columns,data.rows,true);
  $('resultSummary').textContent = hideData ? '无可用结果' : `${state.mode==='empty'?0:data.rows.length} 示例行 · 12 ms${state.mode==='truncated'?' · 截断':''}`;
  $('resultFooter').textContent=state.mode==='offline'?'离线快照 · 示例时间 09:42:00':state.mode==='truncated'?'预算截断 · 不提供虚构 continuation':'当前已加载结果 · 示例快照';
}
function renderInspector() {
  const p=current(); const data=resultData(); const row=data.rows[state.selectedRow] || [];
  document.querySelectorAll('.inspector-tabs button').forEach((b)=>b.classList.toggle('selected',b.dataset.action===`inspector-${state.inspectorView==='info'?'info':'payload'}`));
  if (state.mode==='permission') { $('inspectorBody').innerHTML='<div class="notice">'+icon('lock')+' 当前没有读取权限，检查器不显示数据。</div>'; return; }
  const facts=state.task > 0 ? currentTask()?.inspector || [] : p.type==='sql' ? [{label:'类型',value:'Measurement 结果行'},{label:'数据库',value:'factory'},{label:'Measurement',value:'Telemetry'},{label:'读取时间',value:'09:42:00 · 示例快照'}] : p.inspector || [];
  const values= data.columns.map((label,i)=>({label,value:row[i]??'—'}));
  $('inspectorBody').innerHTML=`<div class="inspector-title"><strong>${esc(p.objectName || p.title)}</strong><small>${esc(scopeName(p.scope))} · ${p.id==='graph'?'Graph Beta':'当前选择'}</small></div>${state.inspectorView==='payload'?`<pre>${esc(JSON.stringify(Object.fromEntries(values.map((v)=>[v.label,v.value])),null,2))}</pre>`:`<h3>上下文</h3>${detail(facts)}<h3 style="margin-top:24px">${row.length?'选中记录':'对象详情'}</h3>${detail(values.slice(0,8))}`}<div class="notice">${icon('info')} 示例值只用于评审布局；实际数据、权限与刷新时间由宿主提供。</div>`;
}
function render() {
  const p=current(); renderRail(); renderSidebar(); renderTabs();
  $('breadcrumb').innerHTML=`${esc(state.connection)}<span>/</span>${p.scope==='instance'?'实例资源':'factory'}<span>/</span>${esc(p.objectName || p.title)}`;
  $('objectPath').textContent=`${scopeName(p.scope)} / ${p.group || catalog.sections.find((s)=>s.id===state.section).label}`;
  $('pageTitle').textContent=state.mode==='long'?(p.objectName||p.title)+'_production_line_02_2026_archive_extended':p.objectName||p.title;
  $('pageIntro').textContent=p.intro;
  $('betaTag').hidden=p.id!=='graph';
  $('plannedTag').hidden=p.status!=='planned' && window.M47_TASK_DETAILS?.[p.id]?.[state.task]?.status!=='planned';
  const task = currentTask();
  const taskSpecific = state.task > 0 || p.status === 'planned' || task?.status === 'planned';
  $('pageActions').innerHTML=`<button class="icon-button" data-action="toggle-inspector" aria-label="切换检查器">${icon('panel')}</button>${isModel(p)&&!['kv','mq','bucket'].includes(p.id)?button('SQL','new-query'):button('历史','history')}${button(taskSpecific ? task.action : p.primary|| (p.type==='sql'?'执行':'刷新'),taskSpecific ? 'task-preview' : primaryAction(p),true,p.type==='sql'?'play':'')}`;
  $('content').innerHTML=content(p); renderState(); renderResults(); renderInspector();
  $('statusScope').textContent=scopeName(p.scope); $('statusContext').textContent=p.title;
  $('hostName').textContent={web:'Web Admin',studio:'Studio',vscode:'VS Code'}[state.host];
  $('connectionName').textContent=state.connection; $('statusConnection').textContent=state.connection;
  $('shell').classList.toggle('theme-vscode',state.host==='vscode');
  document.querySelectorAll('[data-action="approve"], [data-action="import-check"], [data-action="new-resource"]').forEach((b)=>{b.disabled=blocked();b.title=blocked()?'当前宿主、视口或权限状态不支持写操作':'';});
  document.querySelectorAll('[data-action="run"]').forEach((b)=>{b.disabled=['offline','permission','loading'].includes(state.mode);});
  icons();
}
let toastTimer;
function toast(message) { clearTimeout(toastTimer); $('toast').textContent=message; $('toast').hidden=false; toastTimer=setTimeout(()=>{$('toast').hidden=true;},4500); }
function modal(title,subtitle,body,footer,review=false) { $('modal').classList.toggle('review-dialog',review); $('modal').classList.toggle('approval-dialog',title.includes('删除')||title.includes('暂存')); $('modalTitle').textContent=title; $('modalSubtitle').textContent=subtitle; $('modalBody').innerHTML=body; $('modalFooter').innerHTML=footer||button('关闭','close-dialog'); if (!$('modal').open) $('modal').showModal(); icons($('modal')); }
function closeModal(){ $('modal').close(); }
function openDrawer(title,body){state.drawerFocus=document.activeElement;$('drawerTitle').textContent=title;$('drawerBody').innerHTML=body;$('drawer').hidden=false;$('drawer').querySelector('button').focus();icons($('drawer'));}
function closeDrawer(){ $('drawer').hidden=true;state.drawerFocus?.focus(); }
function connectionList(){return `<div class="connection-list" style="margin-top:18px"><button class="connection-item ${state.connection==='Factory / Local'?'active':''}" data-connect="Factory / Local"><div>${icon('database')}</div><div><strong>Factory / Local</strong><small>127.0.0.1:5080 · 默认库 factory · 示例</small></div><span class="status-dot"></span></button><button class="connection-item ${state.connection==='Analytics / Remote'?'active':''}" data-connect="Analytics / Remote"><div>${icon('database')}</div><div><strong>Analytics / Remote</strong><small>db.example.test · 默认库 analytics · 示例</small></div><span class="status-dot"></span></button></div>`;}
function connections(){modal('连接库','选择上下文；切换仅恢复工作区，不自动执行操作。',connectionList()+`<div class="notice" style="margin-top:18px">${icon('lock')} 凭据独立存储，原型未访问任何端点。</div>`,button('添加远程连接','add-connection')+button('关闭','close-dialog'));}
function approval(danger=false){
  if(blocked()){toast('当前为只读或离线状态，请先检查连接与权限。');return;}
  const p=current();
  modal(danger?'确认删除对象':'预览暂存操作','原型确认只生成本地演示记录，不写入数据库。',`<div class="dialog-steps"><span>1 草稿</span><span class="active">2 范围与预览</span><span>3 确认</span></div><div class="notice ${danger?'warning':''}">${icon('info')} ${danger?'删除不可由界面撤销。必须核对目标原名与备份范围。':'确认目标和影响范围后再提交。实际影响数量须由真实预检提供。'}</div>${detail([{label:'连接',value:state.connection},{label:'范围',value:scopeName(p.scope)},{label:'对象',value:p.objectName || p.title},{label:'拟定动作',value:danger?'删除对象':p.primary || '保存草稿'},{label:'影响数量',value:'尚未估计 · 等待服务端预检'}])}<div class="code-block"><pre>${esc(danger?`删除目标：${p.objectName||p.title}\n实际指令由模型适配器生成，此处仅展示影响预览。`:'-- 示例暂存草稿\n-- 正式执行由当前模型的真实预检与权限决定')}</pre></div>${danger?`<label class="field">输入对象原名 <input id="confirmationName" autocomplete="off" placeholder="${esc(p.objectName||p.title)}"></label>`:''}<label class="checkbox-field"><input id="riskCheck" type="checkbox">我已核对目标与影响范围，了解此处仅演示确认流程。</label>`,button('返回编辑','close-dialog')+button('预检','dry-run')+`<button class="button ${danger?'danger':'primary'}" id="commitDraft" data-action="commit-draft" data-danger="${danger}" disabled>${danger?'确认删除预览':'确认演示操作'}</button>`);
}
const modes=[['normal','正常'],['empty','空数据'],['loading','加载中'],['partial','局部失败'],['readonly','只读'],['offline','离线'],['permission','无权限'],['error','错误'],['timeout','超时'],['truncated','结果截断'],['long','长内容']];
function review(){modal('设计目录','先审外轮廓与导航，再审模型任务和页面状态；所有数值均为示例。',`<div class="review-toolbar"><label class="field">宿主外壳<select id="reviewHost">${[['web','Web Admin'],['studio','Studio'],['vscode','VS Code 开发者预览']].map(([id,label])=>`<option value="${id}" ${id===state.host?'selected':''}>${label}</option>`).join('')}</select></label><label class="field">全页面状态<select id="reviewState">${modes.map(([id,label])=>`<option value="${id}" ${id===state.mode?'selected':''}>${label}</option>`).join('')}</select></label><label class="field">共享交互<select id="reviewDialog"><option value="">选择对话框或抽屉</option>${[['connections','连接库'],['add-connection','添加连接'],['new-resource','创建资源'],['approve','写审批'],['delete','危险删除'],['import-check','导入预检'],['tasks','导入进度 / 停止后续批次'],['history','历史恢复'],['command','命令中心'],['permission-info','权限说明'],['unknown-outcome','执行结果待核对']].map(([id,label])=>`<option value="${id}">${label}</option>`).join('')}</select></label></div><div class="review-grid">${catalog.sections.map((s)=>`<section class="review-section"><h3>${esc(s.label)}</h3>${s.pages.map((p)=>`<button data-review-page="${p.id}">${esc(p.title)}<small>${p.status==='planned'?'规划':p.status==='extension'?'延伸':'已有'}</small></button>`).join('')}</section>`).join('')}<section class="review-section"><h3>九模型工作台</h3>${catalog.models.map((p)=>`<button data-review-page="${p.id}">${esc(p.title)}<small>${p.id==='graph'?'Beta':p.scope==='instance'?'实例':'数据库'}</small></button>`).join('')}</section></div>`,button('查看当前页面','apply-review',true),true);}
function palette(){modal('命令中心','搜索页面、资源与命令；危险动作只打开预览。','<input class="palette-input" id="commandInput" aria-label="搜索命令" placeholder="搜索 SQL、资源、连接或设置"><div class="palette-results" id="commandResults"></div>',button('关闭','close-dialog'));filterPalette('');$('commandInput').focus();}
function filterPalette(term){const candidates=[...allPages(),...catalog.models].filter((p)=>`${p.title} ${p.objectName||''}`.toLowerCase().includes(term.toLowerCase())).slice(0,8);$('commandResults').innerHTML=candidates.map((p)=>`<button class="palette-result" data-review-page="${p.id}"><span>${esc(p.objectName||p.title)}</span><small>${scopeName(p.scope)}</small></button>`).join('')||'<p>没有匹配的页面或资源。</p>';}
function tasks(){openDrawer('任务进度',`<div class="notice">${icon('info')} 本地演示任务，未上传文件或写入服务。</div><div class="history-item"><div><strong>导入 sample-data.jsonl</strong><span class="small-tag" id="taskState">运行中</span></div><small>${esc(scopeName(current().scope))} · ${esc(current().objectName||current().title)}</small><div class="import-progress"><span id="taskBar"></span></div><div class="section-head"><span id="taskCounts">已提交 200 · 当前批次 100 · 剩余 200</span></div>${button('停止后续批次','stop-task')}${button('查看报告','task-report')}</div><div class="notice warning">${icon('info')} 已提交批次不会回滚；对象 Multipart 续传需要重新核对服务端会话。关系导入需根据报告重新建立任务。</div>`);}
function act(action){
  switch(action){
    case 'review':review();break;
    case 'apply-review':state.host=$('reviewHost').value;state.mode=$('reviewState').value;closeModal();render();break;
    case 'close-dialog':closeModal();break;
    case 'close-drawer':closeDrawer();break;
    case 'command':palette();break;
    case 'connections':connections();break;
    case 'toggle-sidebar':$('shell').classList.toggle('sidebar-hidden');break;
    case 'toggle-inspector':$('shell').classList.toggle('inspector-hidden');break;
    case 'toggle-results':$('shell').classList.toggle('results-hidden');break;
    case 'inspector-info':state.inspectorView='info';renderInspector();break;
    case 'inspector-payload':state.inspectorView='payload';renderInspector();break;
    case 'refresh':case 'retry':case 'reset-filter':state.mode='normal';render();toast('已恢复正常示例状态；正式实现将重新读取当前范围。');break;
    case 'run':if(currentTask()?.status==='planned'){act('task-preview');break;}state.resultView='表格';if(current().type==='sql'&&state.task===0)$('shell').classList.remove('results-hidden');renderResults();state.history.unshift({title:'读取 '+(current().objectName||current().title),status:'成功 · 演示',time:'刚刚',pageId:state.pageId});toast('原型结果已更新，未访问数据库。');break;
    case 'task-preview':{const task=currentTask();modal(task.title+' · 设计预览',task.status==='planned'?'规划能力尚未实现，当前不能调用。':'核对本页输入、目标和能力后，正式流程才进入模型适配器。',`<p>${esc(task.description)}</p>${detail([{label:'目标',value:current().objectName||current().title},{label:'范围',value:scopeName(current().scope)},{label:'能力状态',value:{existing:'现有入口，需真实接线验证',extension:'现有能力的设计延伸',planned:'规划，禁止执行'}[task.status]},...task.fields.map(f=>({label:f.label,value:state.drafts[draftKey(f.label)]??f.value}))])}<div class="notice">${icon('info')} 此处仅检查静态设计，不生成真实审批或请求。</div>`,button('返回编辑','close-dialog'));break;}
    case 'format':toast('SQL 格式化布局已展示。');break;
    case 'explain':state.resultView='EXPLAIN';$('shell').classList.remove('results-hidden');renderResults();break;
    case 'new-query':navigate('sql');break;
    case 'restore-context':navigate('sql');toast('仅恢复示例输入与上下文，没有执行查询。');break;
    case 'approve':approval();break;
    case 'delete':approval(true);break;
    case 'dry-run':toast('预检演示：真实影响数量、执行权限与预算等待服务端提供。');break;
    case 'commit-draft':state.history.unshift({title:'暂存操作演示 · '+(current().objectName||current().title),status:'已确认 · 本地演示',time:'刚刚',pageId:state.pageId});closeModal();$('notificationDot').hidden=false;toast('确认流程已完成。正式实现须由服务端执行与审计。');break;
    case 'add-connection':modal('添加远程连接','配置、健康检查和认证状态分开显示。',fields([{label:'连接名称',value:'Analytics / Remote'},{label:'端点 URL',value:'https://db.example.test'},{label:'默认数据库',value:'analytics'},{label:'凭据来源',value:'宿主安全存储',kind:'select'}])+`<div class="notice" id="connectionTestResult" style="margin-top:18px">${icon('info')} 尚未检查 · 原型不会发送请求。</div>`,button('取消','close-dialog')+button('测试连接','connection-test')+button('保存示例配置','save-connection',true));break;
    case 'connection-test':if($('connectionTestResult'))$('connectionTestResult').textContent='健康检查演示完成；认证和对象读取权限仍需分别核对。';toast('示例健康检查；未访问网络。');break;
    case 'save-connection':closeModal();toast('示例配置已查看，未写入连接库。');break;
    case 'new-resource':modal('创建资源','确认当前作用域，字段错误保留在表单内。',fields([{label:'目标连接',value:state.connection},{label:'资源类型',value:current().id==='mq'?'消息 Topic':'数据库对象',kind:'select'},{label:'对象原名',value:'DeviceMetrics_New'},{label:'所属范围',value:scopeName(current().scope)}])+`<div class="notice" style="margin-top:18px">${icon('info')} 原始名称拼写保留；重复名称与权限由真实创建预检确定。</div>`,button('取消','close-dialog')+button('预览创建','approve'));break;
    case 'import-check':modal('导入预检','本地格式校验与服务端预检分开显示。',`<div class="dialog-steps"><span>1 文件</span><span>2 映射</span><span class="active">3 校验</span><span>4 审批</span></div>${detail([{label:'示例文件',value:'sample-data.jsonl'},{label:'目标',value:current().objectName||current().title},{label:'本地校验',value:'示例 500 行 · 结构有效'},{label:'服务端预检',value:'尚未执行'},{label:'批次',value:'100 行 / 批'}])}<div class="notice warning">${icon('info')} 停止后续批次不会回滚已提交批次；续传仅用于有真实 checkpoint 或服务端会话的模型。</div>`,button('返回映射','close-dialog')+button('进入写审批','approve',true));break;
    case 'tasks':tasks();break;
    case 'stop-task':$('taskState').textContent='已停止后续批次';$('taskCounts').textContent='已提交 300 · 未提交 200 · 失败 0';$('taskBar').style.width='60%';toast('当前示例批次结束后已停止，已提交数据保留。');break;
    case 'task-report':modal('导入报告','重新导入需要重新核对目标和去重策略。',detail([{label:'提交',value:'300 行'},{label:'未提交',value:'200 行'},{label:'失败',value:'0 行'},{label:'后续操作',value:'从报告重新建立导入，不宣称无重复持久续传'}]),button('关闭','close-dialog'));break;
    case 'history':openDrawer('本机工作历史',`<div class="notice">${icon('info')} 恢复对象、筛选与输入；不会自动重放操作。服务端审计另列在治理。</div><label class="resource-search" style="margin:18px 0"><input placeholder="筛选模型、状态或连接" aria-label="筛选本机历史"></label>${[...state.history.slice(0,5),{title:'设备温度查询',status:'成功 · 示例',time:'09:42',pageId:'sql'},{title:'浏览实例 Topic',status:'成功 · 示例',time:'09:38',pageId:'mq'},{title:'关系表编辑草稿',status:'草稿 · 示例',time:'09:32',pageId:'table'}].map((h)=>`<article class="history-item"><div><strong>${esc(h.title)}</strong><span class="small-tag">${esc(h.status)}</span></div><small>${esc(state.connection)} · ${esc(h.time)}</small><button class="text-button" data-restore="${h.pageId}">恢复上下文</button></article>`).join('')}`);break;
    case 'notifications':openDrawer('通知',`<div class="notice">${icon('info')} 示例通知不代表真实服务告警。</div><article class="history-item"><strong>对象索引预览已生成</strong><small>需要确认目标和影响范围</small><button class="text-button" data-action="approve">查看预览</button></article><article class="history-item"><strong>发布证据待补</strong><small>Studio 安装与 Extension Host 真实旅程独立验收。</small></article>`);break;
    case 'audit':openDrawer('服务端审计',`<div class="notice">${icon('lock')} 审计与本机操作历史分开。此处仅展示信息结构。</div>${detail([{label:'连接',value:state.connection},{label:'对象',value:current().objectName||current().title},{label:'请求标识',value:'demo-request-0042'},{label:'结果',value:'示例 · 未执行'}])}`);break;
    case 'permission-info':modal('权限与宿主能力','客户端显示的能力不能作为服务端授权。',detail([{label:'资源',value:current().objectName||current().title},{label:'读取权限',value:'未知 / 未取得快照'},{label:'写入权限',value:'需服务端核对'},{label:'当前宿主',value:state.host},{label:'处理方法',value:'切换有权限的连接，或申请对应对象范围授权'}]),button('检查连接','connections')+button('关闭','close-dialog'));break;
    case 'unknown-outcome':modal('执行结果待核对','写请求超时后不能判断是否已提交。',`<div class="notice warning">${icon('info')} 保留原请求标识并检查服务端状态。不要自动重试，也不要把超时当作回滚。</div>${detail([{label:'对象',value:current().objectName||current().title},{label:'请求标识',value:'demo-request-0042'},{label:'当前结果',value:'未知 · 示例流程'}])}`,button('返回工作区','close-dialog')+button('查看审计','audit'));break;
    case 'mcp-test':modal('MCP 连接自检','原型检查器未向任何 MCP 服务发请求。',detail([{label:'传输',value:'Streamable HTTP（已有）；stdio bridge（规划）'},{label:'health',value:'待实际核对'},{label:'tools/list',value:'待实际核对'},{label:'工具权限',value:'默认只读，实际按服务端合同'},{label:'数据外发',value:'依宿主与 Provider 设置明确确认'}]),button('关闭','close-dialog'));break;
    case 'copy-config':toast('脱敏配置结构已展示，凭据使用运行时占位符。');break;
    case 'copy-result':toast('复制范围：当前已加载的示例结果。');break;
    case 'export':modal('导出当前结果','导出范围固定为当前已加载的数据，不宣称全库导出。',fields([{label:'格式',value:'JSON / CSV',kind:'select'},{label:'范围',value:'当前已加载示例行'},{label:'目标',value:current().objectName||current().title},{label:'截断',value:state.mode==='truncated'?'已截断，导出不包含省略行':'当前示例范围'}]),button('取消','close-dialog')+button('确认导出预览','close-dialog',true));break;
    case 'previous-page':case 'next-page':toast('此结果仅含当前示例快照；真实 continuation 由模型端点能力决定。');break;
    case 'account':modal('当前账户','示例身份，未接入认证服务。',detail([{label:'账户',value:'admin（示例）'},{label:'连接',value:state.connection},{label:'权限',value:'以服务端最新快照为准'}]),button('查看权限','permission-info')+button('关闭','close-dialog'));break;
  }
}
document.addEventListener('click',(event)=>{
  const close=event.target.closest('[data-close-tab]');
  if(close){const id=close.dataset.closeTab;state.opened=state.opened.filter((p)=>p!==id);if(!state.opened.length)state.opened=['sql'];if(state.pageId===id)navigate(state.opened.at(-1),false);else renderTabs();return;}
  const page=event.target.closest('[data-page],[data-review-page],[data-restore]');
  if(page){const id=page.dataset.page||page.dataset.reviewPage||page.dataset.restore;if(page.dataset.reviewPage){if($('reviewHost'))state.host=$('reviewHost').value;if($('reviewState'))state.mode=$('reviewState').value;closeModal();}if(page.dataset.restore)closeDrawer();navigate(id);return;}
  const section=event.target.closest('[data-section]');if(section){const s=catalog.sections.find((p)=>p.id===section.dataset.section);navigate(s.pages[0].id);return;}
  const task=event.target.closest('[data-task]');if(task){state.task=Number(task.dataset.task);state.selectedRow=0;render();return;}
  const result=event.target.closest('[data-result]');if(result){state.resultView=result.dataset.result;renderResults();return;}
  const row=event.target.closest('[data-row]');if(row){state.selectedRow=Number(row.dataset.row);renderInspector();document.querySelectorAll('[data-row]').forEach((r)=>{const selected=r.dataset.row===row.dataset.row;r.classList.toggle('selected',selected);r.setAttribute('aria-selected',String(selected));});$('shell').classList.remove('inspector-hidden');return;}
  const connection=event.target.closest('[data-connect]');if(connection){state.connection=connection.dataset.connect;state.mode='normal';closeModal();render();toast('连接示例上下文已切换；未重放任何操作。');return;}
  const action=event.target.closest('[data-action]');if(action&&!action.disabled)act(action.dataset.action);
});
document.addEventListener('input',(event)=>{
  if(event.target.id==='sqlEditor')state.drafts[draftKey('sql')]=event.target.innerText;
  if(event.target.dataset.draft)state.drafts[event.target.dataset.draft]=event.target.value;
  if(event.target.id==='resourceSearch')renderSidebar();
  if(event.target.id==='commandInput')filterPalette(event.target.value);
  if(['riskCheck','confirmationName'].includes(event.target.id)){
    const commit=$('commitDraft');if(commit){const expected=current().objectName||current().title;commit.disabled=!$('riskCheck').checked||(commit.dataset.danger==='true'&&$('confirmationName').value!==expected);}
  }
});
document.addEventListener('change',(event)=>{if(event.target.dataset.draft)state.drafts[event.target.dataset.draft]=event.target.value;if(event.target.id==='reviewDialog'&&event.target.value){const action=event.target.value;state.host=$('reviewHost').value;state.mode=$('reviewState').value;closeModal();render();act(action);}});
document.addEventListener('keydown',(event)=>{
  if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='k'){event.preventDefault();palette();}
  if((event.ctrlKey||event.metaKey)&&event.key==='Enter'&&!$('modal').open){event.preventDefault();if(!blocked())act('run');}
  if(event.key==='Escape'){if($('modal').open)closeModal();else if(!$('drawer').hidden)closeDrawer();}
  if(event.key==='/'&&!['INPUT','TEXTAREA'].includes(event.target.tagName)&&!event.target.isContentEditable&&!$('modal').open){event.preventDefault();$('resourceSearch').focus();}
  if(event.key==='Enter'&&event.target.dataset.row!==undefined){event.target.click();}
  if(event.key==='Enter'&&event.target.dataset.closeTab){event.preventDefault();event.target.click();}
  if(['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(event.key)){
    const container=event.target.closest('.task-tabs,.workspace-tabs,.result-tabs,.rail');
    if(container){const candidates=[...container.querySelectorAll('button:not(:disabled)')];const index=candidates.indexOf(event.target);if(index>=0){event.preventDefault();candidates[(index+(['ArrowLeft','ArrowUp'].includes(event.key)?-1:1)+candidates.length)%candidates.length].focus();}}
  }
});
document.querySelectorAll('[data-resize]').forEach((splitter)=>{
  const kind=splitter.dataset.resize;const key={sidebar:'--sidebar',inspector:'--inspector',results:'--results'}[kind];
  const bounds=kind==='sidebar'?[240,420]:kind==='inspector'?[320,520]:[180,innerHeight*.55];
  const set=(value)=>{document.documentElement.style.setProperty(key,`${Math.max(bounds[0],Math.min(bounds[1],value))}px`);};
  splitter.addEventListener('keydown',(event)=>{if(['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(event.key)){event.preventDefault();const now=parseFloat(getComputedStyle(document.documentElement).getPropertyValue(key));set(now+(['ArrowRight','ArrowUp'].includes(event.key)?16:-16));}});
  splitter.addEventListener('pointerdown',(event)=>{
    const start=kind==='results'?event.clientY:event.clientX;const initial=parseFloat(getComputedStyle(document.documentElement).getPropertyValue(key));splitter.setPointerCapture(event.pointerId);
    const move=(e)=>{const next=kind==='results'?e.clientY:e.clientX;set(initial+(kind==='sidebar'?next-start:start-next));};
    const done=()=>{splitter.removeEventListener('pointermove',move);splitter.removeEventListener('pointerup',done);splitter.removeEventListener('pointercancel',done);};
    splitter.addEventListener('pointermove',move);splitter.addEventListener('pointerup',done,{once:true});splitter.addEventListener('pointercancel',done,{once:true});
  });
});
addEventListener('resize',()=>{renderState();});
addEventListener('popstate',()=>{const id=location.hash.slice(1);if(findPage(id))navigate(id);});
addEventListener('hashchange',()=>{const id=location.hash.slice(1);if(findPage(id))navigate(id);});
if(innerWidth<1100)$('shell').classList.add('sidebar-hidden');
if(innerWidth<1440)$('shell').classList.add('inspector-hidden');
if(location.hash&&findPage(location.hash.slice(1)))state.pageId=location.hash.slice(1);
navigate(state.pageId);
