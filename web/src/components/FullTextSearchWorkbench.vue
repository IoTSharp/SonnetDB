<template>
  <main class="fulltext-workbench" data-testid="workbench-fulltext">
    <section class="fulltext-toolbar">
      <div class="fulltext-toolbar__identity">
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="default" :bordered="false">FullText</n-tag>
          <n-text class="fulltext-toolbar__title">{{ activeIndexLabel || 'No fulltext index selected' }}</n-text>
          <n-tag v-if="activeIndex" size="tiny" :bordered="false">{{ activeIndex.tokenizer }}</n-tag>
        </n-space>
        <n-text depth="3" class="fulltext-toolbar__meta">
          {{ targetDb || 'database' }} · {{ formatStat(activeIndex?.documentCount) }} docs · {{ formatStat(activeIndex?.termCount) }} terms
        </n-text>
      </div>

      <div class="fulltext-toolbar__actions">
        <n-select
          v-model:value="selectedIndexKey"
          size="small"
          :options="indexOptions"
          :disabled="indexOptions.length === 0"
          class="fulltext-toolbar__index"
        />
        <n-select v-model:value="field" size="small" :options="fieldOptions" class="fulltext-toolbar__field" />
        <n-input-number
          v-model:value="topK"
          size="small"
          :min="1"
          :max="100"
          :show-button="false"
          placeholder="Top-K"
          class="fulltext-toolbar__topk"
        />
        <n-button size="small" secondary :loading="loadingIndexes || searching" @click="$emit('refreshSchema')">
          Refresh
        </n-button>
        <n-button size="small" secondary :disabled="!activeIndex" @click="stageRebuild">
          Rebuild
        </n-button>
        <n-button size="small" quaternary @click="historyVisible = true">History</n-button>
      </div>
    </section>

    <WorkbenchSectionTabs
      :model-value="activeView"
      :items="fulltextSections"
      aria-label="全文工作区"
      @update:model-value="activeView = $event as FullTextView"
    />

    <WriteApprovalPanel
      v-if="previewPlan"
      :plan="previewPlan"
      :busy="confirmBusy"
      @cancel="clearPreviewPlan"
      @confirm="confirmRebuild"
    />

    <n-alert
      v-if="errorMsg"
      type="error"
      :title="errorMsg"
      closable
      class="fulltext-alert"
      @close="errorMsg = ''"
    />

    <section class="fulltext-stats">
      <article v-for="item in statItems" :key="item.label" class="fulltext-stat">
        <span>{{ item.label }}</span>
        <strong>{{ item.value }}</strong>
      </article>
    </section>

    <section class="fulltext-body" :class="`is-${activeView}`">
      <aside v-if="activeView !== 'analyzer'" class="fulltext-indexes">
        <div class="fulltext-panel-head">
          <div>
            <n-text class="fulltext-panel-head__title">FullText indexes</n-text>
            <n-text depth="3" class="fulltext-panel-head__meta">{{ filteredIndexes.length }} visible · {{ indexes.length }} total</n-text>
          </div>
        </div>
        <n-input
          v-model:value="indexFilter"
          size="small"
          clearable
          placeholder="Filter indexes"
          class="fulltext-index-filter"
        />
        <div class="fulltext-index-list">
          <button
            v-for="item in filteredIndexes"
            :key="indexKey(item)"
            type="button"
            class="fulltext-index-card"
            :class="{ 'is-active': indexKey(item) === activeIndexKey }"
            @click="selectIndex(item)"
          >
            <span>{{ item.collection }}.{{ item.name }}</span>
            <small>{{ item.tokenizer }} · {{ item.fields.join(', ') || 'fields n/a' }}</small>
          </button>
          <n-empty v-if="filteredIndexes.length === 0" description="No fulltext indexes found." />
        </div>
      </aside>

      <section v-if="activeView === 'search'" class="fulltext-search-panel">
        <div class="fulltext-panel-head fulltext-panel-head--grid">
          <div>
            <n-text class="fulltext-panel-head__title">BM25 search playground</n-text>
            <n-text depth="3" class="fulltext-panel-head__meta">{{ querySummary }}</n-text>
          </div>
          <n-space size="small" align="center" :wrap="true">
            <n-select v-model:value="mode" size="small" :options="modeOptions" class="fulltext-mode-select" />
            <n-select v-model:value="queryKind" size="small" :options="queryKindOptions" class="fulltext-kind-select" />
            <n-button size="small" secondary :disabled="!canSearch" :loading="searching" @click="runSearch">
              Search
            </n-button>
          </n-space>
        </div>

        <section class="fulltext-query-editor">
          <n-input
            v-model:value="queryText"
            type="textarea"
            :autosize="{ minRows: 4, maxRows: 8 }"
            placeholder="pump alarm"
            @keydown.ctrl.enter.prevent="runSearch"
          />

          <div class="fulltext-builder">
            <n-input
              v-model:value="builderText"
              size="small"
              clearable
              placeholder="Builder terms or phrase"
              @keydown.enter="applyBuilder('all')"
            />
            <n-space size="small" align="center" :wrap="true">
              <n-button size="small" quaternary @click="applyBuilder('all')">All</n-button>
              <n-button size="small" quaternary @click="applyBuilder('any')">Any</n-button>
              <n-button size="small" quaternary @click="applyBuilder('phrase')">Phrase</n-button>
              <n-button size="small" quaternary @click="applyFuzzyBuilder">Fuzzy</n-button>
            </n-space>
          </div>

          <div class="fulltext-param-strip">
            <span>
              <small>Collection</small>
              <strong>{{ activeIndex?.collection ?? '-' }}</strong>
            </span>
            <span>
              <small>Field</small>
              <strong>{{ effectiveField }}</strong>
            </span>
            <span>
              <small>Mode</small>
              <strong>{{ mode }}</strong>
            </span>
            <span>
              <small>Kind</small>
              <strong>{{ queryKind }}</strong>
            </span>
          </div>
        </section>

        <n-data-table
          :columns="hitColumns"
          :data="pagedRows"
          :loading="searching || loadingIndexes"
          :bordered="false"
          :single-line="false"
          :pagination="false"
          :row-key="rowKey"
          size="small"
          remote
          flex-height
          class="fulltext-grid"
        />

        <footer class="fulltext-pager">
          <span>{{ pageSummary }}</span>
          <n-space size="small" align="center">
            <n-select v-model:value="pageSize" size="small" :options="pageSizeOptions" class="fulltext-page-size" />
            <n-button size="small" secondary :disabled="page <= 1" @click="page -= 1">Previous</n-button>
            <n-button size="small" secondary :disabled="page >= pageCount" @click="page += 1">Next</n-button>
          </n-space>
        </footer>
      </section>

      <aside class="fulltext-inspector">
        <div class="fulltext-panel-head">
          <div>
            <n-text class="fulltext-panel-head__title">
              {{ activeView === 'search' ? '命中详情' : activeView === 'analyzer' ? 'Analyzer 预览' : '索引详情' }}
            </n-text>
            <n-text depth="3" class="fulltext-panel-head__meta">
              {{ activeView === 'search' ? (selectedHit ? selectedHit.documentId : '尚未选择命中项') : activeIndexLabel }}
            </n-text>
          </div>
          <n-tag v-if="selectedHit" size="tiny" :bordered="false">score {{ formatScore(selectedHit.score) }}</n-tag>
        </div>

        <template v-if="activeView === 'search' && selectedHit">
          <div class="fulltext-detail-strip">
            <span>rank {{ selectedHit.rank }}</span>
            <span>version {{ selectedHit.version ?? '-' }}</span>
            <span>{{ selectedHit.fieldName }}</span>
          </div>

          <section class="fulltext-section">
            <div class="fulltext-section-title">
              <span>Highlight</span>
              <n-button size="tiny" quaternary @click="copyText(selectedHit.snippetText, 'Snippet copied')">Copy</n-button>
            </div>
            <p class="fulltext-snippet">
              <template v-for="(part, index) in selectedHit.snippetParts" :key="`${selectedHit.key}:${index}`">
                <mark v-if="part.hit">{{ part.text }}</mark>
                <span v-else>{{ part.text }}</span>
              </template>
            </p>
          </section>

          <section class="fulltext-section fulltext-document">
            <div class="fulltext-section-title">
              <span>Document</span>
              <n-button size="tiny" quaternary @click="copyText(selectedHit.rawJson, 'Document copied')">Copy</n-button>
            </div>
            <pre>{{ selectedHit.rawJson }}</pre>
          </section>
        </template>
        <n-empty v-else-if="activeView === 'search'" description="执行检索并选择一条命中记录。" />

        <section v-if="activeView === 'analyzer'" class="fulltext-analyzer">
          <n-text class="fulltext-section-title fulltext-section-title--standalone">Analyzer preview</n-text>
          <div class="fulltext-analyzer-row">
            <n-select v-model:value="analyzeTokenizer" size="small" :options="tokenizerOptions" />
            <n-button size="small" secondary :loading="analyzing" @click="runAnalyze(false)">Analyze</n-button>
            <n-button size="small" quaternary :disabled="!queryText.trim()" @click="runAnalyze(true)">
              Query
            </n-button>
          </div>
          <n-input
            v-model:value="analyzeText"
            type="textarea"
            :autosize="{ minRows: 3, maxRows: 6 }"
            placeholder="Text to analyze"
          />
          <div class="fulltext-token-list">
            <span v-for="token in analyzeTokens" :key="`${token.text}:${token.startOffset}:${token.endOffset}`">
              <strong>{{ token.text }}</strong>
              <small>{{ token.startOffset }}-{{ token.endOffset }} · +{{ token.positionIncrement }}</small>
            </span>
            <n-empty v-if="analyzeTokens.length === 0" description="No tokens yet." />
          </div>
        </section>

        <section v-if="activeView === 'index'" class="fulltext-index-detail">
          <dl>
            <div><dt>Collection</dt><dd>{{ activeIndex?.collection ?? '-' }}</dd></div>
            <div><dt>Index</dt><dd>{{ activeIndex?.name ?? '-' }}</dd></div>
            <div><dt>Tokenizer</dt><dd>{{ activeIndex?.tokenizer ?? '-' }}</dd></div>
            <div><dt>Documents</dt><dd>{{ formatStat(activeIndex?.documentCount) }}</dd></div>
            <div><dt>Terms</dt><dd>{{ formatStat(activeIndex?.termCount) }}</dd></div>
            <div><dt>Fields</dt><dd>{{ activeIndex?.fields.join(', ') || '-' }}</dd></div>
          </dl>
          <n-button type="primary" :disabled="!activeIndex" @click="stageRebuild">暂存重建索引</n-button>
        </section>
      </aside>
    </section>

    <WorkbenchResultPanel
      class="fulltext-result"
      title="FullText search result"
      :sql="latestCommand"
      :result="latestResult"
      :ran-once="ranOnce"
      :summary="resultSummary"
      :file-name="`${targetDb}_${activeIndex?.collection ?? 'fulltext'}_${activeIndex?.name ?? 'search'}`"
      empty-description="No fulltext search result yet."
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
  NTag,
  NText,
  useMessage,
  type DataTableColumns,
  type SelectOption,
} from 'naive-ui';
import type { FullTextIndexStat } from '@/api/management';
import type { DocumentItemResponse } from '@/api/documents';
import { findDocuments } from '@/api/documents';
import {
  analyzeFullText,
  searchFullTextPreview,
  type FullTextQueryKind,
  type FullTextSearchMode,
  type FullTextSearchPreviewHit,
  type FullTextTokenInfo,
} from '@/api/fulltext';
import type { SqlResultSet } from '@/api/sql';
import { quote } from '@/api/sql';
import {
  runMaintenance,
  type MaintenanceResponse,
} from '@/api/schema';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import WorkbenchResultPanel from '@/components/WorkbenchResultPanel.vue';
import WorkbenchSectionTabs, { type WorkbenchSectionTab } from '@/components/WorkbenchSectionTabs.vue';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import {
  useWorkbenchHistoryStore,
  type WorkbenchHistoryEntry,
} from '@/stores/workbenchHistory';
import { createWriteApprovalPlan, type WriteApprovalPlan } from '@/utils/writeApproval';
import { formatSqlIdentifier } from '@/utils/sqlWorkbench';

