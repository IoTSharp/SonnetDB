<template>
  <main class="vector-workbench" data-testid="workbench-vector">
    <section class="vector-toolbar">
      <div class="vector-toolbar__identity">
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="warning" :bordered="false">Vector</n-tag>
          <n-text class="vector-toolbar__title">{{ activeIndexLabel || 'No vector index selected' }}</n-text>
          <n-tag v-if="activeIndex" size="tiny" :bordered="false">{{ activeIndex.kind }}</n-tag>
        </n-space>
        <n-text depth="3" class="vector-toolbar__meta">
          {{ targetDb || 'database' }} · dim {{ activeIndex?.dimension ?? '-' }} · {{ activeIndex?.metric ?? 'metric' }} · {{ formatStat(activeIndex?.rowCount) }} rows
        </n-text>
      </div>

      <div class="vector-toolbar__actions">
        <n-select
          v-model:value="selectedIndexKey"
          size="small"
          :options="indexOptions"
          :disabled="indexOptions.length === 0"
          class="vector-toolbar__index"
        />
        <n-select v-model:value="metric" size="small" :options="metricOptions" class="vector-toolbar__metric" />
        <n-input-number
          v-model:value="topK"
          size="small"
          :min="1"
          :max="100"
          :show-button="false"
          placeholder="Top-K"
          class="vector-toolbar__topk"
        />
        <n-button size="small" secondary :loading="loadingIndexes || searching" @click="$emit('refreshSchema')">
          Refresh
        </n-button>
        <n-button size="small" quaternary @click="historyVisible = true">History</n-button>
      </div>
    </section>

    <n-alert
      v-if="errorMsg"
      type="error"
      :title="errorMsg"
      closable
      class="vector-alert"
      @close="errorMsg = ''"
    />

    <section class="vector-stats">
      <article v-for="item in statItems" :key="item.label" class="vector-stat">
        <span>{{ item.label }}</span>
        <strong>{{ item.value }}</strong>
      </article>
    </section>

    <section class="vector-body">
      <aside class="vector-indexes">
        <div class="vector-panel-head">
          <div>
            <n-text class="vector-panel-head__title">Vector indexes</n-text>
            <n-text depth="3" class="vector-panel-head__meta">{{ filteredIndexes.length }} visible · {{ indexes.length }} total</n-text>
          </div>
        </div>
        <n-input
          v-model:value="indexFilter"
          size="small"
          clearable
          placeholder="Filter indexes"
          class="vector-index-filter"
        />
        <div class="vector-index-list">
          <button
            v-for="item in filteredIndexes"
            :key="indexKey(item)"
            type="button"
            class="vector-index-card"
            :class="{ 'is-active': indexKey(item) === activeIndexKey }"
            @click="selectIndex(item)"
          >
            <span>{{ item.measurement }}.{{ item.column }}</span>
            <small>dim {{ item.dimension ?? '-' }} · {{ item.metric }} · {{ formatStat(item.rowCount) }} rows</small>
          </button>
          <n-empty v-if="filteredIndexes.length === 0" description="No vector indexes found." />
        </div>
      </aside>

      <section class="vector-query-panel">
        <div class="vector-panel-head vector-panel-head--grid">
          <div>
            <n-text class="vector-panel-head__title">ANN search playground</n-text>
            <n-text depth="3" class="vector-panel-head__meta">{{ querySummary }}</n-text>
          </div>
          <n-space size="small" align="center" :wrap="true">
            <n-button size="small" secondary :disabled="!canSearch" :loading="searching" @click="runSearch">
              Search
            </n-button>
            <n-button size="small" quaternary :disabled="!queryVector.length" @click="copyVector">
              Copy vector
            </n-button>
          </n-space>
        </div>

        <section class="vector-query-editor">
          <n-tabs v-model:value="queryMode" type="segment" size="small">
            <n-tab name="raw" tab="Raw vector" />
            <n-tab name="text" tab="Text embed" />
          </n-tabs>

          <template v-if="queryMode === 'raw'">
            <n-input
              v-model:value="rawVectorText"
              type="textarea"
              :autosize="{ minRows: 5, maxRows: 10 }"
              placeholder="[0.12, -0.34, 0.57]"
              @blur="parseRawVector"
            />
            <n-space size="small" align="center" :wrap="true">
              <n-button size="small" secondary @click="parseRawVector">Parse</n-button>
              <n-button size="small" quaternary @click="fillZeroVector">Zero vector</n-button>
              <n-button size="small" quaternary @click="clearVector">Clear</n-button>
            </n-space>
          </template>

          <template v-else>
            <n-input
              v-model:value="embedText"
              type="textarea"
              :autosize="{ minRows: 5, maxRows: 10 }"
              placeholder="Text to embed with the configured Copilot embedding provider"
            />
            <n-space size="small" align="center" :wrap="true">
              <n-button size="small" secondary :disabled="!embedText.trim()" :loading="embedding" @click="embedTextToVector">
                Embed text
              </n-button>
              <n-button size="small" quaternary :disabled="!queryVector.length" @click="syncVectorToRaw">
                Use as raw
              </n-button>
            </n-space>
          </template>

          <div class="vector-filter-row">
            <n-input
              v-model:value="filterText"
              size="small"
              clearable
              placeholder="source = 'wiki' AND time >= 1700000000000"
            />
            <n-tag size="small" :bordered="false" :type="dimensionState.type">{{ dimensionState.label }}</n-tag>
          </div>

          <div class="vector-param-strip">
            <span>
              <small>Query dim</small>
              <strong>{{ queryVector.length || '-' }}</strong>
            </span>
            <span>
              <small>Index dim</small>
              <strong>{{ activeIndex?.dimension ?? '-' }}</strong>
            </span>
            <span>
              <small>Metric</small>
              <strong>{{ metric }}</strong>
            </span>
            <span>
              <small>Top-K</small>
              <strong>{{ topK ?? '-' }}</strong>
            </span>
          </div>
        </section>

        <n-data-table
          :columns="hitColumns"
          :data="hitRows"
          :loading="searching || loadingIndexes"
          :bordered="false"
          :single-line="false"
          :pagination="false"
          :row-key="rowKey"
          size="small"
          remote
          flex-height
          class="vector-grid"
        />
      </section>

      <aside class="vector-inspector">
        <div class="vector-panel-head">
          <div>
            <n-text class="vector-panel-head__title">Hit inspector</n-text>
            <n-text depth="3" class="vector-panel-head__meta">{{ selectedHit ? `rank ${selectedHit.rank}` : 'No hit selected' }}</n-text>
          </div>
          <n-tag v-if="selectedHit" size="tiny" :bordered="false">score {{ formatDistance(selectedHit.distance) }}</n-tag>
        </div>

        <template v-if="selectedHit">
          <div class="vector-detail-strip">
            <span>{{ selectedHit.timestampUtc }}</span>
            <span>{{ selectedHit.tagText || 'no tags' }}</span>
          </div>

          <section class="vector-kv-section">
            <div class="vector-section-title">
              <span>Tags</span>
              <n-button size="tiny" quaternary @click="copyText(selectedHit.tagText, 'Tags copied')">Copy</n-button>
            </div>
            <pre>{{ selectedHit.tagText || '-' }}</pre>
          </section>

          <section class="vector-kv-section">
            <div class="vector-section-title">
              <span>Fields</span>
              <n-button size="tiny" quaternary @click="copyText(selectedHit.fieldText, 'Fields copied')">Copy</n-button>
            </div>
            <pre>{{ selectedHit.fieldText || '-' }}</pre>
          </section>
        </template>
        <n-empty v-else description="Run a search and select a hit." />

        <section class="vector-index-params">
          <n-text class="vector-section-title vector-section-title--standalone">Index parameters</n-text>
          <div class="vector-param-list">
            <span v-for="param in activeIndex?.params ?? []" :key="param.key">
              <small>{{ param.key }}</small>
              <strong>{{ param.value }}</strong>
            </span>
            <n-empty v-if="!activeIndex || activeIndex.params.length === 0" description="No index parameters." />
          </div>
        </section>
      </aside>
    </section>

    <WorkbenchResultPanel
      class="vector-result"
      title="Vector search result"
      :sql="latestCommand"
      :result="latestResult"
      :ran-once="ranOnce"
      :summary="resultSummary"
      :file-name="`${targetDb}_${activeIndex?.measurement ?? 'vector'}_${activeIndex?.column ?? 'search'}`"
      empty-description="No vector hits yet."
      @clear-error="latestResult = null"
    />

    <WorkbenchHistoryDrawer
      v-model:show="historyVisible"
      :active-database="targetDb"
      @select="openHistoryEntry"
    />
  </main>
