<template>
  <n-drawer
    :show="show"
    :width="drawerWidth"
    placement="right"
    class="query-diagnostics"
    @update:show="$emit('update:show', $event)"
  >
    <n-drawer-content title="查询诊断" closable>
      <div class="diagnostics-toolbar">
        <n-radio-group v-model:value="mode" size="small">
          <n-radio-button value="slow">慢查询</n-radio-button>
          <n-radio-button value="top">Top-N</n-radio-button>
        </n-radio-group>

        <n-select
          v-model:value="database"
          class="database-filter"
          size="small"
          :options="databaseOptions"
          placeholder="全部数据库"
        />

        <n-select
          v-model:value="limit"
          class="limit-filter"
          size="small"
          :options="limitOptions"
        />

        <n-button quaternary size="small" title="刷新查询诊断" :loading="loading" @click="load">
          <template #icon><RefreshCw :size="16" /></template>
        </n-button>
      </div>

      <div class="diagnostics-summary">
        <span>{{ summaryText }}</span>
        <span v-if="slowData?.enabled">阈值 {{ formatDuration(slowData.thresholdMs) }}</span>
        <span v-if="capacity">内存缓冲 {{ capacity }} 条</span>
      </div>

      <n-alert v-if="enabled === false" type="warning" :bordered="false" title="慢查询采集已关闭" />
      <n-alert v-if="error" type="error" :bordered="false" :title="error" />

      <n-data-table
        v-if="mode === 'slow' && slowRows.length > 0"
        class="diagnostics-table"
        :columns="slowColumns"
        :data="slowRows"
        :bordered="false"
        :single-line="false"
        :max-height="tableHeight"
        size="small"
      />

      <n-data-table
        v-else-if="mode === 'top' && topRows.length > 0"
        class="diagnostics-table"
        :columns="topColumns"
        :data="topRows"
        :bordered="false"
        :single-line="false"
        :max-height="tableHeight"
        size="small"
      />

      <n-empty
        v-else-if="!loading && !error"
        class="diagnostics-empty"
        :description="enabled === false ? '服务端未采集慢查询' : '当前缓冲窗口没有记录'"
      />
    </n-drawer-content>
  </n-drawer>
</template>

<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import {
  NAlert,
  NButton,
  NDataTable,
  NDrawer,
  NDrawerContent,
  NEmpty,
  NRadioButton,
  NRadioGroup,
  NSelect,
  NTag,
  useMessage,
  type DataTableColumns,
  type SelectOption,
} from 'naive-ui';
import { Copy, RefreshCw } from 'lucide-vue-next';
import {
  fetchSlowQueries,
  fetchTopQueries,
  type SlowQueryDiagnosticEntry,
  type SlowQueryListResponse,
  type TopQueryDiagnosticEntry,
  type TopQueryListResponse,
} from '@/api/diagnostics';
import { useAuthStore } from '@/stores/auth';

const props = defineProps<{
  show: boolean;
  activeDatabase: string;
  databases: string[];
}>();

defineEmits<{
  'update:show': [value: boolean];
}>();

const auth = useAuthStore();
const message = useMessage();
const mode = ref<'slow' | 'top'>('slow');
const database = ref('');
const limit = ref(50);
const loading = ref(false);
const error = ref('');
const slowData = ref<SlowQueryListResponse | null>(null);
const topData = ref<TopQueryListResponse | null>(null);
const drawerWidth = ref(920);
const tableHeight = ref(620);
let requestSequence = 0;

const limitOptions: SelectOption[] = [
  { label: '20 条', value: 20 },
  { label: '50 条', value: 50 },
  { label: '100 条', value: 100 },
];

const databaseOptions = computed<SelectOption[]>(() => [
  { label: '全部可见数据库', value: '' },
  ...props.databases.map((name) => ({ label: name, value: name })),
]);

const slowRows = computed(() => slowData.value?.items ?? []);
const topRows = computed(() => topData.value?.items ?? []);
const enabled = computed(() => mode.value === 'slow' ? slowData.value?.enabled : topData.value?.enabled);
const capacity = computed(() => mode.value === 'slow' ? slowData.value?.capacity : topData.value?.capacity);
const summaryText = computed(() => {
  if (mode.value === 'slow') {
    const count = slowData.value?.count ?? 0;
    return `${count} 条可见慢查询`;
  }
  const groups = topData.value?.items.length ?? 0;
  const samples = topData.value?.sampleCount ?? 0;
  return `${groups} 个指纹 · ${samples} 条样本`;
});

const slowColumns: DataTableColumns<SlowQueryDiagnosticEntry> = [
  {
    title: '时间',
    key: 'timestampMs',
    width: 156,
    render: (row) => formatTimestamp(row.timestampMs),
  },
  { title: '数据库', key: 'database', width: 120, ellipsis: { tooltip: true } },
  {
    title: '耗时',
    key: 'elapsedMs',
    width: 96,
    sorter: (a, b) => a.elapsedMs - b.elapsedMs,
    render: (row) => h(NTag, {
      size: 'small',
      bordered: false,
      type: severityType(row.severity),
    }, { default: () => formatDuration(row.elapsedMs) }),
  },
  {
    title: '结果',
    key: 'rowCount',
    width: 92,
    render: (row) => row.failed ? '失败' : row.recordsAffected >= 0 ? `${row.recordsAffected} 影响` : `${row.rowCount} 行`,
  },
  {
    title: 'SQL',
    key: 'sql',
    minWidth: 330,
    render: (row) => h('div', { class: 'sql-cell' }, [
      h('code', { title: row.sql }, row.sql),
      h('small', `${row.fingerprint} · ${row.normalizedSql}`),
    ]),
  },
  {
    title: '',
    key: 'actions',
    width: 48,
    render: (row) => h(NButton, {
      quaternary: true,
      size: 'tiny',
      title: '复制 SQL',
      onClick: () => copySql(row.sql),
    }, { icon: () => h(Copy, { size: 14 }) }),
  },
];