const props = withDefaults(defineProps<{
  targetDb: string;
  index: FullTextIndexStat | null;
  indexes?: FullTextIndexStat[];
  loading?: boolean;
}>(), {
  indexes: () => [],
  loading: false,
});

const emit = defineEmits<{
  selectIndex: [index: FullTextIndexStat];
  refreshSchema: [];
}>();

interface HighlightPart {
  text: string;
  hit: boolean;
}

interface HitRow {
  key: string;
  rank: number;
  documentId: string;
  score: number;
  fieldName: string;
  fieldText: string;
  snippetText: string;
  snippetParts: HighlightPart[];
  rawJson: string;
  version?: number;
  raw: FullTextSearchPreviewHit;
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();
type FullTextView = 'search' | 'analyzer' | 'index';
const activeView = ref<FullTextView>('search');
const fulltextSections: WorkbenchSectionTab[] = [
  { key: 'search', label: '全文检索' },
  { key: 'analyzer', label: 'Analyzer' },
  { key: 'index', label: '索引' },
];

const queryText = ref('pump alarm');
const builderText = ref('pump alarm');
const mode = ref<FullTextSearchMode>('exact');
const queryKind = ref<FullTextQueryKind>('all');
const field = ref('*');
const topK = ref<number | null>(20);
const page = ref(1);
const pageSize = ref(10);
const indexFilter = ref('');
const searching = ref(false);
const analyzing = ref(false);
const confirmBusy = ref(false);
const errorMsg = ref('');
const hits = ref<FullTextSearchPreviewHit[]>([]);
const documentsById = ref<Record<string, DocumentItemResponse>>({});
const selectedId = ref('');
const analyzeText = ref('Pump alarm in north station');
const analyzeTokenizer = ref('unicode');
const analyzeTokens = ref<FullTextTokenInfo[]>([]);
const searchTokens = ref<string[]>([]);
const latestResult = ref<SqlResultSet | null>(null);
const latestCommand = ref('');
const ranOnce = ref(false);
const historyVisible = ref(false);
const previewPlan = ref<WriteApprovalPlan | null>(null);
const pendingRebuild = ref<FullTextIndexStat | null>(null);

const loadingIndexes = computed(() => props.loading);
const indexes = computed(() => props.indexes);

const activeIndex = computed(() => props.index ?? indexes.value[0] ?? null);
const activeIndexKey = computed(() => activeIndex.value ? indexKey(activeIndex.value) : '');
const activeIndexLabel = computed(() =>
  activeIndex.value ? `${activeIndex.value.collection}.${activeIndex.value.name}` : '');

const selectedIndexKey = computed({
  get: () => activeIndexKey.value,
  set: (value: string) => {
    const next = indexes.value.find((item) => indexKey(item) === value);
    if (next) selectIndex(next);
  },
});

const indexOptions = computed<SelectOption[]>(() =>
  indexes.value.map((item) => ({
    label: `${item.collection}.${item.name}`,
    value: indexKey(item),
  })));

const fieldOptions = computed<SelectOption[]>(() => {
  const fields = activeIndex.value?.fields ?? [];
  return [
    { label: 'All indexed fields (*)', value: '*' },
    ...fields.map((value) => ({ label: value, value })),
  ];
});

const effectiveField = computed(() => {
  if (!activeIndex.value) return '*';
  return field.value === '*' || activeIndex.value.fields.includes(field.value) ? field.value : '*';
});

const filteredIndexes = computed(() => {
  const keyword = indexFilter.value.trim().toLowerCase();
  const sorted = [...indexes.value].sort((a, b) => indexKey(a).localeCompare(indexKey(b)));
  if (!keyword) return sorted;
  return sorted.filter((item) =>
    item.collection.toLowerCase().includes(keyword)
    || item.name.toLowerCase().includes(keyword)
    || item.tokenizer.toLowerCase().includes(keyword)
    || item.fields.some((name) => name.toLowerCase().includes(keyword)));
});

const modeOptions: SelectOption[] = [
  { label: 'exact', value: 'exact' },
  { label: 'fuzzy', value: 'fuzzy' },
];

const queryKindOptions: SelectOption[] = [
  { label: 'all terms', value: 'all' },
  { label: 'any term', value: 'any' },
  { label: 'phrase', value: 'phrase' },
];

const pageSizeOptions: SelectOption[] = [
  { label: '10 rows', value: 10 },
  { label: '25 rows', value: 25 },
  { label: '50 rows', value: 50 },
];

const tokenizerOptions = computed<SelectOption[]>(() => {
  const names = new Set(['unicode', 'cjk', 'jieba']);
  if (activeIndex.value?.tokenizer) names.add(activeIndex.value.tokenizer);
  return [...names].map((value) => ({ label: value, value }));
});

const statItems = computed(() => [
  { label: 'Indexes', value: formatStat(indexes.value.length) },
  { label: 'Docs', value: formatStat(activeIndex.value?.documentCount) },
  { label: 'Terms', value: formatStat(activeIndex.value?.termCount) },
  { label: 'Fields', value: formatStat(activeIndex.value?.fields.length) },
  { label: 'Tokenizer', value: activeIndex.value?.tokenizer ?? '-' },
  { label: 'Hits', value: formatStat(hits.value.length) },
]);

const querySummary = computed(() => {
  if (!activeIndex.value) return 'No fulltext index selected.';
  const query = queryText.value.trim();
  if (!query) return 'Query text empty.';
  return `${activeIndexLabel.value} · ${effectiveField.value} · ${mode.value}/${queryKind.value}`;
});

const canSearch = computed(() =>
  Boolean(props.targetDb && activeIndex.value && queryText.value.trim()));

const highlightTerms = computed(() => {
  const source = searchTokens.value.length > 0 ? searchTokens.value : extractQueryTerms(queryText.value);
  return [...new Set(source.map((term) => term.trim()).filter((term) => term.length > 0))]
    .sort((a, b) => b.length - a.length);
});

const hitRows = computed<HitRow[]>(() =>
  hits.value.map((hit, index) => mapHitRow(hit, index)));

const pageCount = computed(() =>
  Math.max(1, Math.ceil(hitRows.value.length / pageSize.value)));

const pagedRows = computed(() => {
  const start = (page.value - 1) * pageSize.value;
  return hitRows.value.slice(start, start + pageSize.value);
});

const selectedHit = computed(() =>
  hitRows.value.find((row) => row.documentId === selectedId.value) ?? hitRows.value[0] ?? null);

const pageSummary = computed(() => {
  if (hitRows.value.length === 0) return 'No hits in the current result.';
  const start = (page.value - 1) * pageSize.value + 1;
  const end = Math.min(hitRows.value.length, start + pageSize.value - 1);
  return `${start}-${end} of ${hitRows.value.length} hits · page ${page.value}/${pageCount.value}`;
});

const resultSummary = computed(() => {
  if (!latestResult.value) return querySummary.value;
  if (latestResult.value.error) return latestResult.value.error.message;
  if (latestResult.value.end) return `${latestResult.value.end.rowCount} rows · ${latestResult.value.end.elapsedMs.toFixed(2)} ms`;
  return 'Ready';
});

const hitColumns = computed<DataTableColumns<HitRow>>(() => [
  {
    title: '#',
    key: 'rank',
    width: 58,
    render: (row) => h('button', {
      type: 'button',
      class: ['fulltext-rank-button', row.documentId === selectedHit.value?.documentId ? 'is-active' : ''],
      onClick: () => { selectedId.value = row.documentId; },
    }, row.rank.toString()),
  },
  {
    title: 'Document',
    key: 'documentId',
    width: 170,
    ellipsis: { tooltip: true },
    render: (row) => h('button', {
      type: 'button',
      class: ['fulltext-doc-button', row.documentId === selectedHit.value?.documentId ? 'is-active' : ''],
      onClick: () => { selectedId.value = row.documentId; },
    }, row.documentId),
  },
  {
    title: 'Score',
    key: 'score',
    width: 116,
    sorter: 'default',
    render: (row) => h('code', formatScore(row.score)),
  },
  {
    title: 'Field',
    key: 'field',
    width: 132,
    ellipsis: { tooltip: true },
    render: (row) => h('code', row.fieldName),
  },
  {
    title: 'Highlight',
    key: 'snippet',
    minWidth: 320,
    render: (row) => h('span', { class: 'fulltext-snippet-cell' }, renderHighlightParts(row.snippetParts)),
  },
]);

function indexKey(index: FullTextIndexStat): string {
  return `fulltext:${index.collection}:${index.name}`;
}

function selectIndex(index: FullTextIndexStat): void {
  emit('selectIndex', index);
}

async function runSearch(): Promise<void> {
  if (!canSearch.value || !activeIndex.value) return;
  searching.value = true;
  errorMsg.value = '';
  const idx = activeIndex.value;
  const started = performance.now();
  const command = buildCommand(idx);
  try {
    const response = await searchFullTextPreview(auth.api, props.targetDb, {
      collection: idx.collection,
      index: idx.name,
      field: effectiveField.value,
      query: queryText.value.trim(),
      topK: topK.value ?? 20,
      mode: mode.value,
      queryKind: queryKind.value,
    });
    hits.value = Array.isArray(response.hits) ? response.hits : [];
    page.value = 1;
    selectedId.value = hits.value[0]?.documentId ?? '';
    await loadHitDocuments(idx.collection, hits.value);
    await analyzeQueryForHighlight(idx.tokenizer);
    const elapsed = performance.now() - started;
    latestCommand.value = command;
    latestResult.value = resultFromHits(hitRows.value, elapsed);
    ranOnce.value = true;
    recordHistory('success', 'FullText search preview', 'search', command, `${hits.value.length} hits`, hits.value.length, -1, elapsed);
  } catch (error) {
    const elapsed = performance.now() - started;
    const msg = errorToMessage(error, '全文检索失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult('fulltext_error', msg);
    ranOnce.value = true;
    recordHistory('error', 'FullText search preview', 'search', command, msg, 0, -1, elapsed);
  } finally {
    searching.value = false;
  }
}

async function loadHitDocuments(collection: string, items: FullTextSearchPreviewHit[]): Promise<void> {
  documentsById.value = {};
  const ids = [...new Set(items.map((hit) => hit.documentId).filter(Boolean))];
  if (ids.length === 0) return;
  const response = await findDocuments(auth.api, props.targetDb, collection, {
    ids,
    limit: Math.min(ids.length, 1000),
  });
  const next: Record<string, DocumentItemResponse> = {};
  for (const item of response.documents ?? []) {
    next[item.id] = item;
  }
  documentsById.value = next;
}

async function analyzeQueryForHighlight(tokenizer: string): Promise<void> {
  try {
    const response = await analyzeFullText(auth.api, props.targetDb, {
      tokenizer,
      text: queryText.value.trim(),
    });
    searchTokens.value = response.tokens.map((token) => token.text);
  } catch {
    searchTokens.value = extractQueryTerms(queryText.value);
  }
}

async function runAnalyze(useQuery: boolean): Promise<void> {
  const text = useQuery ? queryText.value.trim() : analyzeText.value;
  if (!text) {
    analyzeTokens.value = [];
    return;
  }
  if (useQuery) analyzeText.value = text;
  analyzing.value = true;
  errorMsg.value = '';
  try {
    const response = await analyzeFullText(auth.api, props.targetDb, {
      tokenizer: analyzeTokenizer.value,
      text,
    });
    analyzeTokens.value = Array.isArray(response.tokens) ? response.tokens : [];
  } catch (error) {
    errorMsg.value = errorToMessage(error, '分词预览失败');
  } finally {
    analyzing.value = false;
  }
}

function applyBuilder(kind: FullTextQueryKind): void {
  const text = builderText.value.trim();
  if (!text) return;
  queryText.value = text;
  queryKind.value = kind;
  if (kind === 'phrase') mode.value = 'exact';
}

function applyFuzzyBuilder(): void {
  const text = builderText.value.trim();
  if (!text) return;
  queryText.value = text;
  queryKind.value = 'all';
  mode.value = 'fuzzy';
}

function stageRebuild(): void {
  const idx = activeIndex.value;
  if (!idx) return;
  pendingRebuild.value = idx;
  previewPlan.value = createWriteApprovalPlan({
    id: `fulltext_rebuild_${props.targetDb}_${idx.collection}_${idx.name}_${Date.now().toString(36)}`,
    title: 'FullText index rebuild',
    target: `${props.targetDb}.${idx.collection}.${idx.name}`,
    items: [{
      id: `rebuild_${idx.collection}_${idx.name}`,
      command: `rebuild_index document_fulltext ${idx.collection}.${idx.name}`,
      severity: 'write',
      label: 'Derived index rebuild',
      detail: 'Rebuilds the fulltext derived index from document primary data.',
    }],
  });
}

async function confirmRebuild(): Promise<void> {
  const idx = pendingRebuild.value;
  if (!idx) return;
  confirmBusy.value = true;
  errorMsg.value = '';
  const started = performance.now();
  const command = `rebuild_index document_fulltext ${idx.collection}.${idx.name}`;
  try {
    const result = await runMaintenance(auth.api, props.targetDb, {
      operation: 'rebuild_index',
      targetModel: 'document_fulltext',
      targetOwner: idx.collection,
      targetName: idx.name,
    });
    const elapsed = performance.now() - started;
    latestCommand.value = command;
    latestResult.value = resultFromMaintenance(result, elapsed);
    ranOnce.value = true;
    previewPlan.value = null;
    pendingRebuild.value = null;
    recordHistory('success', 'FullText index rebuild', 'rebuild', command, result.message, 0, result.index?.documentCount ?? 0, elapsed);
    message.success(result.index?.documentCount != null
      ? `Rebuilt ${result.index.documentCount} documents.`
      : result.message);
    emit('refreshSchema');
  } catch (error) {
    const elapsed = performance.now() - started;
    const msg = errorToMessage(error, '全文索引重建失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult('fulltext_rebuild_error', msg);
    ranOnce.value = true;
    recordHistory('error', 'FullText index rebuild', 'rebuild', command, msg, 0, 0, elapsed);
  } finally {
    confirmBusy.value = false;
  }
}

function clearPreviewPlan(): void {
  previewPlan.value = null;
  pendingRebuild.value = null;
}

function openHistoryEntry(entry: WorkbenchHistoryEntry): void {
  latestCommand.value = entry.command;
}

function rowKey(row: HitRow): string {
  return row.key;
}

function mapHitRow(hit: FullTextSearchPreviewHit, index: number): HitRow {
  const doc = documentsById.value[hit.documentId];
  const rawJson = doc ? formatDocument(doc.document) : '';
  const fieldText = doc ? extractFieldText(doc.document, effectiveField.value) : '';
  const snippet = buildSnippet(fieldText || rawJson, highlightTerms.value);
  return {
    key: `${hit.documentId}:${index}`,
    rank: index + 1,
    documentId: hit.documentId,
    score: hit.score,
    fieldName: effectiveField.value,
    fieldText,
    snippetText: snippet.text,
    snippetParts: snippet.parts,
    rawJson: rawJson || '(document not loaded)',
    version: doc?.version,
    raw: hit,
  };
}

function renderHighlightParts(parts: HighlightPart[]) {
  return parts.map((part) => part.hit ? h('mark', { class: 'fulltext-mark' }, part.text) : part.text);
}

function buildSnippet(text: string, terms: string[]): { text: string; parts: HighlightPart[] } {
  const normalized = text.replace(/\s+/g, ' ').trim();
  if (!normalized) return { text: '-', parts: [{ text: '-', hit: false }] };
  const lower = normalized.toLowerCase();
  const lowerTerms = terms.map((term) => term.toLowerCase()).filter(Boolean);
  const first = lowerTerms.reduce((best, term) => {
    const index = lower.indexOf(term);
    return index >= 0 && index < best ? index : best;
  }, Number.POSITIVE_INFINITY);
  const center = Number.isFinite(first) ? first : 0;
  const start = Math.max(0, center - 90);
  const end = Math.min(normalized.length, start + 240);
  const snippet = `${start > 0 ? '...' : ''}${normalized.slice(start, end)}${end < normalized.length ? '...' : ''}`;
  return {
    text: snippet,
    parts: highlightText(snippet, lowerTerms),
  };
}

function highlightText(text: string, lowerTerms: string[]): HighlightPart[] {
  const ranges: Array<{ start: number; end: number }> = [];
  const lower = text.toLowerCase();
  for (const term of lowerTerms) {
    if (!term) continue;
    let start = lower.indexOf(term);
    while (start >= 0) {
      ranges.push({ start, end: start + term.length });
      start = lower.indexOf(term, start + Math.max(1, term.length));
    }
  }
  if (ranges.length === 0) return [{ text, hit: false }];

  ranges.sort((a, b) => a.start - b.start || b.end - a.end);
  const merged: Array<{ start: number; end: number }> = [];
  for (const range of ranges) {
    const last = merged[merged.length - 1];
    if (last && range.start <= last.end) {
      last.end = Math.max(last.end, range.end);
    } else {
      merged.push({ ...range });
    }
  }

  const parts: HighlightPart[] = [];
  let cursor = 0;
  for (const range of merged) {
    if (range.start > cursor) parts.push({ text: text.slice(cursor, range.start), hit: false });
    parts.push({ text: text.slice(range.start, range.end), hit: true });
    cursor = range.end;
  }
  if (cursor < text.length) parts.push({ text: text.slice(cursor), hit: false });
  return parts;
}

function extractQueryTerms(text: string): string[] {
  return text
    .replace(/\b(AND|OR|NOT)\b/gi, ' ')
    .replace(/["'()]/g, ' ')
    .split(/[^\p{L}\p{N}_]+/u)
    .map((term) => term.trim())
    .filter(Boolean);
}

function extractFieldText(document: unknown, selectedField: string): string {
  if (selectedField === '*' || selectedField === 'document' || selectedField === 'json') {
    return formatDocument(document);
  }
  const value = readJsonPath(document, selectedField);
  return value === undefined ? formatDocument(document) : formatDocument(value);
}

function readJsonPath(source: unknown, path: string): unknown {
  if (!path.startsWith('$.')) return undefined;
  const parts = path.slice(2).split('.').filter(Boolean);
  let current: unknown = source;
  for (const part of parts) {
    const key = part.replace(/^\['?/, '').replace(/'?\]$/, '');
    if (current && typeof current === 'object' && key in current) {
      current = (current as Record<string, unknown>)[key];
    } else {
      return undefined;
    }
  }
  return current;
}

function formatDocument(value: unknown): string {
  if (typeof value === 'string') return value;
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value ?? '');
  }
}

function resultFromHits(rows: HitRow[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['rank', 'document_id', 'score', 'field', 'snippet'],
    rows: rows.map((row) => [row.rank, row.documentId, row.score, row.fieldName, row.snippetText]),
    end: {
      type: 'end',
      rowCount: rows.length,
      recordsAffected: -1,
      elapsedMs,
    },
    error: null,
    hasColumns: true,
  };
}

function resultFromMaintenance(result: MaintenanceResponse, elapsedMs: number): SqlResultSet {
  return {
    columns: ['operation', 'status', 'success', 'message', 'documents'],
    rows: [[result.operation, result.status, result.success, result.message, result.index?.documentCount ?? null]],
    end: {
      type: 'end',
      rowCount: 1,
      recordsAffected: result.index?.documentCount ?? -1,
      elapsedMs,
    },
    error: null,
    hasColumns: true,
  };
}

function errorResult(code: string, messageText: string): SqlResultSet {
  return {
    columns: [],
    rows: [],
    end: null,
    error: { type: 'error', code, message: messageText },
    hasColumns: false,
  };
}

function buildCommand(index: FullTextIndexStat): string {
  const top = topK.value ?? 20;
  const fieldArg = effectiveField.value === '*' ? '*' : quote(effectiveField.value);
  return [
    `SELECT id, score, document`,
    `FROM ${formatSqlIdentifier(index.collection)}`,
    `WHERE match(${formatSqlIdentifier(index.name)}, ${fieldArg}, ${quote(queryText.value.trim())}, ${top}, ${quote(mode.value)})`,
    `-- queryKind: ${queryKind.value}`,
  ].join('\n');
}

function recordHistory(
  status: 'success' | 'error',
  title: string,
  action: string,
  command: string,
  summary: string,
  rowCount: number,
  recordsAffected: number,
  elapsedMs: number,
): void {
  history.record({
    kind: action === 'search' ? 'query' : 'operation',
    status,
    title,
    target: activeIndexLabel.value,
    database: props.targetDb,
    connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name,
    model: 'fulltext',
    action,
    command,
    summary,
    rowCount,
    recordsAffected,
    elapsedMs,
  });
}

async function copyText(text: string, success: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    message.success(success);
  } catch {
    message.warning(text);
  }
}

function formatScore(value?: number | null): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '-';
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
  field.value = '*';
  analyzeTokenizer.value = index?.tokenizer || 'unicode';
  hits.value = [];
  documentsById.value = {};
  latestResult.value = null;
  ranOnce.value = false;
}, { immediate: true });

