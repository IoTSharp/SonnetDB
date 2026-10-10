<template>
  <section class="preview-workbench" data-testid="preview-workbench">
    <div class="preview-controls">
      <label>数据库 <select v-model="database" aria-label="数据库" @change="load"><option value="">请选择</option><option v-for="db in databases" :key="db">{{ db }}</option></select></label>
      <label>模型 <select v-model="tool" aria-label="模型" @change="load"><option v-for="value in previewTools" :key="value" :value="value">{{ labels[value] }}</option></select></label>
      <label v-if="tool !== 'sql' && previewToolEnabled(tool)">资源 <select v-model="resource" aria-label="资源" @change="load"><option v-for="name in resourceNames" :key="name">{{ name }}</option></select></label>
      <button :disabled="loading" @click="load">清空并重新读取</button>
      <button @click="cancel">取消当前操作</button>
      <span>{{ previewSession.canWrite ? '可读写 · 仅单行插入' : '只读' }}</span>
    </div>
    <p class="preview-note">其它工具栏和子页本预览未开放。结果仅保留当前窗口；取消不等于回滚，未知写入不会自动重放。</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading">正在核对权限与加载资源…</p>
    <p v-if="!previewToolEnabled(tool)" data-testid="preview-model-deferred">{{ labels[tool] }} · {{ deferredModelReason }} Graph 保持 Beta；MQ 身份为 database + Topic，单库备份不包含实例 MQ。</p>
    <template v-if="ready && !previewSession.blocked">
      <details v-if="tool === 'table' || tool === 'measurement'"><summary>Schema · {{ resource }}</summary><pre>{{ selectedSchema }}</pre></details>
      <div v-if="tool === 'sql'" class="preview-sql">
        <label for="preview-sql">单条只读 SQL</label>
        <textarea id="preview-sql" v-model="sql" spellcheck="false" :maxlength="65536" />
        <button :disabled="running || !sql.trim()" @click="runSql">执行读取</button>
        <p v-if="result?.end?.truncated" role="status">结果已截断，只显示当前最多 1000 行。</p>
        <p v-if="result?.error" role="alert">{{ result.error.message }}</p>
        <button v-if="result?.hasColumns && !result.error" @click="exportSql">导出当前窗口</button>
        <pre v-if="result?.hasColumns">{{ JSON.stringify({ columns: result.columns, rows: result.rows }, null, 2) }}</pre>
      </div>
      <RelationalTableWorkbench v-else-if="tool === 'table'" :key="panelKey" :target-db="database" :table="table" :tables="schema?.tables" :read-only="!previewSession.canWrite" @open-sql="openSql" />
      <MeasurementWorkbench v-else-if="tool === 'measurement'" :key="panelKey" :target-db="database" :measurement="measurement" :measurements="schema?.measurements" :read-only="true" @open-sql="openSql" />
    </template>
  </section>