const topColumns: DataTableColumns<TopQueryDiagnosticEntry> = [
  { title: '数据库', key: 'database', width: 120, ellipsis: { tooltip: true } },
  {
    title: '查询指纹',
    key: 'normalizedSql',
    minWidth: 330,
    render: (row) => h('div', { class: 'sql-cell' }, [
      h('code', { title: row.normalizedSql }, row.normalizedSql),
      h('small', row.fingerprint),
    ]),
  },
  { title: '次数', key: 'count', width: 72, sorter: (a, b) => a.count - b.count },
  { title: 'P50', key: 'p50Ms', width: 86, sorter: (a, b) => a.p50Ms - b.p50Ms, render: (row) => formatDuration(row.p50Ms) },
  { title: 'P95', key: 'p95Ms', width: 86, sorter: (a, b) => a.p95Ms - b.p95Ms, render: (row) => formatDuration(row.p95Ms) },
  { title: '最大', key: 'maxMs', width: 90, sorter: (a, b) => a.maxMs - b.maxMs, render: (row) => formatDuration(row.maxMs) },
  {
    title: '失败',
    key: 'failedCount',
    width: 68,
    render: (row) => row.failedCount > 0
      ? h(NTag, { size: 'small', type: 'error', bordered: false }, { default: () => row.failedCount })
      : '0',
  },
];

function formatTimestamp(timestampMs: number): string {
  return new Date(timestampMs).toLocaleString('zh-CN', { hour12: false });
}

function formatDuration(milliseconds: number): string {
  if (milliseconds >= 60_000) return `${(milliseconds / 60_000).toFixed(1)} min`;
  if (milliseconds >= 1_000) return `${(milliseconds / 1_000).toFixed(2)} s`;
  return `${milliseconds.toFixed(milliseconds >= 100 ? 0 : 1)} ms`;
}

function severityType(severity: SlowQueryDiagnosticEntry['severity']): 'warning' | 'error' | 'default' {
  if (severity === 'critical') return 'error';
  if (severity === 'warning') return 'warning';
  return 'default';
}

async function copySql(sql: string): Promise<void> {
  await navigator.clipboard.writeText(sql);
  message.success('SQL 已复制');
}

async function load(): Promise<void> {
  if (!props.show) return;
  const sequence = ++requestSequence;
  loading.value = true;
  error.value = '';
  try {
    const query = { database: database.value || undefined, limit: limit.value };
    if (mode.value === 'slow') {
      const response = await fetchSlowQueries(auth.api, query);
      if (sequence === requestSequence) slowData.value = response;
    } else {
      const response = await fetchTopQueries(auth.api, query);
      if (sequence === requestSequence) topData.value = response;
    }
  } catch (reason) {
    if (sequence === requestSequence) {
      error.value = reason instanceof Error ? reason.message : '查询诊断加载失败';
    }
  } finally {
    if (sequence === requestSequence) loading.value = false;
  }
}

function updateDrawerWidth(): void {
  drawerWidth.value = Math.max(320, Math.min(920, window.innerWidth - 16));
  tableHeight.value = Math.max(320, window.innerHeight - 220);
}

watch(() => props.show, (visible) => {
  if (!visible) return;
  const nextDatabase = props.databases.includes(props.activeDatabase) ? props.activeDatabase : '';
  if (database.value === nextDatabase) void load();
  else database.value = nextDatabase;
});

watch([mode, database, limit], () => {
  if (props.show) void load();
});

onMounted(() => {
  updateDrawerWidth();
  window.addEventListener('resize', updateDrawerWidth);
});

onBeforeUnmount(() => {
  requestSequence++;
  window.removeEventListener('resize', updateDrawerWidth);
});
</script>

<style scoped>
.diagnostics-toolbar {
  display: grid;
  grid-template-columns: auto minmax(180px, 1fr) 104px 34px;
  gap: 8px;
  align-items: center;
}

.database-filter {
  min-width: 0;
}

.limit-filter {
  width: 104px;
}

.diagnostics-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 18px;
  min-height: 20px;
  margin: 14px 0 10px;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.diagnostics-table {
  border-top: 1px solid var(--sndb-border);
}

.diagnostics-table :deep(.sql-cell) {
  display: grid;
  min-width: 0;
  gap: 4px;
  padding: 3px 0;
}

.diagnostics-table :deep(.sql-cell code),
.diagnostics-table :deep(.sql-cell small) {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.diagnostics-table :deep(.sql-cell code) {
  color: var(--sndb-ink-strong);
  font-size: 12px;
}

.diagnostics-table :deep(.sql-cell small) {
  color: var(--sndb-ink-subtle);
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 10px;
}

.diagnostics-empty {
  margin-top: 96px;
}

@media (max-width: 640px) {
  .diagnostics-toolbar {
    grid-template-columns: minmax(0, 1fr) 96px 34px;
  }

  .diagnostics-toolbar > :first-child {
    grid-column: 1 / -1;
  }
}
</style>
