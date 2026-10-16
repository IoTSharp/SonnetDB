<template>
  <section class="diagnostics" aria-labelledby="diagnostics-title">
    <header>
      <div><h1 id="diagnostics-title">数据库诊断</h1><p>实时读取当前账号有权访问的数据库。示例数据与运行诊断分别展示。</p></div>
      <n-button :loading="loading" @click="refresh">重新检测</n-button>
    </header>
    <div class="toolbar">
      <n-select v-model:value="selected" :options="databases.map(value => ({ label: value, value }))" aria-label="诊断数据库" placeholder="选择数据库" />
      <span role="status">{{ checkedAt ? `采集于 ${checkedAt}` : '等待检测' }}{{ loading ? ' · 检测中' : '' }}</span>
    </div>
    <n-alert v-if="error" type="error" style="margin:16px 0">{{ error }}</n-alert>
    <n-alert v-if="!loading && !databases.length && !error" type="info">当前账号没有可读取的数据库，请联系管理员授权。</n-alert>
    <template v-if="selected">
      <h2>模型与存储</h2>
      <div class="model-grid">
        <div v-for="model in models" :key="model.label"><span>{{ model.label }}</span><strong>{{ model.count ?? '未取得' }}</strong><small>{{ model.unit }}</small></div>
      </div>
      <dl v-if="storage" class="storage">
        <div><dt>数据文件</dt><dd>{{ bytes(storage.totalBytes) }}</dd></div>
        <div><dt>时序 Segment</dt><dd>{{ storage.segmentCount }}</dd></div>
        <div><dt>WAL 文件</dt><dd>{{ storage.walFileCount }}</dd></div>
        <div><dt>待刷盘点数</dt><dd>{{ storage.memTablePointCount }}</dd></div>
        <div><dt>检查点 LSN</dt><dd>{{ storage.checkpointLsn }}</dd></div>
      </dl>
      <p class="hint">模型数量表示已创建的对象。MQ 数据按实例保存，单库备份不包含 MQ；存在 WAL 或备份能力不代表已经完成备份。</p>
      <h2>自检结果</h2>
      <div class="table-scroll"><table>
        <thead><tr><th>检查</th><th>结果</th><th>耗时</th><th>说明</th></tr></thead>
        <tbody><tr v-for="check in checks" :key="check.name"><th>{{ check.name }}</th><td :class="check.ok ? 'pass' : 'fail'">{{ check.ok ? '正常' : '失败' }}</td><td>{{ check.ms }} ms</td><td>{{ check.detail }}</td></tr></tbody>
      </table></div>
      <h2>查询诊断</h2>
      <p v-if="!top" class="hint">{{ loading ? '正在读取查询诊断…' : '查询诊断尚未取得，请查看自检结果。' }}</p>
      <p v-else-if="!top.enabled" class="hint">服务端尚未启用慢查询采集。</p>
      <p v-else-if="!top.items.length" class="hint">暂无超过采集阈值的查询；这不表示数据库没有数据或查询失败。正常执行查询后，符合阈值的记录会出现在这里。</p>
      <div v-else class="table-scroll"><table>
        <thead><tr><th>查询形状</th><th>次数</th><th>失败</th><th>P95</th></tr></thead>
        <tbody><tr v-for="item in top.items" :key="item.fingerprint"><td><code>{{ item.normalizedSql }}</code></td><td>{{ item.count }}</td><td>{{ item.failedCount }}</td><td>{{ item.p95Ms.toFixed(2) }} ms</td></tr></tbody>
      </table></div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { NAlert, NButton, NSelect } from 'naive-ui';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import { usePreferencesStore } from '@/stores/preferences';
import { execDataSql } from '@/api/sql';
import type { BackupStatusInfo, SchemaResponse } from '@/api/schema';
import type { TopQueryListResponse } from '@/api/diagnostics';

const auth = useAuthStore();
const connections = useConnectionsStore();
const preferences = usePreferencesStore();
const databases = ref<string[]>([]), selected = ref(''), loading = ref(false), error = ref(''), checkedAt = ref('');
const storage = ref<BackupStatusInfo | null>(null), top = ref<TopQueryListResponse | null>(null);
const definitions = [['时序', 'measurement'], ['关系', '表'], ['文档', '集合'], ['KV', 'keyspace'], ['全文', '索引'], ['向量', '索引'], ['对象', '桶'], ['消息', 'topic'], ['属性图', '图']];
const models = ref(definitions.map(([label, unit]) => ({ label, unit, count: null as number | null })));
const checks = ref<Array<{ name: string; ok: boolean; ms: number; detail: string }>>([]);
let controller: AbortController | null = null;
let timer: ReturnType<typeof setTimeout> | null = null;
let generation = 0, stopped = false, automaticSamples = 0;