watch(queryKind, (kind) => {
  if (kind === 'phrase' && mode.value === 'fuzzy') {
    mode.value = 'exact';
  }
});

watch(mode, (value) => {
  if (value === 'fuzzy' && queryKind.value === 'phrase') {
    queryKind.value = 'all';
  }
});

watch([hitRows, pageSize], () => {
  if (page.value > pageCount.value) page.value = pageCount.value;
});

watch(() => props.targetDb, () => {
  hits.value = [];
  documentsById.value = {};
  latestResult.value = null;
  ranOnce.value = false;
});
</script>

<style scoped>
.fulltext-workbench {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.fulltext-toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbf8ff;
}

.fulltext-toolbar__identity {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.fulltext-toolbar__title {
  color: var(--sndb-ink-strong);
  font-size: 15px;
  font-weight: 800;
}

.fulltext-toolbar__meta,
.fulltext-panel-head__meta {
  font-size: 12px;
}

.fulltext-toolbar__actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.fulltext-toolbar__index {
  width: 230px;
}

.fulltext-toolbar__field {
  width: 180px;
}

.fulltext-toolbar__topk {
  width: 86px;
}

.fulltext-alert {
  margin: 10px 12px 0;
}

.fulltext-stats {
  display: grid;
  grid-template-columns: repeat(6, minmax(110px, 1fr));
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.fulltext-stat {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
  padding: 9px 12px;
  border-right: 1px solid rgba(15, 23, 42, 0.08);
}

.fulltext-stat span,
.fulltext-param-strip small,
.fulltext-token-list small {
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.fulltext-stat strong,
.fulltext-param-strip strong {
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 15px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.fulltext-body {
  display: grid;
  flex: 1;
  min-height: 430px;
  grid-template-columns: 250px minmax(440px, 1fr) 360px;
  min-width: 0;
  overflow: hidden;
}

.fulltext-body.is-analyzer {
  grid-template-columns: minmax(460px, 760px);
  justify-content: center;
  padding: 20px;
  overflow: auto;
  background: var(--sndb-surface);
}

.fulltext-body.is-index {
  grid-template-columns: 300px minmax(420px, 720px);
  justify-content: center;
  padding: 20px;
  overflow: auto;
  background: var(--sndb-surface);
}

.fulltext-body.is-analyzer .fulltext-inspector,
.fulltext-body.is-index .fulltext-indexes,
.fulltext-body.is-index .fulltext-inspector {
  border: 1px solid var(--sndb-border);
  background: #fff;
}

.fulltext-index-detail {
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 16px;
}

.fulltext-index-detail dl {
  display: grid;
  gap: 0;
  margin: 0;
  border: 1px solid var(--sndb-border);
}

.fulltext-index-detail dl div {
  display: grid;
  grid-template-columns: 120px minmax(0, 1fr);
  gap: 12px;
  padding: 10px 12px;
  border-bottom: 1px solid var(--sndb-border);
}

.fulltext-index-detail dl div:last-child {
  border-bottom: 0;
}

.fulltext-index-detail dt {
  color: var(--sndb-ink-muted);
}

.fulltext-index-detail dd {
  margin: 0;
  font-family: "Cascadia Code", Consolas, monospace;
}

.fulltext-indexes,
.fulltext-search-panel,
.fulltext-inspector {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.fulltext-indexes {
  border-right: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfcfe;
}

.fulltext-inspector {
  border-left: 1px solid rgba(15, 23, 42, 0.08);
  background: #fffdfa;
}

.fulltext-panel-head {
  display: flex;
  flex: 0 0 auto;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.fulltext-panel-head--grid {
  align-items: center;
}

.fulltext-panel-head__title,
.fulltext-section-title {
  display: block;
  color: var(--sndb-ink-strong);
  font-weight: 800;
}

.fulltext-index-filter {
  flex: 0 0 auto;
  margin: 8px;
  width: calc(100% - 16px);
}

.fulltext-index-list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-height: 0;
  overflow: auto;
  padding: 0 8px 8px;
}

.fulltext-index-card,
.fulltext-rank-button,
.fulltext-doc-button {
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.fulltext-index-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  width: 100%;
  min-width: 0;
  padding: 8px;
  border-left: 2px solid rgba(131, 86, 210, 0.45);
  border-radius: 6px;
  text-align: left;
}

.fulltext-index-card:hover,
.fulltext-index-card.is-active,
.fulltext-rank-button:hover,
.fulltext-rank-button.is-active,
.fulltext-doc-button:hover,
.fulltext-doc-button.is-active {
  background: rgba(131, 86, 210, 0.09);
}

.fulltext-index-card.is-active {
  border-left-color: rgba(131, 86, 210, 0.9);
}

.fulltext-index-card span {
  width: 100%;
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.fulltext-index-card small {
  color: var(--sndb-ink-soft);
  font-size: 11px;
}

.fulltext-query-editor {
  display: flex;
  flex: 0 0 auto;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.fulltext-builder {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 8px;
  align-items: center;
}

.fulltext-mode-select,
.fulltext-kind-select {
  width: 118px;
}

.fulltext-param-strip {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 8px;
}

.fulltext-param-strip span {
  min-width: 0;
  padding: 7px 8px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fbfcfe;
}

.fulltext-param-strip small,
.fulltext-param-strip strong {
  display: block;
}

.fulltext-grid {
  flex: 1;
  min-height: 0;
}

.fulltext-grid :deep(.n-data-table-base-table-body) {
  min-height: 260px;
}

.fulltext-rank-button {
  min-width: 30px;
  padding: 2px 5px;
  border-radius: 4px;
  color: #6f49b8;
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  font-weight: 800;
}

.fulltext-doc-button {
  max-width: 100%;
  overflow: hidden;
  padding: 2px 5px;
  border-radius: 4px;
  color: var(--sndb-ink-strong);
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  font-weight: 700;
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.fulltext-snippet-cell {
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  color: #345;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.fulltext-mark,
.fulltext-snippet mark {
  border-radius: 3px;
  background: rgba(247, 196, 83, 0.55);
  color: #1f2937;
  font-weight: 800;
}

.fulltext-pager {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.fulltext-page-size {
  width: 104px;
}

.fulltext-detail-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 9px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.fulltext-detail-strip span {
  padding: 2px 7px;
  border-radius: 999px;
  background: rgba(131, 86, 210, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
}

.fulltext-section,
.fulltext-analyzer {
  padding: 10px 12px 0;
}

.fulltext-section-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 6px;
}

.fulltext-section-title--standalone {
  margin-bottom: 8px;
}

.fulltext-snippet {
  min-height: 54px;
  margin: 0;
  padding: 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
  color: #24384f;
  font-size: 13px;
  line-height: 1.55;
}

.fulltext-document pre {
  max-height: 210px;
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

.fulltext-analyzer {
  display: flex;
  flex-direction: column;
  gap: 8px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  margin-top: 10px;
  padding-bottom: 12px;
}

.fulltext-analyzer-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto auto;
  gap: 8px;
  align-items: center;
}

.fulltext-token-list {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  min-height: 40px;
}

.fulltext-token-list span {
  display: inline-flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
  padding: 5px 7px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fbfcfe;
}

.fulltext-token-list strong {
  color: var(--sndb-ink-strong);
  font-size: 12px;
}

.fulltext-result {
  flex: 0 0 240px;
  min-height: 220px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

@media (max-width: 1360px) {
  .fulltext-body {
    grid-template-columns: 230px minmax(420px, 1fr);
  }

  .fulltext-inspector {
    grid-column: 1 / -1;
    border-top: 1px solid rgba(15, 23, 42, 0.08);
    border-left: 0;
  }
}

@media (max-width: 980px) {
  .fulltext-toolbar,
  .fulltext-panel-head--grid,
  .fulltext-pager {
    flex-direction: column;
    align-items: stretch;
  }

  .fulltext-body {
    grid-template-columns: 1fr;
    overflow: visible;
  }

  .fulltext-indexes,
  .fulltext-inspector {
    border-right: 0;
    border-left: 0;
  }

  .fulltext-stats,
  .fulltext-param-strip,
  .fulltext-builder,
  .fulltext-analyzer-row {
    grid-template-columns: 1fr;
  }

  .fulltext-toolbar__index,
  .fulltext-toolbar__field,
  .fulltext-toolbar__topk,
  .fulltext-mode-select,
  .fulltext-kind-select,
  .fulltext-page-size {
    width: 100%;
  }
}
</style>