</template>
<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, provide, ref, shallowRef, watch } from 'vue';
import { useRoute } from 'vue-router';
import { useAuthStore } from '@/stores/auth';
import { fetchSchema, type SchemaResponse } from '@/api/schema';
import { listDatabases } from '@/api/server';
import { execDataSql, type SqlResultSet } from '@/api/sql';
import { saveTextFile } from '@/utils/resultExport';
import { previewTools, previewSqlReadAllowed, previewToolEnabled, deferredModelReason, type PreviewTool } from './policy';
import { clearPreviewSession, previewSession, selectPreviewDatabase } from './session';
const RelationalTableWorkbench = defineAsyncComponent(() => import('@/components/RelationalTableWorkbench.vue'));
const MeasurementWorkbench = defineAsyncComponent(() => import('@/components/MeasurementWorkbench.vue'));
const labels = { sql: 'SQL', table: '关系表', measurement: '时序', document: '文档', kv: 'KV', fulltext: '全文', vector: '向量', bucket: '对象', mq: 'MQ', graph: 'Graph Beta' };
const auth = useAuthStore();
const route = useRoute();
const database = ref('');
const tool = ref<PreviewTool>((route.query.tool ?? 'sql') as PreviewTool);
provide('previewTool', tool);
const resource = ref('');
const databases = ref<string[]>([]);
const schema = shallowRef<SchemaResponse | null>(null);
const loading = ref(false);
const ready = ref(false);
const error = ref('');
const sql = ref('SELECT 1');
const result = shallowRef<SqlResultSet | null>(null);
const running = ref(false);
const panelKey = computed(() => `${previewSession.value.generation}:${resource.value}`);
const table = computed(() => schema.value?.tables?.find((item) => item.name === resource.value) ?? null);
const measurement = computed(() => schema.value?.measurements.find((item) => item.name === resource.value) ?? null);
const selectedSchema = computed(() => table.value ?? measurement.value);
const resourceNames = computed<string[]>(() => tool.value === 'table' ? schema.value?.tables?.map((item) => item.name) ?? [] : tool.value === 'measurement' ? schema.value?.measurements.map((item) => item.name) ?? [] : []);
let loadRequest = 0;
function clearPayload() { loadRequest++; ready.value = false; loading.value = false; running.value = false; schema.value = null; result.value = null; sql.value = ''; resource.value = ''; }
watch(() => previewSession.value.generation, clearPayload, { flush: 'sync' });
async function load() {
  const wanted = resource.value || String(route.query.resource ?? '');
  clearPreviewSession();
  let request = loadRequest;
  const target = database.value;
  error.value = ''; loading.value = true;
  try {
    const list = await listDatabases(auth.api);
    if (request !== loadRequest) return;
    if (list.error) throw new Error(list.error.message);
    databases.value = list.databases;
    if (!target || !databases.value.includes(target)) { database.value = ''; return; }
    if (!previewToolEnabled(tool.value)) return;
    const access = (await auth.api.get<{ canRead: boolean; canWrite: boolean }>(`/v1/db/${encodeURIComponent(target)}/access`)).data;
    if (request !== loadRequest) return;
    if (access.canRead !== true || typeof access.canWrite !== 'boolean') throw new Error('无法确认当前数据库读取权限。');
    selectPreviewDatabase(target, access.canWrite);
    request = loadRequest; loading.value = true;
    const loaded = await fetchSchema(auth.api, target);
    if (request !== loadRequest) return;
    schema.value = loaded;
    resource.value = resourceNames.value.includes(wanted) ? wanted : resourceNames.value[0] ?? '';
    ready.value = true; sql.value = 'SHOW TABLES';
  } catch (cause) { if (request === loadRequest) { clearPayload(); error.value = cause instanceof Error ? cause.message : '读取失败'; } }
  finally { if (request === loadRequest) loading.value = false; }
}
function cancel() { clearPreviewSession('操作已取消；写入若已发送，状态仍未知且不等于回滚。请显式重读。', true); }
async function runSql() {
  if (running.value) return;
  result.value = null;
  if (!previewSqlReadAllowed(sql.value)) { error.value = '本预览只允许单条 SELECT、只读 SHOW/DESCRIBE 或 EXPLAIN SELECT，输入最多64 KiB。'; return; }
  const generation = previewSession.value.generation;
  running.value = true; result.value = null; error.value = '';
  try { const value = await execDataSql(auth.api, database.value, sql.value, undefined, undefined, 1000); if (generation === previewSession.value.generation) result.value = value; }
  catch (cause) { if (generation === previewSession.value.generation) error.value = cause instanceof Error ? cause.message : '读取失败'; }
  finally { if (generation === previewSession.value.generation) running.value = false; }
}
async function openSql(value: string) { if (!previewSqlReadAllowed(value)) { error.value = '本预览未开放此 SQL。'; return; } tool.value = 'sql'; await load(); if (ready.value) sql.value = value; }
async function exportSql() {
  if (!result.value || result.value.error || previewSession.value.blocked) return;
  try { await saveTextFile('sonnetdb-preview-window.json', JSON.stringify({ columns: result.value.columns, rows: result.value.rows }), 'application/json'); }
  catch (cause) { error.value = cause instanceof Error ? cause.message : '导出失败'; }
}
async function checkAccess() {
  if (!ready.value || loading.value || running.value || !database.value) return;
  const generation = previewSession.value.generation;
  try {
    const value = (await auth.api.get<{ canRead: boolean; canWrite: boolean }>(`/v1/db/${encodeURIComponent(database.value)}/access`)).data;
    if (generation !== previewSession.value.generation) return;
    if (value.canRead !== true || value.canWrite !== previewSession.value.canWrite) clearPreviewSession('权限已变化；旧载荷、草稿与审批已清除。请显式重读。', true);
  } catch { if (generation === previewSession.value.generation) clearPreviewSession('无法确认权限；旧载荷已清除。请显式重读。', true); }
}
watch(() => route.fullPath, () => { tool.value = (route.query.tool ?? 'sql') as PreviewTool; database.value = String(route.query.db ?? database.value); void load(); });
onMounted(() => { database.value = String(route.query.db ?? ''); void load(); window.addEventListener('focus', checkAccess); });
onBeforeUnmount(() => { window.removeEventListener('focus', checkAccess); clearPreviewSession(); });
</script>
<style scoped>
.preview-workbench { display: flex; flex-direction: column; min-height: 100%; padding: 12px; gap: 8px; }
.preview-controls { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; } select, button, textarea { font: inherit; padding: 6px; } .preview-note { font-size: 12px; color: var(--sndb-ink-muted); } .preview-sql { display: flex; flex-direction: column; gap: 10px; } textarea { min-height: 130px; font-family: monospace; } pre { overflow: auto; max-height: 520px; } .preview-workbench > :deep(main), .preview-workbench > :deep(section) { min-height: 600px; }
</style>