</template>

<script setup lang="ts">
import { computed, h, ref, watch } from 'vue';
import {
  NAlert,
  NButton,
  NDataTable,
  NEmpty,
  NInput,
  NInputNumber,
  NSelect,
  NSpace,
  NTab,
  NTabs,
  NTag,
  NText,
  useMessage,
  type DataTableColumns,
  type SelectOption,
} from 'naive-ui';
import type { VectorIndexStat } from '@/api/management';
import type { SqlResultSet } from '@/api/sql';
import {
  embedVectorText,
  searchVectorPreview,
  type KeyValueInfo,
  type VectorSearchPreviewHit,
} from '@/api/vector';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import WorkbenchResultPanel from '@/components/WorkbenchResultPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import {
  useWorkbenchHistoryStore,
  type WorkbenchHistoryEntry,
} from '@/stores/workbenchHistory';

const props = withDefaults(defineProps<{
  targetDb: string;
  index: VectorIndexStat | null;
  indexes?: VectorIndexStat[];
  loading?: boolean;
}>(), {
  indexes: () => [],
  loading: false,
});

const emit = defineEmits<{
  selectIndex: [index: VectorIndexStat];
  refreshSchema: [];
}>();

interface HitRow {
  key: string;
  rank: number;
  timestampUtc: number;
  distance: number;
  tagText: string;
  fieldText: string;
  raw: VectorSearchPreviewHit;
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();

const queryMode = ref<'raw' | 'text'>('raw');
const rawVectorText = ref('[1, 0, 0]');
const embedText = ref('');
const queryVector = ref<number[]>([]);
const filterText = ref('');
const metric = ref('cosine');
const topK = ref<number | null>(10);
const indexFilter = ref('');
const searching = ref(false);
const embedding = ref(false);
const errorMsg = ref('');
const hits = ref<VectorSearchPreviewHit[]>([]);
const selectedKey = ref('');
const latestResult = ref<SqlResultSet | null>(null);
const latestCommand = ref('');
const ranOnce = ref(false);
const historyVisible = ref(false);

const loadingIndexes = computed(() => props.loading);
const indexes = computed(() => props.indexes);

const activeIndex = computed(() => {
  if (props.index) return props.index;
  return indexes.value[0] ?? null;
});

const activeIndexKey = computed(() => activeIndex.value ? indexKey(activeIndex.value) : '');
const activeIndexLabel = computed(() =>
  activeIndex.value ? `${activeIndex.value.measurement}.${activeIndex.value.column}` : '');

const selectedIndexKey = computed({
  get: () => activeIndexKey.value,
  set: (value: string) => {
    const next = indexes.value.find((item) => indexKey(item) === value);
    if (next) selectIndex(next);
  },
});

const indexOptions = computed<SelectOption[]>(() =>
  indexes.value.map((item) => ({
    label: `${item.measurement}.${item.column}`,
    value: indexKey(item),
  })));

const filteredIndexes = computed(() => {
  const keyword = indexFilter.value.trim().toLowerCase();
  const sorted = [...indexes.value].sort((a, b) => indexKey(a).localeCompare(indexKey(b)));
  if (!keyword) return sorted;
  return sorted.filter((item) =>
    item.measurement.toLowerCase().includes(keyword)
    || item.column.toLowerCase().includes(keyword)
    || item.kind.toLowerCase().includes(keyword)
    || item.metric.toLowerCase().includes(keyword));
});

const metricOptions = computed<SelectOption[]>(() => {
  const values = new Set(['cosine', 'l2', 'inner_product']);
  if (activeIndex.value?.metric) values.add(activeIndex.value.metric);
  return [...values].map((value) => ({ label: value, value }));
});

const statItems = computed(() => [
  { label: 'Indexes', value: formatStat(indexes.value.length) },
  { label: 'Rows', value: formatStat(activeIndex.value?.rowCount) },
  { label: 'Dimension', value: formatStat(activeIndex.value?.dimension) },
  { label: 'Kind', value: activeIndex.value?.kind ?? '-' },
  { label: 'Metric', value: activeIndex.value?.metric ?? '-' },
  { label: 'Hits', value: formatStat(hits.value.length) },
]);

const querySummary = computed(() => {
  if (!activeIndex.value) return 'No vector index selected.';
  if (queryVector.value.length === 0) return 'Query vector empty.';
  return `${activeIndexLabel.value} · dim ${queryVector.value.length} · ${filterText.value.trim() || 'no filter'}`;
});

const dimensionState = computed<{ type: 'default' | 'success' | 'warning' | 'error'; label: string }>(() => {
  const indexDim = activeIndex.value?.dimension ?? null;
  if (queryVector.value.length === 0) return { type: 'default', label: 'empty query' };
  if (!indexDim) return { type: 'warning', label: `${queryVector.value.length} dims` };
  if (queryVector.value.length === indexDim) return { type: 'success', label: 'dimension match' };
  return { type: 'error', label: `expected ${indexDim}` };
});

const canSearch = computed(() =>
  Boolean(props.targetDb && activeIndex.value && queryVector.value.length > 0 && dimensionState.value.type !== 'error'));

const hitRows = computed<HitRow[]>(() =>
  hits.value.map((hit, index) => ({
    key: `${hit.timestampUtc}:${index}`,
    rank: index + 1,
    timestampUtc: hit.timestampUtc,
    distance: hit.distance,
    tagText: formatPairs(hit.tags),
    fieldText: formatPairs(hit.fields),
    raw: hit,
  })));

const selectedHit = computed(() =>
  hitRows.value.find((row) => row.key === selectedKey.value) ?? hitRows.value[0] ?? null);

const resultSummary = computed(() => {
  if (!latestResult.value) return querySummary.value;
  if (latestResult.value.error) return latestResult.value.error.message;
  if (latestResult.value.end) return `${latestResult.value.end.rowCount} hits · ${latestResult.value.end.elapsedMs.toFixed(2)} ms`;
  return 'Ready';
});

const hitColumns = computed<DataTableColumns<HitRow>>(() => [
  {
    title: '#',
    key: 'rank',
    width: 62,
    render: (row) => h('button', {
      type: 'button',
      class: ['vector-rank-button', row.key === selectedHit.value?.key ? 'is-active' : ''],
      onClick: () => { selectedKey.value = row.key; },
    }, row.rank.toString()),
  },
  {
    title: 'Time',
    key: 'timestampUtc',
    width: 160,
    render: (row) => h('code', row.timestampUtc.toString()),
  },
  {
    title: 'Distance',
    key: 'distance',
    width: 132,
    sorter: 'default',
    render: (row) => h('code', formatDistance(row.distance)),
  },
  {
    title: 'Tags',
    key: 'tags',
    minWidth: 180,
    ellipsis: { tooltip: true },
    render: (row) => h('span', { class: 'vector-preview-cell' }, row.tagText || '-'),
  },
  {
    title: 'Fields',
    key: 'fields',
    minWidth: 240,
    ellipsis: { tooltip: true },
    render: (row) => h('span', { class: 'vector-preview-cell' }, row.fieldText || '-'),
  },
]);

function indexKey(index: VectorIndexStat): string {
  return `${index.measurement}:${index.column}`;
}

function selectIndex(index: VectorIndexStat): void {
  emit('selectIndex', index);
  metric.value = index.metric || 'cosine';
}

async function runSearch(): Promise<void> {
  if (!canSearch.value || !activeIndex.value) return;
  searching.value = true;
  errorMsg.value = '';
  const started = performance.now();
  const command = buildCommand();
  try {
    const response = await searchVectorPreview(auth.api, props.targetDb, {
      measurement: activeIndex.value.measurement,
      column: activeIndex.value.column,
      query: queryVector.value,
      topK: topK.value ?? 10,
      metric: metric.value,
      filter: filterText.value.trim() || null,
    });
    const elapsed = performance.now() - started;
    hits.value = Array.isArray(response.hits) ? response.hits : [];
    selectedKey.value = hitRows.value[0]?.key ?? '';
    latestCommand.value = command;
    latestResult.value = resultFromHits(hits.value, elapsed);
    ranOnce.value = true;
    recordHistory('success', command, `${hits.value.length} hits`, hits.value.length, elapsed);
  } catch (error) {
    const elapsed = performance.now() - started;
    const msg = errorToMessage(error, '向量检索失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult(msg);
    ranOnce.value = true;
    recordHistory('error', command, msg, 0, elapsed);
  } finally {
    searching.value = false;
  }
}

async function embedTextToVector(): Promise<void> {
  const text = embedText.value.trim();
  if (!text) return;
  embedding.value = true;
  errorMsg.value = '';
  try {
    const response = await embedVectorText(auth.api, props.targetDb, text);
    queryVector.value = response.vector;
    rawVectorText.value = vectorToText(response.vector);
    queryMode.value = 'raw';
    message.success(`Embedded ${response.dimension} dimensions`);
  } catch (error) {
    errorMsg.value = errorToMessage(error, '生成 embedding 失败');
  } finally {
    embedding.value = false;
  }
}

function parseRawVector(): void {
  const parsed = parseVector(rawVectorText.value);
  if (!parsed.ok) {
    errorMsg.value = parsed.message;
    queryVector.value = [];
    return;
  }
  errorMsg.value = '';
  queryVector.value = parsed.vector;
  rawVectorText.value = vectorToText(parsed.vector);
}

function fillZeroVector(): void {
  const dim = activeIndex.value?.dimension ?? 3;
  queryVector.value = Array.from({ length: dim }, () => 0);
  rawVectorText.value = vectorToText(queryVector.value);
}

function clearVector(): void {
  queryVector.value = [];
  rawVectorText.value = '';
}

function syncVectorToRaw(): void {
  rawVectorText.value = vectorToText(queryVector.value);
  queryMode.value = 'raw';
}

async function copyVector(): Promise<void> {
  await copyText(vectorToText(queryVector.value), 'Vector copied');
}

async function copyText(text: string, success: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    message.success(success);
  } catch {
    message.warning(text);
  }
}

function openHistoryEntry(entry: WorkbenchHistoryEntry): void {
  latestCommand.value = entry.command;
}

function rowKey(row: HitRow): string {
  return row.key;
}

function resultFromHits(items: VectorSearchPreviewHit[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['rank', 'time', 'distance', 'tags', 'fields'],
    rows: items.map((hit, index) => [
      index + 1,
      hit.timestampUtc,
      hit.distance,
      formatPairs(hit.tags),
      formatPairs(hit.fields),
    ]),
    end: {
      type: 'end',
      rowCount: items.length,
      recordsAffected: -1,
      elapsedMs,
    },
    error: null,
    hasColumns: true,
  };
}

function errorResult(messageText: string): SqlResultSet {
  return {
    columns: [],
    rows: [],
    end: null,
    error: { type: 'error', code: 'vector_error', message: messageText },
    hasColumns: false,
  };
}

function buildCommand(): string {
  const idx = activeIndex.value;
  if (!idx) return 'VECTOR SEARCH';
  const filter = filterText.value.trim();
  const query = queryVector.value.length > 8
    ? `[${queryVector.value.slice(0, 8).map(formatNumber).join(', ')}, ... ${queryVector.value.length} dims]`
    : vectorToText(queryVector.value);
  return [
    `SELECT * FROM knn(${idx.measurement}, ${idx.column}, ${query}, ${topK.value ?? 10}, '${metric.value}')`,
    filter ? `WHERE ${filter}` : '',
  ].filter(Boolean).join('\n');
}

function recordHistory(
  status: 'success' | 'error',
  command: string,
  summary: string,
  rowCount: number,
  elapsedMs: number,
): void {
  history.record({
    kind: 'query',
    status,
    title: 'Vector search preview',
    target: activeIndexLabel.value,
    database: props.targetDb,
    connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name,
    model: 'vector',
    action: queryMode.value === 'text' ? 'embed_search' : 'search',
    command,
    summary,
    rowCount,
    recordsAffected: -1,
    elapsedMs,
  });
}

function parseVector(text: string): { ok: true; vector: number[] } | { ok: false; message: string } {
  const trimmed = text.trim();
  if (!trimmed) return { ok: false, message: 'Query vector is empty.' };
  let source = trimmed;
  if (source.startsWith('[')) {
    try {
      const parsed = JSON.parse(source) as unknown;
      if (!Array.isArray(parsed)) return { ok: false, message: 'Vector JSON must be an array.' };
      const vector = parsed.map((value) => Number(value));
      if (vector.some((value) => !Number.isFinite(value))) {
        return { ok: false, message: 'Vector contains a non-numeric component.' };
      }
      return { ok: true, vector };
    } catch (error) {
      return { ok: false, message: error instanceof Error ? error.message : 'Invalid vector JSON.' };
    }
  }
  source = source.replace(/^\[/, '').replace(/\]$/, '');
  const parts = source.split(/[\s,]+/g).map((part) => part.trim()).filter(Boolean);
  const vector = parts.map((part) => Number(part));
  if (vector.length === 0) return { ok: false, message: 'Query vector is empty.' };
  if (vector.some((value) => !Number.isFinite(value))) {
    return { ok: false, message: 'Vector contains a non-numeric component.' };
  }
  return { ok: true, vector };
}

function vectorToText(vector: number[]): string {
  return `[${vector.map(formatNumber).join(', ')}]`;
}

function formatNumber(value: number): string {
  if (!Number.isFinite(value)) return '0';
  const abs = Math.abs(value);
  if (abs !== 0 && (abs < 0.0001 || abs >= 100000)) return value.toExponential(6);
  return Number(value.toPrecision(8)).toString();
}

function formatPairs(items?: KeyValueInfo[] | null): string {
  if (!items || items.length === 0) return '';
  return items.map((item) => `${item.key}=${item.value}`).join(' · ');
}

function formatDistance(value: number): string {
  if (!Number.isFinite(value)) return '-';
  return Math.abs(value) < 0.000001 ? value.toExponential(3) : value.toFixed(6);
}

function formatStat(value?: number | null): string {
  return typeof value === 'number' && Number.isFinite(value) ? value.toLocaleString() : '-';
}

function errorToMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const response = (error as { response?: { data?: unknown; status?: number } }).response;
    if (response?.data && typeof response.data === 'object') {
      const data = response.data as Record<string, unknown>;
      if (typeof data.message === 'string') return data.message;
      if (typeof data.error === 'string') return data.error;
    }
    if (typeof (error as { message?: unknown }).message === 'string') {
      return (error as { message: string }).message;
    }
  }
  return fallback;
}