function bytes(value: number): string { return value >= 1048576 ? `${(value / 1048576).toFixed(2)} MiB` : `${(value / 1024).toFixed(1)} KiB`; }
function clearSnapshot(): void {
  storage.value = null; top.value = null; checks.value = []; checkedAt.value = '';
  models.value = definitions.map(([label, unit]) => ({ label, unit, count: null }));
}
async function refresh(): Promise<void> {
  controller?.abort();
  const current = ++generation;
  const abort = new AbortController(); controller = abort;
  const currentDatabase = selected.value;
  clearSnapshot(); loading.value = true; error.value = '';
  const config = { signal: abort.signal, timeout: 12000 };
  try {
    const response = await auth.api.get<{ databases: string[] }>('/v1/db', config);
    if (current !== generation || stopped) return;
    databases.value = response.data.databases;
    if (!databases.value.includes(currentDatabase)) {
      selected.value = databases.value.includes(connections.activeDatabase) ? connections.activeDatabase : databases.value[0] ?? '';
      return;
    }
    if (!currentDatabase) return;
    const path = `/v1/db/${encodeURIComponent(currentDatabase)}`;
    const count = (index: number, value: number): void => { models.value[index].count = value; };
    const run = async (name: string, action: () => Promise<unknown>, apply: (data: any) => void): Promise<void> => {
      const start = performance.now();
      try {
        const data = await action();
        if (current !== generation || stopped) return;
        apply(data);
        checks.value.push({ name, ok: true, ms: Math.round(performance.now() - start), detail: '授权读取成功' });
      } catch (failure: unknown) {
        if (current !== generation || stopped || abort.signal.aborted) return;
        const status = (failure as { response?: { status?: number } }).response?.status;
        checks.value.push({ name, ok: false, ms: Math.round(performance.now() - start), detail: status ? `HTTP ${status}，可重新检测` : '请求未完成，请检查连接后重试' });
      }
    };
    // 分两批，最多四个并发请求；每个请求有超时，切换连接/数据库会取消旧批次。
    await Promise.all([
      run('数据目录与 WAL', () => auth.api.get<SchemaResponse>(path + '/schema', config), ({ data }: { data: SchemaResponse }) => {
        count(0, data.measurements.length); count(1, data.tables?.length ?? 0); count(2, data.documentCollections?.length ?? 0); storage.value = data.backupStatus ?? null;
      }),
      run('KV 目录', () => auth.api.post(path + '/kv/keyspaces', undefined, config), ({ data }) => count(3, data.keyspaces.length)),
      run('全文索引', () => auth.api.post(path + '/fulltext/indexes', undefined, config), ({ data }) => count(4, data.indexes.length)),
      run('向量索引', () => auth.api.post(path + '/vector/indexes', undefined, config), ({ data }) => count(5, data.indexes.length)),
    ]);
    if (current !== generation || stopped) return;
    await Promise.all([
      run('对象桶', () => auth.api.get(path + '/s3', config), ({ data }) => count(6, data.length)),
      run('消息主题', () => auth.api.post(path + '/mq/topics', undefined, config), ({ data }) => count(7, data.topics.length)),
      run('属性图', () => auth.api.get(path + '/graphs', config), ({ data }) => count(8, data.length)),
      run('查询诊断', () => auth.api.get<TopQueryListResponse>('/v1/diagnostics/top-queries', { ...config, params: { database: currentDatabase, limit: 10 } }), ({ data }) => { top.value = data; }),
    ]);
    if (current !== generation || stopped) return;
    await run('SQL 只读执行', () => execDataSql(auth.api, currentDatabase, 'SELECT 1 AS diagnostic_ok', undefined, abort.signal), result => {
      if (result.error || result.rows?.[0]?.[0] !== 1) throw new Error('SQL probe failed');
    });
    if (current === generation && !stopped) checkedAt.value = new Date().toLocaleTimeString();
  } catch {
    if (current === generation && !stopped && !abort.signal.aborted) { databases.value = []; error.value = '无法读取数据库列表，请检查登录状态和网络后重新检测。'; }
  } finally { if (current === generation && !stopped) loading.value = false; }
}
function schedule(): void {
  if (timer) clearTimeout(timer);
  if (stopped || preferences.refreshSeconds === 0 || automaticSamples >= 120) return;
  timer = setTimeout(async () => {
    if (!document.hidden && !loading.value) { automaticSamples++; await refresh(); }
    schedule();
  }, preferences.refreshSeconds * 1000);
}
watch(selected, () => { void refresh(); });
watch(() => [connections.activeBaseUrl, auth.username], () => { selected.value = ''; void refresh(); });
watch(() => preferences.refreshSeconds, () => { automaticSamples = 0; schedule(); });
onMounted(() => { void refresh(); schedule(); });
onBeforeUnmount(() => { stopped = true; generation++; controller?.abort(); if (timer) clearTimeout(timer); });
</script>

<style scoped>
.diagnostics { margin-bottom: 32px; }
header { display: flex; justify-content: space-between; align-items: center; gap: 16px; }
h1 { margin: 0; font-size: 24px; }
h2 { margin: 28px 0 14px; font-size: 17px; }
header p, .hint, dt, .toolbar span { color: var(--sndb-ink-soft, #566578); }
.toolbar { display: flex; align-items: center; flex-wrap: wrap; gap: 16px; margin: 18px 0; }
.toolbar .n-select { width: 240px; }
.model-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(125px, 1fr)); border-top: 1px solid #dce3eb; border-bottom: 1px solid #dce3eb; padding: 16px 0; gap: 20px; }
.model-grid > div { display: flex; align-items: baseline; gap: 8px; }
.model-grid strong { font-variant-numeric: tabular-nums; }
.model-grid small { color: var(--sndb-ink-soft, #566578); }
.storage { display: flex; flex-wrap: wrap; gap: 20px 36px; margin: 20px 0; }
dd { margin: 5px 0 0; font-variant-numeric: tabular-nums; }
.hint { font-size: 13px; line-height: 1.7; max-width: 90ch; }
.table-scroll { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-size: 13px; }
td, th { text-align: left; padding: 10px 12px; border-bottom: 1px solid #e4e9ef; font-variant-numeric: tabular-nums; }
tbody th { font-weight: 500; }
code { overflow-wrap: anywhere; white-space: pre-wrap; }
.pass { color: #17633d; }.fail { color: #aa2727; }
@media (max-width: 600px) { header { align-items: flex-start; flex-direction: column; } .toolbar .n-select { width: 100%; } }
</style>
