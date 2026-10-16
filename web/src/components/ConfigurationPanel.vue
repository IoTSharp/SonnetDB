<template>
  <div>
    <div class="config-toolbar">
      <n-select v-if="scope === 'database'" v-model:value="database" :options="databases.map(value => ({ label: value, value }))" aria-label="参数数据库" placeholder="选择数据库" />
      <n-input v-model:value="search" clearable placeholder="搜索参数名称或值" aria-label="搜索配置参数" />
      <n-button :loading="loading" @click="load">刷新参数</n-button>
    </div>
    <n-alert v-if="error" type="error">{{ error }}</n-alert>
    <p v-else-if="!snapshot">{{ loading ? '正在读取生效配置…' : '当前账号没有可读取的数据库。' }}</p>
    <template v-if="snapshot">
      <p class="source">{{ snapshot.source }}</p>
      <p class="source">{{ snapshot.entries.length }} 项参数 · 采集于 {{ new Date(snapshot.capturedAtUtc).toLocaleString() }}。密钥、密码及连接凭据不返回浏览器。</p>
      <p class="source">启动参数暂不支持网页直接保存，需由运维修改部署配置并重启。AI 设置和数据模型中已有的在线修改功能可通过下方入口使用。</p>
      <details v-for="group in groups" :key="group.key" :open="!!search || groups.length === 1">
        <summary>{{ group.label }} <span>{{ group.entries.length }} 项</span></summary>
        <div class="config-table"><table>
          <thead><tr><th>参数</th><th>生效值</th><th>修改方式</th></tr></thead>
          <tbody><tr v-for="entry in group.entries" :key="entry.path"><td><code>{{ entry.path }}</code></td><td :class="{ redacted: entry.redacted }">{{ entry.value }}</td><td>{{ entry.changeMode }}</td></tr></tbody>
        </table></div>
      </details>
      <p v-if="!groups.length">没有匹配的参数，请调整搜索条件。</p>
      <n-button v-if="scope === 'database' && database" @click="router.push({ name: 'sql', query: { db: database } })">管理数据模型配置</n-button>
      <n-button v-if="scope === 'instance'" @click="router.push({ name: 'ai-settings' })">打开 AI 设置</n-button>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { NAlert, NButton, NInput, NSelect } from 'naive-ui';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';

interface Entry { path: string; value: string; redacted: boolean; changeMode: string }
interface Snapshot { scope: string; source: string; capturedAtUtc: string; entries: Entry[] }
const props = defineProps<{ scope: 'database' | 'instance' }>();
const auth = useAuthStore(), connections = useConnectionsStore(), router = useRouter();
const database = ref(''), databases = ref<string[]>([]), search = ref(''), error = ref(''), loading = ref(false);
const snapshot = ref<Snapshot | null>(null);
let controller: AbortController | null = null, generation = 0, disposed = false;
const labels: Record<string, string> = {
  General: '常规', SqlHttpAdmission: 'SQL 请求准入', SqlExecution: 'SQL 执行预算', SqlMemory: 'SQL 内存与并行',
  Kv: 'KV 存储与恢复', MeasurementSchema: '自动 Schema 策略', MeasurementSchemaPolicy: 'Schema 策略',
  Observability: '观测与慢查询', Mqtt: 'MQTT 接入', Coap: 'CoAP 接入', LineProtocolUdp: 'UDP 接入', Modbus: 'Modbus',
  Copilot: 'Copilot 与 RAG', SemanticSearch: '语义检索与对象处理', AI: 'AI 账号配置',
  FlushPolicy: 'MemTable 刷盘策略', SegmentWriterOptions: 'Segment 写入', SegmentReaderOptions: 'Segment 读取与缓存',
  WalGroupCommit: 'WAL 组提交', BackgroundFlush: '后台刷盘', Compaction: '压缩合并', WalRolling: 'WAL 滚动',
  Retention: '数据保留', TombstoneCheckpoint: '删除标记检查点',
};
const groups = computed(() => {
  const grouped = new Map<string, Entry[]>();
  const term = search.value.trim().toLowerCase();
  for (const entry of snapshot.value?.entries ?? []) {
    const parts = entry.path.split(':');
    const key = parts[0] === 'AI' ? 'AI' : parts.length > 2 ? parts[1] : 'General';
    if (term && !`${entry.path} ${entry.value} ${labels[key] ?? key}`.toLowerCase().includes(term)) continue;
    const rows = grouped.get(key) ?? []; rows.push(entry); grouped.set(key, rows);
  }
  return [...grouped].map(([key, entries]) => ({ key, label: labels[key] ?? key, entries }));
});
async function load(): Promise<void> {
  controller?.abort(); controller = new AbortController();
  const current = ++generation, config = { signal: controller.signal, timeout: 15000 };
  snapshot.value = null; error.value = ''; loading.value = true;
  try {
    if (props.scope === 'instance' && !auth.isSuperuser) { error.value = '整个实例的配置仅限实例管理员查看。'; return; }
    if (props.scope === 'database') {
      const response = await auth.api.get<{ databases: string[] }>('/v1/db', config);
      if (current !== generation || disposed) return;
      databases.value = response.data.databases;
      if (!databases.value.includes(database.value)) { database.value = databases.value.includes(connections.activeDatabase) ? connections.activeDatabase : databases.value[0] ?? ''; return; }
      if (!database.value) return;
    }
    const path = props.scope === 'instance' ? '/v1/system/configuration' : `/v1/db/${encodeURIComponent(database.value)}/configuration`;
    const response = await auth.api.get<Snapshot>(path, config);
    if (current === generation && !disposed) snapshot.value = response.data;
  } catch (failure: unknown) {
    if (current !== generation || disposed) return;
    const status = (failure as { response?: { status?: number } }).response?.status;
    error.value = status === 403 ? '当前账号没有读取这些配置的权限。' : `配置读取失败${status ? `（HTTP ${status}）` : ''}，请重新登录或刷新重试。`;
  } finally { if (current === generation && !disposed) loading.value = false; }
}
watch(database, () => { void load(); });
watch(() => [connections.activeBaseUrl, auth.username], () => { database.value = ''; void load(); });
onMounted(() => { void load(); });
onBeforeUnmount(() => { disposed = true; generation++; controller?.abort(); });
</script>

<style scoped>
.config-toolbar { display: flex; gap: 10px; flex-wrap: wrap; margin-bottom: 16px; }
.config-toolbar .n-select { width: 180px; }.config-toolbar .n-input { flex: 1; min-width: 180px; }
.source { color: var(--sndb-ink-soft, #566578); line-height: 1.7; font-size: 13px; }
details { border-top: 1px solid #dce3eb; }
summary { cursor: pointer; padding: 15px 0; font-weight: 600; }
summary span { font-weight: 400; margin-left: 12px; color: var(--sndb-ink-soft, #566578); }
.config-table { overflow-x: auto; margin-bottom: 14px; }
table { width: 100%; border-collapse: collapse; font-size: 13px; }
th, td { padding: 10px; border-bottom: 1px solid #e4e9ef; text-align: left; vertical-align: top; }
td:first-child { width: 52%; }td { overflow-wrap: anywhere; min-width: 110px; }
.redacted { color: var(--sndb-ink-soft, #566578); }
code { font-size: 12px; white-space: normal; }
summary:focus-visible { outline: 2px solid #2c7be5; outline-offset: 2px; }
@media (max-width: 600px) { .config-toolbar .n-select { width: 100%; } }
</style>