watch(activeIndex, (index) => {
  metric.value = index?.metric || 'cosine';
  if (queryVector.value.length === 0 && index?.dimension && index.dimension <= 8) {
    fillZeroVector();
  }
}, { immediate: true });

watch(() => props.targetDb, () => {
  hits.value = [];
  latestResult.value = null;
  ranOnce.value = false;
});
</script>

<style scoped>
.vector-workbench {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.vector-toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fffaf3;
}

.vector-toolbar__identity {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.vector-toolbar__title {
  color: var(--sndb-ink-strong);
  font-size: 15px;
  font-weight: 800;
}

.vector-toolbar__meta,
.vector-panel-head__meta {
  font-size: 12px;
}

.vector-toolbar__actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.vector-toolbar__index {
  width: 220px;
}

.vector-toolbar__metric {
  width: 132px;
}

.vector-toolbar__topk {
  width: 86px;
}

.vector-alert {
  margin: 10px 12px 0;
}

.vector-stats {
  display: grid;
  grid-template-columns: repeat(6, minmax(110px, 1fr));
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.vector-stat {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
  padding: 9px 12px;
  border-right: 1px solid rgba(15, 23, 42, 0.08);
}

.vector-stat span,
.vector-param-strip small,
.vector-param-list small {
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.vector-stat strong,
.vector-param-strip strong,
.vector-param-list strong {
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 15px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.vector-body {
  display: grid;
  flex: 1;
  min-height: 420px;
  grid-template-columns: 250px minmax(440px, 1fr) 350px;
  min-width: 0;
  overflow: hidden;
}

.vector-indexes,
.vector-query-panel,
.vector-inspector {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.vector-indexes {
  border-right: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfcfe;
}

.vector-query-panel {
  background: #fff;
}

.vector-inspector {
  border-left: 1px solid rgba(15, 23, 42, 0.08);
  background: #fffdfa;
}

.vector-panel-head {
  display: flex;
  flex: 0 0 auto;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.vector-panel-head--grid {
  align-items: center;
}

.vector-panel-head__title,
.vector-section-title {
  display: block;
  color: var(--sndb-ink-strong);
  font-weight: 800;
}

.vector-index-filter {
  flex: 0 0 auto;
  margin: 8px;
  width: calc(100% - 16px);
}

.vector-index-list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-height: 0;
  overflow: auto;
  padding: 0 8px 8px;
}

.vector-index-card,
.vector-rank-button {
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.vector-index-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  width: 100%;
  min-width: 0;
  padding: 8px;
  border-left: 2px solid rgba(176, 92, 24, 0.45);
  border-radius: 6px;
  text-align: left;
}

.vector-index-card:hover,
.vector-index-card.is-active,
.vector-rank-button:hover,
.vector-rank-button.is-active {
  background: rgba(176, 92, 24, 0.09);
}

.vector-index-card.is-active {
  border-left-color: rgba(176, 92, 24, 0.9);
}

.vector-index-card span {
  width: 100%;
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.vector-index-card small {
  color: var(--sndb-ink-soft);
  font-size: 11px;
}

.vector-query-editor {
  display: flex;
  flex: 0 0 auto;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.vector-filter-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 8px;
  align-items: center;
}

.vector-param-strip,
.vector-param-list {
  display: grid;
  gap: 8px;
}

.vector-param-strip {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.vector-param-strip span,
.vector-param-list span {
  min-width: 0;
  padding: 7px 8px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fbfcfe;
}

.vector-param-strip small,
.vector-param-list small,
.vector-param-strip strong,
.vector-param-list strong {
  display: block;
}

.vector-grid {
  flex: 1;
  min-height: 0;
}

.vector-grid :deep(.n-data-table-base-table-body) {
  min-height: 260px;
}

.vector-rank-button {
  min-width: 32px;
  padding: 2px 5px;
  border-radius: 4px;
  color: #9a4f10;
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  font-weight: 800;
}

.vector-preview-cell {
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  color: #345;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.vector-detail-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 9px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.vector-detail-strip span {
  padding: 2px 7px;
  border-radius: 999px;
  background: rgba(176, 92, 24, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
}

.vector-kv-section,
.vector-index-params {
  padding: 10px 12px 0;
}

.vector-section-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 6px;
}

.vector-section-title--standalone {
  margin-bottom: 8px;
}

.vector-kv-section pre {
  max-height: 150px;
  margin: 0;
  overflow: auto;
  padding: 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
  color: #24384f;
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.vector-param-list {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.vector-result {
  flex: 0 0 240px;
  min-height: 220px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

@media (max-width: 1360px) {
  .vector-body {
    grid-template-columns: 230px minmax(420px, 1fr);
  }

  .vector-inspector {
    grid-column: 1 / -1;
    border-top: 1px solid rgba(15, 23, 42, 0.08);
    border-left: 0;
  }
}

@media (max-width: 980px) {
  .vector-toolbar,
  .vector-panel-head--grid {
    flex-direction: column;
    align-items: stretch;
  }

  .vector-body {
    grid-template-columns: 1fr;
    overflow: visible;
  }

  .vector-indexes,
  .vector-inspector {
    border-right: 0;
    border-left: 0;
  }

  .vector-stats,
  .vector-param-strip,
  .vector-param-list,
  .vector-filter-row {
    grid-template-columns: 1fr;
  }

  .vector-toolbar__index,
  .vector-toolbar__metric,
  .vector-toolbar__topk {
    width: 100%;
  }
}
</style>
